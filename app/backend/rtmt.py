import asyncio
import json
import logging
import os
import pathlib
import re
import time
from datetime import datetime, timezone
from enum import Enum
from typing import Any

import aiohttp
from aiohttp import web
from azure.core.credentials import AzureKeyCredential
from azure.identity import DefaultAzureCredential, get_bearer_token_provider

from config_loader import get_config
from order_state import SessionIdentifiers, order_state_singleton

logger = logging.getLogger("sonic-drive-in")

# Load centralized config
_config = get_config()
_audio_cfg = _config.get("audio", {})
_conn_cfg = _config.get("connection", {})

# ── Verbose diagnostic logger ──
# Separate logger so verbose output can be toggled without affecting production logs.
# Enabled globally via VERBOSE_LOGGING=true env var, or per-session via WebSocket message.
vlogger = logging.getLogger("sonic-verbose")
_VERBOSE_GLOBAL = os.environ.get("VERBOSE_LOGGING", "").lower() in ("true", "1", "yes")
if _VERBOSE_GLOBAL:
    vlogger.setLevel(logging.DEBUG)
    if not vlogger.handlers:
        _handler = logging.StreamHandler()
        _handler.setFormatter(logging.Formatter("%(message)s"))
        vlogger.addHandler(_handler)
else:
    vlogger.setLevel(logging.WARNING)  # silent until per-session toggle

# ── Verbose file logging ──
# Always-on file logging via VERBOSE_LOG_FILE=true env var (parallel to VERBOSE_LOGGING).
_VERBOSE_LOG_FILE_GLOBAL = os.environ.get("VERBOSE_LOG_FILE", "").lower() in ("true", "1", "yes")
_LOGS_DIR = pathlib.Path(__file__).parent / "logs"


def _create_verbose_file_handler() -> logging.FileHandler:
    """Create a FileHandler that writes verbose logs to a timestamped file in app/backend/logs/."""
    _LOGS_DIR.mkdir(parents=True, exist_ok=True)
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M")
    log_path = _LOGS_DIR / f"verbose-{ts}.log"
    fh = logging.FileHandler(log_path, encoding="utf-8")
    fh.setLevel(logging.DEBUG)
    fh.setFormatter(logging.Formatter("%(message)s"))
    # Flush after every write so the file is always up to date
    fh.stream.reconfigure(line_buffering=True)  # type: ignore[union-attr]
    # Write header line
    header = f"═══ Verbose Log Started: {datetime.now(timezone.utc).isoformat()} ═══"
    fh.stream.write(header + "\n")
    fh.stream.flush()
    logger.info("Verbose log file opened: %s", log_path)
    return fh


def _remove_verbose_file_handler(fh: logging.FileHandler) -> None:
    """Remove a file handler from the verbose logger and close it cleanly."""
    vlogger.removeHandler(fh)
    try:
        fh.close()
    except Exception:
        pass


# If VERBOSE_LOG_FILE env var is set, attach a global file handler at module load.
_global_file_handler: logging.FileHandler | None = None
if _VERBOSE_LOG_FILE_GLOBAL:
    vlogger.setLevel(logging.DEBUG)
    if not any(isinstance(h, logging.StreamHandler) and not isinstance(h, logging.FileHandler) for h in vlogger.handlers):
        _sh = logging.StreamHandler()
        _sh.setFormatter(logging.Formatter("%(message)s"))
        vlogger.addHandler(_sh)
    _global_file_handler = _create_verbose_file_handler()
    vlogger.addHandler(_global_file_handler)

# Max characters of tool result text to log (prevents terminal flooding)
_VERBOSE_RESULT_TRUNCATE = _config.get("logging", {}).get("verbose_result_truncate", 500)

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
_ECHO_COOLDOWN_SEC = _audio_cfg.get("echo_cooldown_seconds", 1.5)

# Fast substring markers for echo suppression (avoids regex/JSON parse overhead)
_MARKER_AUDIO_APPEND = '"input_audio_buffer.append"'
_MARKER_AUDIO_DELTA = '"response.audio.delta"'
_MARKER_AUDIO_DONE = '"response.audio.done"'
_MARKER_SPEECH_STARTED = '"input_audio_buffer.speech_started"'
_MARKER_SESSION_UPDATE = '"session.update"'
_MARKER_SESSION_UPDATED = '"session.updated"'
_MARKER_RESPONSE_CANCEL = '"response.cancel"'
_MARKER_VERBOSE_LOGGING = '"extension.set_verbose_logging"'
_MARKER_LOG_TO_FILE = '"extension.set_log_to_file"'

# Connection tuning constants
_WS_HEARTBEAT_SEC = _conn_cfg.get("ws_heartbeat_seconds", 15.0)
_WS_CONNECT_TIMEOUT = aiohttp.ClientTimeout(
    total=_conn_cfg.get("ws_connect_timeout_total", 30),
    connect=_conn_cfg.get("ws_connect_timeout_connect", 10),
)

# Default greeting — overridden by PromptLoader at runtime.
# Kept as fallback for tests and backwards compatibility.
_DEFAULT_GREETING_MSG = json.dumps({
    "type": "conversation.item.create",
    "item": {
        "type": "message",
        "role": "user",
        "content": [
            {"type": "input_text", "text": "Say EXACTLY this greeting and NOTHING else: Welcome to Sonic Drive-In! What can I get started for you today?"}
        ]
    }
})


def _vlog(verbose: bool, msg: str, *args: Any) -> None:
    """Log to sonic-verbose at DEBUG level if this session has verbose enabled."""
    if verbose or _VERBOSE_GLOBAL:
        vlogger.debug(msg, *args)


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
        self._session_map: dict[web.WebSocketResponse, str] = {}
        self._sent_greeting: set[str] = set()
        self._prompt_loader = prompt_loader
        # Use prompt loader greeting if available, else fallback
        if prompt_loader is not None:
            self._greeting_msg = prompt_loader.get_greeting_json_str()
        else:
            self._greeting_msg = _DEFAULT_GREETING_MSG
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
        session_id = self._session_map.get(client_ws)
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
                        await self._emit_session_identifiers(client_ws, "extension.session_metadata", identifiers)
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
                        await self._emit_session_identifiers(client_ws, "extension.round_trip_token", identifiers)
                        _vlog(verbose, "─── [ROUND TRIP] #%d ───\n"
                                       "Token: %s\n"
                                       "────────────────────────",
                              identifiers.round_trip_index,
                              identifiers.round_trip_token)

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

        return updated_message

    async def _forward_messages(self, ws: web.WebSocketResponse):
        # Per-connection tool tracking — prevents cross-connection interference
        tools_pending: dict[str, RTToolCall] = {}

        # Per-connection verbose logging toggle (set by frontend extension message)
        verbose = _VERBOSE_GLOBAL
        audio_frame_count = 0  # Counter for verbose audio frame logging
        # Per-connection file handler for verbose log-to-file (set by frontend or env var)
        session_file_handler: logging.FileHandler | None = None

        # Echo suppression state — shared between client→server and server→client tasks.
        # Safe without locks: single-threaded asyncio event loop guarantees no concurrent mutation.
        ai_speaking = False
        cooldown_end = 0.0  # loop.time() value after which user audio is accepted again
        # Blocks barge-in (speech_started) during the greeting response.
        # Without this, the greeting audio echoing into the mic triggers VAD,
        # which resets ai_speaking and allows the echo through — causing a second greeting.
        greeting_in_progress = False

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

                _vlog(verbose, "\n═══ [SESSION] Connected ═══\n"
                               "Session ID: %s\n"
                               "═══════════════════════════", session_id or "?")

                async def send_greeting_once(trigger: str = "unknown"):
                    nonlocal greeting_sent, ai_speaking, greeting_in_progress
                    if greeting_sent:
                        return
                    logger.info("Greeting firing via trigger=%s (session=%s)", trigger, session_id)
                    _vlog(verbose, "─── [Lifecycle] Greeting trigger=%s ───", trigger)
                    # Pre-set echo suppression: the AI will start speaking as soon as
                    # OpenAI processes response.create.  Without this, there's a gap
                    # between response.create and the first response.audio.delta where
                    # mic audio could leak through and cause an echo loop.
                    ai_speaking = True
                    greeting_in_progress = True
                    _vlog(verbose, "  Echo suppression: ai_speaking=True (pre-set for greeting)")
                    # Flush any stale audio that arrived before session was configured
                    await target_ws.send_str(_INPUT_AUDIO_CLEAR_MSG)
                    await target_ws.send_str(self._greeting_msg)
                    await target_ws.send_str(_RESPONSE_CREATE_MSG)
                    greeting_sent = True
                    if session_id is not None:
                        self._sent_greeting.add(session_id)

                async def from_client_to_server():
                    nonlocal ai_speaking, cooldown_end, verbose, audio_frame_count, session_file_handler
                    async for msg in ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            # Intercept extension messages — don't forward to OpenAI
                            if _MARKER_VERBOSE_LOGGING in msg.data:
                                try:
                                    ext_msg = json.loads(msg.data)
                                    if ext_msg.get("type") == "extension.set_verbose_logging":
                                        verbose = bool(ext_msg.get("enabled", False))
                                        if verbose and not _VERBOSE_GLOBAL:
                                            # Enable the verbose logger for this session
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
                                        continue  # Don't forward to OpenAI
                                except (json.JSONDecodeError, KeyError):
                                    pass

                            if _MARKER_LOG_TO_FILE in msg.data:
                                try:
                                    ext_msg = json.loads(msg.data)
                                    if ext_msg.get("type") == "extension.set_log_to_file":
                                        enabled = bool(ext_msg.get("enabled", False))
                                        if enabled and session_file_handler is None:
                                            # Ensure verbose logger is active
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
                                        continue  # Don't forward to OpenAI
                                except (json.JSONDecodeError, KeyError):
                                    pass

                            # Echo suppression: drop mic audio while AI is speaking or cooling down.
                            # The AI's speaker output leaks into the mic and gets transcribed as
                            # phantom user input ("Peace.", "Thank you so much."), creating a
                            # self-conversation loop. Dropping audio here breaks that cycle.
                            if _MARKER_AUDIO_APPEND in msg.data:
                                if ai_speaking or loop.time() < cooldown_end:
                                    continue
                                audio_frame_count += 1
                                if (verbose or _VERBOSE_GLOBAL) and audio_frame_count % 50 == 0:
                                    _vlog(verbose, "─── [Client → Server] Audio frame #%d ───", audio_frame_count)
                            # Barge-in: client sent response.cancel — user wants to speak.
                            # Disable echo suppression so their audio can flow through.
                            if _MARKER_RESPONSE_CANCEL in msg.data:
                                logger.info("Client sent response.cancel — disabling echo suppression for barge-in")
                                _vlog(verbose, "─── [Client] response.cancel — barge-in, echo suppression OFF ───")
                                ai_speaking = False
                                cooldown_end = 0.0
                            # Forward client message to OpenAI.
                            new_msg = await self._process_message_to_server(msg, ws, verbose)
                            if new_msg is not None:
                                await target_ws.send_str(new_msg)
                            # Fallback greeting trigger: fire after forwarding session.update
                            # in case session.updated never arrives from the server.
                            # The session.updated trigger in from_server_to_client is preferred
                            # (confirms tools are configured), but this ensures the greeting
                            # always fires even if the API doesn't send session.updated.
                            if not greeting_sent and _MARKER_SESSION_UPDATE in msg.data and _MARKER_SESSION_UPDATED not in msg.data:
                                logger.info("Fallback greeting: session.update forwarded — sending greeting without waiting for session.updated")
                                await send_greeting_once(trigger="fallback-after-session.update")
                        elif msg.type == aiohttp.WSMsgType.ERROR:
                            logger.error("Client WebSocket error: %s", ws.exception())
                            break
                        elif msg.type in (aiohttp.WSMsgType.CLOSE, aiohttp.WSMsgType.CLOSING, aiohttp.WSMsgType.CLOSED):
                            break
                    
                    # Means it is gracefully closed by the client then time to close the target_ws
                    if target_ws and not target_ws.closed:
                        logger.info("Closing OpenAI's realtime socket connection.")
                        _vlog(verbose, "─── [Lifecycle] Disconnect — closing OpenAI socket ───")
                        await target_ws.close()
                        
                async def from_server_to_client():
                    nonlocal ai_speaking, cooldown_end, greeting_in_progress
                    async for msg in target_ws:
                        if msg.type == aiohttp.WSMsgType.TEXT:
                            # Track AI speaking state for echo suppression.
                            # These substring checks are O(1)-fast (no JSON parse) and only
                            # trigger state transitions, not per-frame work.
                            data = msg.data
                            if _MARKER_AUDIO_DELTA in data:
                                if not ai_speaking:
                                    logger.debug("Echo suppression: AI speaking — suppressing user audio")
                                    _vlog(verbose, "─── [Echo] ai_speaking=True — suppressing user audio ───")
                                ai_speaking = True
                            elif _MARKER_AUDIO_DONE in data:
                                ai_speaking = False
                                # Longer cooldown after greeting — speakers at full volume,
                                # no prior calibration makes echo worst-case at startup.
                                if greeting_in_progress:
                                    actual_cooldown = _ECHO_COOLDOWN_SEC * 2
                                    greeting_in_progress = False
                                    logger.debug("Echo suppression: greeting audio done — extended cooldown %.1fs", actual_cooldown)
                                else:
                                    actual_cooldown = _ECHO_COOLDOWN_SEC
                                cooldown_end = loop.time() + actual_cooldown
                                logger.debug("Echo suppression: AI audio done — cooldown %.1fs", actual_cooldown)
                                _vlog(verbose, "─── [Echo] ai_speaking=False — cooldown %.1fs ───", actual_cooldown)
                                # Flush any echoed audio that leaked into OpenAI's buffer
                                await target_ws.send_str(_INPUT_AUDIO_CLEAR_MSG)
                                # Schedule a SECOND flush after cooldown expires to catch
                                # echo audio that accumulated during the cooldown window.
                                # Without this, residual echo triggers VAD → self-talk loop.
                                def _make_delayed_flush(tws=target_ws):
                                    if not tws.closed:
                                        asyncio.ensure_future(tws.send_str(_INPUT_AUDIO_CLEAR_MSG))
                                loop.call_later(actual_cooldown, _make_delayed_flush)
                            elif _MARKER_SPEECH_STARTED in data:
                                # Server VAD detected speech — but during the greeting
                                # this is almost certainly echo, not a real barge-in.
                                if greeting_in_progress:
                                    logger.debug("Echo suppression: ignoring speech_started during greeting")
                                    _vlog(verbose, "─── [Echo] speech_started IGNORED (greeting in progress) ───")
                                else:
                                    if ai_speaking:
                                        logger.debug("Echo suppression: barge-in detected — resuming user audio")
                                        _vlog(verbose, "─── [Echo] Barge-in — ai_speaking=False, cooldown reset ───")
                                    ai_speaking = False
                                    cooldown_end = 0.0

                            # Greeting trigger: wait for session.updated confirmation
                            # from OpenAI before sending the greeting.  This guarantees
                            # tools + system_message are fully configured before the first
                            # model completion (response.create).
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
                    # Ignore the errors resulting from the client disconnecting the socket
                    pass
                except Exception:
                    logger.exception("Unexpected error in WebSocket forwarding")
                finally:
                    _vlog(verbose, "\n═══ [SESSION] Disconnected ═══\n"
                                   "Session ID: %s\n"
                                   "══════════════════════════════", session_id or "?")
                    # Clean up per-connection file handler — don't leak file handles
                    if session_file_handler is not None:
                        _remove_verbose_file_handler(session_file_handler)
                        session_file_handler = None
                    if session_id is not None:
                        order_state_singleton.delete_session(session_id)
                    # Clean up the session map and greeting tracker when the connection is closed
                    self._session_map.pop(ws, None)
                    if session_id is not None:
                        self._sent_greeting.discard(session_id)

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
