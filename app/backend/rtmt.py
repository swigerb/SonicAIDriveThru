import asyncio
import json
import logging
import re
from enum import Enum
from typing import Any

import aiohttp
from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import DefaultAzureCredential, get_bearer_token_provider

from order_state import SessionIdentifiers, order_state_singleton

logger = logging.getLogger("sonic-drive-in")

__all__ = ["RTMiddleTier", "RTToolCall", "Tool", "ToolResult", "ToolResultDirection"]

# High-frequency message types that are never modified by the middleware.
# Returning early for these avoids match/case overhead on the audio hot-path.
_PASSTHROUGH_SERVER_TYPES = frozenset({
    "response.audio.delta",
    "response.audio.done",
    "response.audio_transcript.delta",
    "response.audio_transcript.done",
    "response.text.delta",
    "response.text.done",
    "response.content_part.added",
    "response.content_part.done",
    "input_audio_buffer.speech_started",
    "input_audio_buffer.speech_stopped",
    "input_audio_buffer.committed",
    "error",
    "rate_limits.updated",
})

# Client messages that never need middleware modification — pass straight through.
# input_audio_buffer.append is BY FAR the most frequent client message (every ~100ms).
_PASSTHROUGH_CLIENT_TYPES = frozenset({
    "input_audio_buffer.append",
    "input_audio_buffer.clear",
    "input_audio_buffer.commit",
})

# Regex to extract "type":"..." from raw JSON without full parse.
# This lets us skip json.loads entirely for passthrough messages on the hot path.
_TYPE_RE = re.compile(r'"type"\s*:\s*"([^"]+)"')

# Pre-serialized static payloads to avoid repeated json.dumps on every round-trip
_RESPONSE_CREATE_MSG = json.dumps({"type": "response.create"})

# Pre-serialized message to flush echoed audio from OpenAI's input buffer
_INPUT_AUDIO_CLEAR_MSG = json.dumps({"type": "input_audio_buffer.clear"})

# Cooldown period (seconds) after AI audio ends before accepting user audio.
# Covers timing gap between server sending last audio delta and speakers finishing.
_ECHO_COOLDOWN_SEC = 0.5

# Fast substring markers for echo suppression (avoids regex/JSON parse overhead)
_MARKER_AUDIO_APPEND = '"input_audio_buffer.append"'
_MARKER_AUDIO_DELTA = '"response.audio.delta"'
_MARKER_AUDIO_DONE = '"response.audio.done"'
_MARKER_SPEECH_STARTED = '"input_audio_buffer.speech_started"'

# Connection tuning constants
_WS_HEARTBEAT_SEC = 15.0
_WS_CONNECT_TIMEOUT = aiohttp.ClientTimeout(total=30, connect=10)

# Pre-serialized greeting to avoid json.dumps at connection time.
# IMPORTANT: This must be a direct command — NOT "how would you greet" or the AI
# will generate meta-commentary about greeting instead of actually greeting.
_GREETING_MSG = json.dumps({
    "type": "conversation.item.create",
    "item": {
        "type": "message",
        "role": "user",
        "content": [
            {"type": "input_text", "text": "A guest just pulled up to the drive-thru speaker. Greet them now — be brief and warm. Say ONLY the greeting itself. Do NOT explain how to greet or offer multiple options."}
        ]
    }
})

class ToolResultDirection(Enum):
    TO_SERVER = 1
    TO_CLIENT = 2
    TO_BOTH = 3

class ToolResult:
    __slots__ = ("text", "destination", "_client_text")

    def __init__(self, text: str, destination: ToolResultDirection, client_text: str | None = None):
        self.text = text
        self.destination = destination
        self._client_text = client_text

    def to_text(self) -> str:
        if self.text is None:
            return ""
        return self.text if isinstance(self.text, str) else json.dumps(self.text)

    def to_client_text(self) -> str:
        """Text for client display. Falls back to to_text() if no separate client payload."""
        if self._client_text is not None:
            return self._client_text
        return self.to_text()

class Tool:
    __slots__ = ("target", "schema")

    def __init__(self, target: Any, schema: Any):
        self.target = target
        self.schema = schema

class RTToolCall:
    __slots__ = ("tool_call_id", "previous_id")

    def __init__(self, tool_call_id: str, previous_id: str):
        self.tool_call_id = tool_call_id
        self.previous_id = previous_id

class RTMiddleTier:
    endpoint: str
    deployment: str
    key: str | None = None
    
    # Tools are server-side only for now, though the case could be made for client-side tools
    # in addition to server-side tools that are invisible to the client
    tools: dict[str, Tool]

    # Server-enforced configuration, if set, these will override the client's configuration
    # Typically at least the model name and system message will be set by the server
    model: str | None = None
    system_message: str | None = None
    temperature: float | None = None
    max_tokens: int | None = None
    disable_audio: bool | None = None
    voice_choice: str | None = None
    api_version: str = "2024-10-01-preview"

    def __init__(self, endpoint: str, deployment: str, credentials: AzureKeyCredential | DefaultAzureCredential, voice_choice: str | None = None):
        self.endpoint = endpoint
        self.deployment = deployment
        self.voice_choice = voice_choice
        self.tools = {}
        self._token_provider = None
        self._session_map: dict[web.WebSocketResponse, str] = {}
        self._sent_greeting: set[str] = set()
        if voice_choice is not None:
            logger.info("Realtime voice choice set to %s", voice_choice)
        if isinstance(credentials, AzureKeyCredential):
            self.key = credentials.key
        else:
            self._token_provider = get_bearer_token_provider(credentials, "https://cognitiveservices.azure.com/.default")
            self._token_provider() # Warm up during startup so we have a token cached when the first request arrives

    async def _emit_session_identifiers(
        self,
        client_ws: web.WebSocketResponse,
        event_type: str,
        identifiers: SessionIdentifiers | None,
    ) -> None:
        if identifiers is None:
            return
        await client_ws.send_json(
            {
                "type": event_type,
                "sessionToken": identifiers.session_token,
                "roundTripIndex": identifiers.round_trip_index,
                "roundTripToken": identifiers.round_trip_token,
            }
        )

    async def _process_message_to_client(self, msg: str, client_ws: web.WebSocketResponse, server_ws: web.WebSocketResponse, tools_pending: dict[str, RTToolCall]) -> str | None:
        data = msg.data

        # FAST PATH: extract type via regex without full JSON parse.
        # Audio deltas are ~95% of server messages — avoid json.loads entirely.
        m = _TYPE_RE.search(data)
        if m is not None and m.group(1) in _PASSTHROUGH_SERVER_TYPES:
            return data

        message = json.loads(data)
        msg_type = message.get("type", "")

        updated_message = data
        session_id = self._session_map.get(client_ws)
        if message is not None:
            match msg_type:
                case "session.created":
                    session = message["session"]
                    # Hide the instructions, tools and max tokens from clients, if we ever allow client-side 
                    # tools, this will need updating
                    session["instructions"] = ""
                    session["tools"] = []
                    session["voice"] = self.voice_choice
                    session["tool_choice"] = "none"
                    session["max_response_output_tokens"] = None
                    updated_message = json.dumps(message)
                    if session_id is not None:
                        identifiers = order_state_singleton.get_session_identifiers(session_id)
                        await self._emit_session_identifiers(client_ws, "extension.session_metadata", identifiers)

                case "response.output_item.added":
                    if "item" in message and message["item"]["type"] == "function_call":
                        # Fallback registration — ensures tools_pending is populated even
                        # if conversation.item.created fires late or is skipped by newer
                        # API versions.  conversation.item.created overwrites with the
                        # correct previous_item_id when it arrives.
                        item = message["item"]
                        call_id = item.get("call_id")
                        if call_id and call_id not in tools_pending:
                            logger.debug("Tool call %s pre-registered via output_item.added", call_id)
                            tools_pending[call_id] = RTToolCall(call_id, "")
                        updated_message = None

                case "conversation.item.created":
                    if "item" in message and message["item"]["type"] == "function_call":
                        item = message["item"]
                        # Always overwrite — may upgrade fallback from output_item.added
                        # with the correct previous_item_id
                        tools_pending[item["call_id"]] = RTToolCall(item["call_id"], message["previous_item_id"])
                        updated_message = None
                    elif "item" in message and message["item"]["type"] == "function_call_output":
                        updated_message = None

                case "response.function_call_arguments.delta":
                    updated_message = None
                
                case "response.function_call_arguments.done":
                    updated_message = None

                case "response.output_item.done":
                    if "item" in message and message["item"]["type"] == "function_call":
                        item = message["item"]
                        tool_call = tools_pending.get(item["call_id"])
                        if tool_call is None:
                            logger.warning("Tool call %s not found in pending tools", item["call_id"])
                            updated_message = None
                        else:
                            tool = self.tools.get(item["name"])
                            if tool is None:
                                logger.error("Unknown tool requested: %s", item["name"])
                                updated_message = None
                            else:
                                args = json.loads(item["arguments"])
                                logger.info("Executing tool '%s' with args %s (session=%s)", item["name"], args, session_id)
                                if item["name"] in ("update_order", "get_order"):
                                    result = await tool.target(args, session_id)
                                else:
                                    result = await tool.target(args)
                                logger.info("Tool '%s' result direction=%s", item["name"], result.destination)
                                await server_ws.send_json({
                                    "type": "conversation.item.create",
                                    "item": {
                                        "type": "function_call_output",
                                        "call_id": item["call_id"],
                                        "output": result.to_text() if result.destination in (ToolResultDirection.TO_SERVER, ToolResultDirection.TO_BOTH) else ""
                                    }
                                })
                                if result.destination in (ToolResultDirection.TO_CLIENT, ToolResultDirection.TO_BOTH):
                                    await client_ws.send_json({
                                        "type": "extension.middle_tier_tool_response",
                                        "previous_item_id": tool_call.previous_id,
                                        "tool_name": item["name"],
                                        "tool_result": result.to_client_text()
                                    })
                                updated_message = None

                case "response.done":
                    if tools_pending:
                        tools_pending.clear()
                        await server_ws.send_str(_RESPONSE_CREATE_MSG)
                    if "response" in message:
                        output = message["response"]["output"]
                        filtered = [o for o in output if o.get("type") != "function_call"]
                        if len(filtered) != len(output):
                            message["response"]["output"] = filtered
                            updated_message = json.dumps(message)
                    if session_id is not None:
                        identifiers = order_state_singleton.advance_round_trip(session_id)
                        await self._emit_session_identifiers(client_ws, "extension.round_trip_token", identifiers)

        return updated_message

    async def _process_message_to_server(self, msg: str, ws: web.WebSocketResponse) -> str | None:
        data = msg.data

        # FAST PATH: input_audio_buffer.append is the most frequent client message
        # (~10 per second). Skip JSON parse entirely — it never needs modification.
        m = _TYPE_RE.search(data)
        if m is not None and m.group(1) in _PASSTHROUGH_CLIENT_TYPES:
            return data

        message = json.loads(data)
        updated_message = data
        if message is not None:
            match message["type"]:
                case "session.update":
                    session = message["session"]
                    if self.system_message is not None:
                        session["instructions"] = self.system_message
                    if self.temperature is not None:
                        session["temperature"] = self.temperature
                    if self.max_tokens is not None:
                        session["max_response_output_tokens"] = self.max_tokens
                    if self.disable_audio is not None:
                        session["disable_audio"] = self.disable_audio
                    if self.voice_choice is not None:
                        session["voice"] = self.voice_choice
                    session["tool_choice"] = "auto" if len(self.tools) > 0 else "none"
                    session["tools"] = [tool.schema for tool in self.tools.values()]
                    logger.info("session.update: injected %d tools, tool_choice=%s", len(session["tools"]), session["tool_choice"])
                    updated_message = json.dumps(message)

        return updated_message

    async def _forward_messages(self, ws: web.WebSocketResponse):
        # Per-connection tool tracking — prevents cross-connection interference
        tools_pending: dict[str, RTToolCall] = {}

        # Echo suppression state — shared between client→server and server→client tasks.
        # Safe without locks: single-threaded asyncio event loop guarantees no concurrent mutation.
        ai_speaking = False
        cooldown_end = 0.0  # loop.time() value after which user audio is accepted again

        async with aiohttp.ClientSession(
            base_url=self.endpoint,
            timeout=_WS_CONNECT_TIMEOUT,
        ) as session:
            params = { "api-version": self.api_version, "deployment": self.deployment}
            headers = {}
            if "x-ms-client-request-id" in ws.headers:
                headers["x-ms-client-request-id"] = ws.headers["x-ms-client-request-id"]
            if self.key is not None:
                headers = { "api-key": self.key }
            else:
                headers = { "Authorization": f"Bearer {self._token_provider()}" } # NOTE: no async version of token provider, maybe refresh token on a timer?
            async with session.ws_connect(
                "/openai/realtime",
                headers=headers,
                params=params,
                heartbeat=_WS_HEARTBEAT_SEC,
            ) as target_ws:
                loop = asyncio.get_running_loop()
                session_id = self._session_map.get(ws)
                greeting_sent = session_id in self._sent_greeting

                async def send_greeting_once():
                    nonlocal greeting_sent, ai_speaking
                    if greeting_sent:
                        return
                    # Pre-set echo suppression: the AI will start speaking as soon as
                    # OpenAI processes response.create.  Without this, there's a gap
                    # between response.create and the first response.audio.delta where
                    # mic audio could leak through and cause an echo loop.
                    ai_speaking = True
                    # Flush any stale audio that arrived before session was configured
                    await target_ws.send_str(_INPUT_AUDIO_CLEAR_MSG)
                    await target_ws.send_str(_GREETING_MSG)
                    await target_ws.send_str(_RESPONSE_CREATE_MSG)
                    greeting_sent = True
                    if session_id is not None:
                        self._sent_greeting.add(session_id)

                async def from_client_to_server():
                    nonlocal ai_speaking, cooldown_end
                    async for msg in ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            # Echo suppression: drop mic audio while AI is speaking or cooling down.
                            # The AI's speaker output leaks into the mic and gets transcribed as
                            # phantom user input ("Peace.", "Thank you so much."), creating a
                            # self-conversation loop. Dropping audio here breaks that cycle.
                            if _MARKER_AUDIO_APPEND in msg.data:
                                if ai_speaking or loop.time() < cooldown_end:
                                    continue
                            # Forward client message FIRST — the initial session.update carries
                            # tools + system_message.  OpenAI must have them configured before
                            # the greeting's response.create triggers the first completion.
                            new_msg = await self._process_message_to_server(msg, ws)
                            if new_msg is not None:
                                await target_ws.send_str(new_msg)
                            if not greeting_sent:
                                await send_greeting_once()
                        elif msg.type == aiohttp.WSMsgType.ERROR:
                            logger.error("Client WebSocket error: %s", ws.exception())
                            break
                        elif msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSING, aiohttp.WSMsgType.CLOSED):
                            break
                    
                    # Means it is gracefully closed by the client then time to close the target_ws
                    if target_ws and not target_ws.closed:
                        logger.info("Closing OpenAI's realtime socket connection.")
                        await target_ws.close()
                        
                async def from_server_to_client():
                    nonlocal ai_speaking, cooldown_end
                    async for msg in target_ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            # Track AI speaking state for echo suppression.
                            # These substring checks are O(1)-fast (no JSON parse) and only
                            # trigger state transitions, not per-frame work.
                            data = msg.data
                            if _MARKER_AUDIO_DELTA in data:
                                if not ai_speaking:
                                    logger.debug("Echo suppression: AI speaking — suppressing user audio")
                                ai_speaking = True
                            elif _MARKER_AUDIO_DONE in data:
                                ai_speaking = False
                                cooldown_end = loop.time() + _ECHO_COOLDOWN_SEC
                                logger.debug("Echo suppression: AI audio done — cooldown %.1fs", _ECHO_COOLDOWN_SEC)
                                # Flush any echoed audio that leaked into OpenAI's buffer
                                await target_ws.send_str(_INPUT_AUDIO_CLEAR_MSG)
                            elif _MARKER_SPEECH_STARTED in data:
                                # Server VAD detected genuine barge-in — immediately accept audio
                                if ai_speaking:
                                    logger.debug("Echo suppression: barge-in detected — resuming user audio")
                                ai_speaking = False
                                cooldown_end = 0.0

                            new_msg = await self._process_message_to_client(msg, ws, target_ws, tools_pending)
                            if new_msg is not None:
                                await ws.send_str(new_msg)
                        elif msg.type == aiohttp.WSMsgType.ERROR:
                            logger.error("Server WebSocket error: %s", target_ws.exception())
                            break
                        elif msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSING, aiohttp.WSMsgType.CLOSED):
                            break

                try:
                    await asyncio.gather(from_client_to_server(), from_server_to_client())
                except ConnectionResetError:
                    # Ignore the errors resulting from the client disconnecting the socket
                    pass
                except Exception:
                    logger.exception("Unexpected error in WebSocket forwarding")
                finally:
                    if session_id is not None:
                        order_state_singleton.delete_session(session_id)
                    # Clean up the session map when the connection is closed
                    self._session_map.pop(ws, None)

    async def _websocket_handler(self, request: web.Request):
        ws = web.WebSocketResponse(
            heartbeat=_WS_HEARTBEAT_SEC,
            autoping=True,
            autoclose=True,
        )
        await ws.prepare(request)
        
        # Create a new session for each WebSocket connection
        session_id = order_state_singleton.create_session()
        self._session_map[ws] = session_id

        await self._forward_messages(ws)
        return ws
    
    def attach_to_app(self, app: web.Application, path: str) -> None:
        app.router.add_get(path, self._websocket_handler)
