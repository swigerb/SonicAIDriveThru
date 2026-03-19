import asyncio
import json
import logging
from collections.abc import Callable
from enum import Enum
from typing import Any

import aiohttp
from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import DefaultAzureCredential, get_bearer_token_provider

from order_state import SessionIdentifiers, order_state_singleton

logger = logging.getLogger("sonic-drive-in")

__all__ = ["RTMiddleTier", "RTToolCall", "Tool", "ToolResult", "ToolResultDirection"]

class ToolResultDirection(Enum):
    TO_SERVER = 1
    TO_CLIENT = 2

class ToolResult:
    text: str
    destination: ToolResultDirection

    def __init__(self, text: str, destination: ToolResultDirection):
        self.text = text
        self.destination = destination

    def to_text(self) -> str:
        if self.text is None:
            return ""
        return self.text if isinstance(self.text, str) else json.dumps(self.text)

class Tool:
    target: Callable[..., ToolResult]
    schema: Any

    def __init__(self, target: Any, schema: Any):
        self.target = target
        self.schema = schema

class RTToolCall:
    tool_call_id: str
    previous_id: str

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
        self._tools_pending: dict[str, RTToolCall] = {}
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

    async def _process_message_to_client(self, msg: str, client_ws: web.WebSocketResponse, server_ws: web.WebSocketResponse) -> str | None:
        message = json.loads(msg.data)
        updated_message = msg.data
        session_id = self._session_map.get(client_ws)
        if message is not None:
            match message["type"]:
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
                        updated_message = None

                case "conversation.item.created":
                    if "item" in message and message["item"]["type"] == "function_call":
                        item = message["item"]
                        if item["call_id"] not in self._tools_pending:
                            self._tools_pending[item["call_id"]] = RTToolCall(item["call_id"], message["previous_item_id"])
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
                        tool_call = self._tools_pending[message["item"]["call_id"]]
                        tool = self.tools[item["name"]]
                        args = item["arguments"]
                        if item["name"] in ["update_order", "get_order"]:
                            result = await tool.target(json.loads(args), session_id)
                        else:
                            result = await tool.target(json.loads(args))
                        await server_ws.send_json({
                            "type": "conversation.item.create",
                            "item": {
                                "type": "function_call_output",
                                "call_id": item["call_id"],
                                "output": result.to_text() if result.destination == ToolResultDirection.TO_SERVER else ""
                            }
                        })
                        if result.destination == ToolResultDirection.TO_CLIENT:
                            # TODO: this will break clients that don't know about this extra message, rewrite 
                            # this to be a regular text message with a special marker of some sort
                            await client_ws.send_json({
                                "type": "extension.middle_tier_tool_response",
                                "previous_item_id": tool_call.previous_id,
                                "tool_name": item["name"],
                                "tool_result": result.to_text()
                            })
                        updated_message = None

                case "response.done":
                    if len(self._tools_pending) > 0:
                        self._tools_pending.clear() # Any chance tool calls could be interleaved across different outstanding responses?
                        await server_ws.send_json({
                            "type": "response.create"
                        })
                    if "response" in message:
                        replace = False
                        try:
                            for i in range(len(message["response"]["output"]) - 1, -1, -1):
                                if message["response"]["output"][i]["type"] == "function_call":
                                    message["response"]["output"].pop(i)
                                    replace = True
                        except IndexError as e:
                            logging.error(f"Error processing message: {e}")
                        if replace:
                            updated_message = json.dumps(message)
                    if session_id is not None:
                        identifiers = order_state_singleton.advance_round_trip(session_id)
                        await self._emit_session_identifiers(client_ws, "extension.round_trip_token", identifiers)

        return updated_message

    async def _process_message_to_server(self, msg: str, ws: web.WebSocketResponse) -> str | None:
        message = json.loads(msg.data)
        updated_message = msg.data
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
                    updated_message = json.dumps(message)

        return updated_message

    async def _forward_messages(self, ws: web.WebSocketResponse):
        async with aiohttp.ClientSession(base_url=self.endpoint) as session:
            params = { "api-version": self.api_version, "deployment": self.deployment}
            headers = {}
            if "x-ms-client-request-id" in ws.headers:
                headers["x-ms-client-request-id"] = ws.headers["x-ms-client-request-id"]
            if self.key is not None:
                headers = { "api-key": self.key }
            else:
                headers = { "Authorization": f"Bearer {self._token_provider()}" } # NOTE: no async version of token provider, maybe refresh token on a timer?
            async with session.ws_connect("/openai/realtime", headers=headers, params=params) as target_ws:
                session_id = self._session_map.get(ws)
                greeting_sent = session_id in self._sent_greeting

                async def send_greeting_once():
                    nonlocal greeting_sent
                    if greeting_sent:
                        return
                    await target_ws.send_json({
                        "type": "conversation.item.create",
                        "item": {
                            "type": "message",
                            "role": "user",
                            "content": [
                                {"type": "input_text", "text": "Please greet the guest with: 'Welcome to Sonic Drive-In! What can I get started for you today?'"}
                            ]
                        }
                    })
                    await target_ws.send_json({"type": "response.create"})
                    greeting_sent = True
                    if session_id is not None:
                        self._sent_greeting.add(session_id)
                async def from_client_to_server():
                    async for msg in ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            if not greeting_sent:
                                await send_greeting_once()
                            new_msg = await self._process_message_to_server(msg, ws)
                            if new_msg is not None:
                                await target_ws.send_str(new_msg)
                        else:
                            logger.warning("Unexpected message type from client: %s", msg.type)
                    
                    # Means it is gracefully closed by the client then time to close the target_ws
                    if target_ws:
                        logger.info("Closing OpenAI's realtime socket connection.")
                        await target_ws.close()
                        
                async def from_server_to_client():
                    async for msg in target_ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            new_msg = await self._process_message_to_client(msg, ws, target_ws)
                            if new_msg is not None:
                                await ws.send_str(new_msg)
                        else:
                            logger.warning("Unexpected message type from server: %s", msg.type)

                try:
                    await asyncio.gather(from_client_to_server(), from_server_to_client())
                except ConnectionResetError:
                    # Ignore the errors resulting from the client disconnecting the socket
                    pass
                finally:
                    if session_id is not None:
                        order_state_singleton.delete_session(session_id)
                    # Clean up the session map when the connection is closed
                    if ws in self._session_map:
                        del self._session_map[ws]

    async def _websocket_handler(self, request: web.Request):
        ws = web.WebSocketResponse()
        await ws.prepare(request)
        
        # Create a new session for each WebSocket connection
        session_id = order_state_singleton.create_session()
        self._session_map[ws] = session_id

        await self._forward_messages(ws)
        return ws
    
    def attach_to_app(self, app: web.Application, path: str) -> None:
        app.router.add_get(path, self._websocket_handler)
