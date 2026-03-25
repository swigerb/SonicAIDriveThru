import asyncio
import base64
import hashlib
import hmac
import json
import logging
import re
import time
from enum import Enum
from typing import Any

import aiohttp
from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import DefaultAzureCredential, get_bearer_token_provider

from config_loader import get_config
from order_state import order_state_singleton
from session_manager import SessionManager
from audio_pipeline import (
    EchoSuppressor,
    vlog as _vlog, vlogger,
    create_verbose_file_handler as _create_verbose_file_handler,
    remove_verbose_file_handler as _remove_verbose_file_handler,
    TYPE_RE as _TYPE_RE,
    RESPONSE_CREATE_MSG as _RESPONSE_CREATE_MSG,
    INPUT_AUDIO_CLEAR_MSG as _INPUT_AUDIO_CLEAR_MSG,
    ECHO_COOLDOWN_SEC as _ECHO_COOLDOWN_SEC,
    MARKER_AUDIO_APPEND as _MARKER_AUDIO_APPEND,
    MARKER_AUDIO_DELTA as _MARKER_AUDIO_DELTA,
    MARKER_AUDIO_DONE as _MARKER_AUDIO_DONE,
    MARKER_SPEECH_STARTED as _MARKER_SPEECH_STARTED,
    MARKER_SESSION_UPDATE as _MARKER_SESSION_UPDATE,
    MARKER_SESSION_UPDATED as _MARKER_SESSION_UPDATED,
    MARKER_RESPONSE_CANCEL as _MARKER_RESPONSE_CANCEL,
    MARKER_VERBOSE_LOGGING as _MARKER_VERBOSE_LOGGING,
    MARKER_LOG_TO_FILE as _MARKER_LOG_TO_FILE,
    _PASSTHROUGH_SERVER_TYPES,
    _PASSTHROUGH_CLIENT_TYPES,
    _VERBOSE_GLOBAL,
    _VERBOSE_RESULT_TRUNCATE,
)

logger = logging.getLogger("sonic-drive-in")

# Load centralized config
_config = get_config()
_conn_cfg = _config.get("connection", {})
_security_cfg = _config.get("security", {})

__all__ = ["RTMiddleTier", "RTToolCall", "Tool", "ToolResult", "ToolResultDirection"]

# Connection tuning constants
_WS_HEARTBEAT_SEC = _conn_cfg.get("ws_heartbeat_seconds", 15.0)
_WS_CONNECT_TIMEOUT = aiohttp.ClientTimeout(
    total=_conn_cfg.get("ws_connect_timeout_total", 30),
    connect=_conn_cfg.get("ws_connect_timeout_connect", 10),
)

# ── HMAC Session Token Utilities ──

def create_hmac_token(secret: bytes, expiry_seconds: int = 900) -> str:
    """Create an HMAC-signed session token with expiry."""
    payload = {"exp": int(time.time()) + expiry_seconds}
    payload_b64 = base64.urlsafe_b64encode(json.dumps(payload).encode()).decode()
    sig = hmac.new(secret, payload_b64.encode(), hashlib.sha256).hexdigest()
    return f"{payload_b64}.{sig}"


def validate_hmac_token(token: str, secret: bytes) -> bool:
    """Validate an HMAC session token (signature + expiry)."""
    if not token or "." not in token:
        return False
    try:
        payload_b64, sig = token.rsplit(".", 1)
        expected_sig = hmac.new(secret, payload_b64.encode(), hashlib.sha256).hexdigest()
        if not hmac.compare_digest(sig, expected_sig):
            return False
        payload = json.loads(base64.urlsafe_b64decode(payload_b64))
        return payload.get("exp", 0) > time.time()
    except Exception:
        return False


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

    def __init__(self, endpoint: str, deployment: str, credentials: AzureKeyCredential | DefaultAzureCredential, voice_choice: str | None = None, prompt_loader=None):
        self.endpoint = endpoint
        self.deployment = deployment
        self.voice_choice = voice_choice
        self.tools = {}
        self._token_provider = None
        self._cached_token: str | None = None
        self._token_refresh_task: asyncio.Task | None = None
        self._prompt_loader = prompt_loader
        self._sessions = SessionManager(prompt_loader=prompt_loader)
        self.app_secret: bytes = b""  # set by app.py at startup
        if voice_choice is not None:
            logger.info("Realtime voice choice set to %s", voice_choice)
        if isinstance(credentials, AzureKeyCredential):
            self.key = credentials.key
        else:
            self._token_provider = get_bearer_token_provider(credentials, "https://cognitiveservices.azure.com/.default")
            self._token_provider() # Warm up during startup so we have a token cached when the first request arrives

    def _get_auth_token(self) -> str:
        """Return the cached token, falling back to a synchronous call if needed."""
        if self._cached_token is not None:
            return self._cached_token
        if self._token_provider is not None:
            return self._token_provider()
        return ""

    async def _refresh_token_loop(self) -> None:
        """Background task: proactively refresh the Azure AD token every 5 minutes."""
        while True:
            try:
                loop = asyncio.get_event_loop()
                token = await loop.run_in_executor(None, self._token_provider)
                self._cached_token = token
                logger.debug("Azure AD token refreshed successfully")
            except Exception as e:
                logger.warning("Token refresh failed: %s", e)
            await asyncio.sleep(300)  # 5 minutes

    def start_background_tasks(self) -> None:
        """Start background tasks (token refresh, idle checker). Called once at app startup."""
        if self._token_provider is not None:
            self._token_refresh_task = asyncio.ensure_future(self._refresh_token_loop())
        self._sessions.start_idle_checker()

    def stop_background_tasks(self) -> None:
        """Cancel background tasks. Called on app shutdown."""
        if self._token_refresh_task and not self._token_refresh_task.done():
            self._token_refresh_task.cancel()
        self._sessions.stop_idle_checker()

    async def _process_message_to_client(self, msg: str, client_ws: web.WebSocketResponse, server_ws: web.WebSocketResponse, tools_pending: dict[str, RTToolCall], verbose: bool = False) -> str | None:
        data = msg.data

        # FAST PATH: extract type via regex without full JSON parse.
        # Audio deltas are ~95% of server messages — avoid json.loads entirely.
        m = _TYPE_RE.search(data)
        if m is not None and m.group(1) in _PASSTHROUGH_SERVER_TYPES:
            # Verbose: log passthrough types (skip audio delta data to avoid flooding)
            if verbose or _VERBOSE_GLOBAL:
                pt = m.group(1)
                if pt == "response.audio.delta":
                    _vlog(verbose, "─── [Server → Client] response.audio.delta (audio data) ───")
                elif pt == "response.audio_transcript.delta":
                    # Extract transcript snippet from raw JSON
                    td_match = re.search(r'"delta"\s*:\s*"([^"]{0,120})', data)
                    snippet = td_match.group(1) if td_match else ""
                    _vlog(verbose, '─── [AI → Client] response.audio_transcript.delta ───\n"%s"', snippet)
                elif pt == "response.audio_transcript.done":
                    td_match = re.search(r'"transcript"\s*:\s*"([^"]{0,200})', data)
                    snippet = td_match.group(1) if td_match else ""
                    _vlog(verbose, '─── [AI → Client] response.audio_transcript.done ───\n"%s"', snippet)
                elif pt == "input_audio_buffer.speech_started":
                    _vlog(verbose, "─── [Server] input_audio_buffer.speech_started ───")
                elif pt == "input_audio_buffer.speech_stopped":
                    _vlog(verbose, "─── [Server] input_audio_buffer.speech_stopped ───")
                else:
                    _vlog(verbose, "─── [Server → Client] %s ───", pt)
            return data

        message = json.loads(data)
        msg_type = message.get("type", "")

        updated_message = data
        session_id = self._sessions.get_session_id(client_ws)
        if message is not None:
            _vlog(verbose, "─── [Server → Client] %s ───", msg_type)
            match msg_type:
                case "error":
                    # Surface OpenAI errors (e.g. rejected session.update, malformed tool schemas)
                    # so they don't silently vanish into the client.
                    logger.error("OpenAI Realtime API error: %s", json.dumps(message, default=str)[:1000])
                    _vlog(verbose, "  ⚠ ERROR: %s", json.dumps(message, default=str)[:500])

                case "session.created":
                    session = message["session"]
                    _vlog(verbose, "  Session ID: %s", session.get("id", "?"))
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
                        await self._sessions.emit_session_identifiers(client_ws, "extension.session_metadata", identifiers)
                        _vlog(verbose, "─── [SESSION TOKEN] ───\n"
                                       "Token: %s\n"
                                       "Round Trip: #%d (token: %s)\n"
                                       "───────────────────────",
                              identifiers.session_token,
                              identifiers.round_trip_index,
                              identifiers.round_trip_token)

                case "response.output_item.added":
                    if "item" in message and message["item"]["type"] == "function_call":
                        # Fallback registration — ensures tools_pending is populated even
                        # if conversation.item.created fires late or is skipped by newer
                        # API versions.  conversation.item.created overwrites with the
                        # correct previous_item_id when it arrives.
                        item = message["item"]
                        call_id = item.get("call_id")
                        if call_id and call_id not in tools_pending:
                            logger.info("Tool call received: name=%s, call_id=%s", item.get("name"), call_id)
                            tools_pending[call_id] = RTToolCall(call_id, "")
                        _vlog(verbose, "  Tool call registered: %s (call_id=%s)", item.get("name"), item.get("call_id"))
                        updated_message = None

                case "conversation.item.created":
                    if "item" in message and message["item"]["type"] == "function_call":
                        item = message["item"]
                        # Always overwrite — may upgrade fallback from output_item.added
                        # with the correct previous_item_id
                        tools_pending[item["call_id"]] = RTToolCall(item["call_id"], message["previous_item_id"])
                        _vlog(verbose, "  Tool pending confirmed: call_id=%s, prev=%s", item["call_id"], message["previous_item_id"])
                        updated_message = None
                    elif "item" in message and message["item"]["type"] == "function_call_output":
                        updated_message = None
                    elif "item" in message and message["item"].get("role") == "assistant":
                        # Log AI conversation items (non-tool)
                        _vlog(verbose, "  AI conversation item created")

                case "response.function_call_arguments.delta":
                    updated_message = None
                
                case "response.function_call_arguments.done":
                    _vlog(verbose, "  Tool args complete: %s", message.get("arguments", "")[:200])
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
                                t0 = time.monotonic()
                                if item["name"] in ("update_order", "get_order", "reset_order"):
                                    result = await tool.target(args, session_id)
                                else:
                                    result = await tool.target(args)
                                elapsed_ms = (time.monotonic() - t0) * 1000
                                logger.info("Tool '%s' result direction=%s", item["name"], result.destination)

                                # ── Verbose: full tool call lifecycle ──
                                result_text = result.to_text()[:_VERBOSE_RESULT_TRUNCATE]
                                _vlog(verbose,
                                      "\n═══ [TOOL CALL] %s ═══\n"
                                      "Args: %s\n"
                                      "Result: %s\n"
                                      "Direction: %s\n"
                                      "Time: %.1fms\n"
                                      "═══════════════════════════",
                                      item["name"],
                                      json.dumps(args, indent=2),
                                      result_text,
                                      result.destination.name,
                                      elapsed_ms)

                                # Track tool call args + result in context window
                                ctx_monitor = self._sessions.get_context_monitor(session_id)
                                if ctx_monitor:
                                    ctx_monitor.add_content(item.get("arguments", ""))
                                    ctx_monitor.add_content(result.to_text())

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
                        fn_calls = [o for o in output if o.get("type") == "function_call"]
                        if fn_calls:
                            logger.info("Response contained %d tool call(s): %s",
                                        len(fn_calls), [o.get("name", "?") for o in fn_calls])
                        else:
                            out_types = [o.get("type", "?") for o in output]
                            logger.info("Response completed with NO tool calls (output types: %s)", out_types)
                        _vlog(verbose, "  Response done — output types: %s",
                              [o.get("type", "?") for o in output])
                        filtered = [o for o in output if o.get("type") != "function_call"]
                        if len(filtered) != len(output):
                            message["response"]["output"] = filtered
                            updated_message = json.dumps(message)
                    if session_id is not None:
                        identifiers = order_state_singleton.advance_round_trip(session_id)
                        await self._sessions.emit_session_identifiers(client_ws, "extension.round_trip_token", identifiers)
                        _vlog(verbose, "─── [ROUND TRIP] #%d ───\n"
                                       "Token: %s\n"
                                       "────────────────────────",
                              identifiers.round_trip_index,
                              identifiers.round_trip_token)
                    # Track context usage from response output
                    ctx_monitor = self._sessions.get_context_monitor(session_id)
                    if ctx_monitor and "response" in message:
                        for out_item in message["response"].get("output", []):
                            for content in out_item.get("content", []):
                                ctx_monitor.add_content(content.get("text", ""))
                                ctx_monitor.add_content(content.get("transcript", ""))

        return updated_message

    async def _process_message_to_server(self, msg: str, ws: web.WebSocketResponse, verbose: bool = False) -> str | None:
        data = msg.data

        # FAST PATH: input_audio_buffer.append is the most frequent client message
        # (~10 per second). Skip JSON parse entirely — it never needs modification.
        m = _TYPE_RE.search(data)
        if m is not None and m.group(1) in _PASSTHROUGH_CLIENT_TYPES:
            return data

        message = json.loads(data)
        msg_type = message.get("type", "")
        updated_message = data
        if message is not None:
            _vlog(verbose, "─── [Client → Server] %s ───", msg_type)
            match msg_type:
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
                    tool_names = [t.get("name", "?") for t in session["tools"]]
                    logger.info(
                        "session.update: injected %d tools %s, tool_choice=%s, max_tokens=%s",
                        len(session["tools"]), tool_names, session["tool_choice"],
                        session.get("max_response_output_tokens"),
                    )
                    _vlog(verbose, "  Injected %d tools: %s, tool_choice=%s",
                          len(session["tools"]), tool_names, session["tool_choice"])
                    updated_message = json.dumps(message)
                    # Track system message + tool schemas in context window
                    session_id = self._sessions.get_session_id(ws)
                    ctx_monitor = self._sessions.get_context_monitor(session_id)
                    if ctx_monitor:
                        ctx_monitor.add_content(session.get("instructions", ""))
                        for tool_schema in session.get("tools", []):
                            ctx_monitor.add_content(json.dumps(tool_schema))

        return updated_message

    async def _forward_messages(self, ws: web.WebSocketResponse):
        # Per-connection tool tracking — prevents cross-connection interference
        tools_pending: dict[str, RTToolCall] = {}

        # Per-connection verbose logging toggle (set by frontend extension message)
        verbose = _VERBOSE_GLOBAL
        audio_frame_count = 0  # Counter for verbose audio frame logging
        # Per-connection file handler for verbose log-to-file (set by frontend or env var)
        session_file_handler: logging.FileHandler | None = None

        # Echo suppression — delegates to EchoSuppressor
        echo = EchoSuppressor()

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
                headers = { "Authorization": f"Bearer {self._get_auth_token()}" }
            async with session.ws_connect(
                "/openai/realtime",
                headers=headers,
                params=params,
                heartbeat=_WS_HEARTBEAT_SEC,
            ) as target_ws:
                loop = asyncio.get_running_loop()
                session_id = self._sessions.get_session_id(ws)
                greeting_sent = self._sessions.has_sent_greeting(session_id) if session_id else False

                _vlog(verbose, "\n═══ [SESSION] Connected ═══\n"
                               "Session ID: %s\n"
                               "═══════════════════════════", session_id or "?")

                async def send_greeting_once(trigger: str = "unknown"):
                    nonlocal greeting_sent
                    if greeting_sent:
                        return
                    logger.info("Greeting firing via trigger=%s (session=%s)", trigger, session_id)
                    _vlog(verbose, "─── [Lifecycle] Greeting trigger=%s ───", trigger)
                    echo.start_greeting_suppression(verbose)
                    # Flush any stale audio that arrived before session was configured
                    await target_ws.send_str(_INPUT_AUDIO_CLEAR_MSG)
                    await target_ws.send_str(self._sessions.greeting_msg)
                    await target_ws.send_str(_RESPONSE_CREATE_MSG)
                    greeting_sent = True
                    if session_id is not None:
                        self._sessions.mark_greeting_sent(session_id)
                    # Track greeting in context window
                    ctx_monitor = self._sessions.get_context_monitor(session_id)
                    if ctx_monitor:
                        ctx_monitor.add_content(self._sessions.greeting_msg)

                async def from_client_to_server():
                    nonlocal verbose, audio_frame_count, session_file_handler
                    async for msg in ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            # Track activity for idle timeout
                            if session_id:
                                self._sessions.touch_activity(session_id)
                            # Intercept extension messages — don't forward to OpenAI
                            if _MARKER_VERBOSE_LOGGING in msg.data:
                                try:
                                    ext_msg = json.loads(msg.data)
                                    if ext_msg.get("type") == "extension.set_verbose_logging":
                                        verbose = bool(ext_msg.get("enabled", False))
                                        if verbose and not _VERBOSE_GLOBAL:
                                            vlogger.setLevel(logging.DEBUG)
                                            if not vlogger.handlers:
                                                _h = logging.StreamHandler()
                                                _h.setFormatter(logging.Formatter("%(message)s"))
                                                vlogger.addHandler(_h)
                                        logger.info("Verbose logging %s for session %s",
                                                    "ENABLED" if verbose else "DISABLED", session_id)
                                        _vlog(verbose,
                                              "\n╔══════════════════════════════════════╗\n"
                                              "║  VERBOSE LOGGING: %-8s           ║\n"
                                              "╚══════════════════════════════════════╝",
                                              "ENABLED" if verbose else "DISABLED")
                                        continue
                                except (json.JSONDecodeError, KeyError):
                                    pass

                            if _MARKER_LOG_TO_FILE in msg.data:
                                try:
                                    ext_msg = json.loads(msg.data)
                                    if ext_msg.get("type") == "extension.set_log_to_file":
                                        enabled = bool(ext_msg.get("enabled", False))
                                        if enabled and session_file_handler is None:
                                            vlogger.setLevel(logging.DEBUG)
                                            if not any(isinstance(h, logging.StreamHandler) and not isinstance(h, logging.FileHandler) for h in vlogger.handlers):
                                                _h = logging.StreamHandler()
                                                _h.setFormatter(logging.Formatter("%(message)s"))
                                                vlogger.addHandler(_h)
                                            session_file_handler = _create_verbose_file_handler()
                                            vlogger.addHandler(session_file_handler)
                                        elif not enabled and session_file_handler is not None:
                                            _remove_verbose_file_handler(session_file_handler)
                                            session_file_handler = None
                                        logger.info("Verbose log-to-file %s for session %s",
                                                    "ENABLED" if enabled else "DISABLED", session_id)
                                        _vlog(verbose or enabled,
                                              "\n╔══════════════════════════════════════╗\n"
                                              "║  LOG TO FILE: %-8s              ║\n"
                                              "╚══════════════════════════════════════╝",
                                              "ENABLED" if enabled else "DISABLED")
                                        continue
                                except (json.JSONDecodeError, KeyError):
                                    pass

                            # Echo suppression: drop mic audio while AI is speaking or cooling down.
                            if _MARKER_AUDIO_APPEND in msg.data:
                                if echo.should_suppress_audio(loop.time()):
                                    continue
                                audio_frame_count += 1
                                if (verbose or _VERBOSE_GLOBAL) and audio_frame_count % 50 == 0:
                                    _vlog(verbose, "─── [Client → Server] Audio frame #%d ───", audio_frame_count)
                            # Barge-in: client sent response.cancel — user wants to speak.
                            if _MARKER_RESPONSE_CANCEL in msg.data:
                                echo.on_barge_in(verbose)
                            # Forward client message to OpenAI.
                            new_msg = await self._process_message_to_server(msg, ws, verbose)
                            if new_msg is not None:
                                await target_ws.send_str(new_msg)
                            # Fallback greeting trigger
                            if not greeting_sent and _MARKER_SESSION_UPDATE in msg.data and _MARKER_SESSION_UPDATED not in msg.data:
                                logger.info("Fallback greeting: session.update forwarded — sending greeting without waiting for session.updated")
                                await send_greeting_once(trigger="fallback-after-session.update")
                        elif msg.type == aiohttp.WSMsgType.ERROR:
                            logger.error("Client WebSocket error: %s", ws.exception())
                            break
                        elif msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSING, aiohttp.WSMsgType.CLOSED):
                            break
                    
                    if target_ws and not target_ws.closed:
                        logger.info("Closing OpenAI's realtime socket connection.")
                        _vlog(verbose, "─── [Lifecycle] Disconnect — closing OpenAI socket ───")
                        await target_ws.close()
                        
                async def from_server_to_client():
                    async for msg in target_ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            data = msg.data
                            if _MARKER_AUDIO_DELTA in data:
                                echo.on_audio_delta(verbose)
                            elif _MARKER_AUDIO_DONE in data:
                                echo.on_audio_done(loop, target_ws, verbose)
                            elif _MARKER_SPEECH_STARTED in data:
                                echo.on_speech_started(verbose)

                            # Greeting trigger: wait for session.updated confirmation
                            if _MARKER_SESSION_UPDATED in data and not greeting_sent:
                                logger.info("session.updated received — tools are configured, sending greeting")
                                _vlog(verbose, "─── [Lifecycle] session.updated — sending greeting ───")
                                await send_greeting_once(trigger="session.updated")

                            # Verbose: log conversation transcription events
                            if (verbose or _VERBOSE_GLOBAL):
                                if '"conversation.item.input_audio_transcription.completed"' in data:
                                    try:
                                        _tr_msg = json.loads(data)
                                        _tr_text = _tr_msg.get("transcript", "")[:200]
                                        _vlog(verbose, '\n─── [User] transcription.completed ───\n"%s"', _tr_text)
                                        # Track user input in context window
                                        ctx_monitor = self._sessions.get_context_monitor(session_id)
                                        if ctx_monitor:
                                            ctx_monitor.add_content(_tr_text)
                                    except (json.JSONDecodeError, KeyError):
                                        pass

                            new_msg = await self._process_message_to_client(msg, ws, target_ws, tools_pending, verbose)
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
                    pass
                except Exception:
                    logger.exception("Unexpected error in WebSocket forwarding")
                finally:
                    _vlog(verbose, "\n═══ [SESSION] Disconnected ═══\n"
                                   "Session ID: %s\n"
                                   "══════════════════════════════", session_id or "?")
                    if session_file_handler is not None:
                        _remove_verbose_file_handler(session_file_handler)
                        session_file_handler = None
                    self._sessions.cleanup_session(ws, session_id)

    async def _websocket_handler(self, request: web.Request):
        # ── Origin validation (Task 3) ──
        origin = request.headers.get("Origin", "")
        allowed_origins = _security_cfg.get("allowed_origins", [])
        host = request.headers.get("Host", "")
        if origin and not origin.endswith(host) and origin not in allowed_origins:
            logger.warning("Rejected WebSocket from disallowed origin: %s", origin)
            return web.Response(status=403, text="Origin not allowed")

        # ── HMAC session token validation (Task 4) ──
        if _security_cfg.get("require_session_token", False):
            token = request.query.get("token", "")
            if not validate_hmac_token(token, self.app_secret):
                logger.warning("Rejected WebSocket with invalid/expired session token")
                return web.Response(status=401, text="Invalid or expired token")

        # ── Concurrency limit (Task 2) ──
        if not self._sessions.can_accept_session():
            logger.warning("Rejected WebSocket — session limit reached (%d)", self._sessions.active_session_count)
            ws = web.WebSocketResponse()
            await ws.prepare(request)
            await ws.send_json({"type": "error", "message": "Server is busy — please try again in a moment."})
            await ws.close()
            return ws

        ws = web.WebSocketResponse(
            heartbeat=_WS_HEARTBEAT_SEC,
            autoping=True,
            autoclose=True,
        )
        await ws.prepare(request)
        
        self._sessions.create_session(ws)

        await self._forward_messages(ws)
        return ws
    
    def attach_to_app(self, app: web.Application, path: str) -> None:
        app.router.add_get(path, self._websocket_handler)
