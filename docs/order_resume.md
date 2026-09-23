# Order resume (backend protocol)

A guest's order survives a short transport drop (Wi-Fi blip, load-balancer reset, missed heartbeat, tab
backgrounded). When the browser reconnects within the hold, it gets the same session and order back, and the carhop
carries on without greeting again. Stage 1 (this backend) implements and tests the protocol below. Stage 2 (the
frontend) implements the browser side.

## Lifecycle

| Event | What the backend does |
| --- | --- |
| Transport close (1001/1002/1006/1011, heartbeat timeout, handler exit, upstream connect failure) | The session is **detached**. Its order, transcript and resume credential are held for `min(resume.grace_seconds, remaining idle budget)`. |
| Idle close (4000 `idle_timeout`) | The session is **ended** before the socket closes. It can't be resumed. |
| `extension.end_session` from the browser | The session is ended, and the socket is closed with 1000 `session_ended`. |
| Hold expires, or the LRU cap (`resume.max_detached`) evicts the entry | The session is ended. This is checked every `resume.sweep_interval_seconds` and again at resume time. |

The idle clock (`security.idle_timeout_seconds`, 300 s) runs from the guest's last activity. It **keeps running while
the guest is disconnected**, so a drop never extends the 5 minutes.

- **Guest activity:** client text frames other than `input_audio_buffer.append`, upstream
  `input_audio_buffer.speech_started`, and `conversation.item.input_audio_transcription.completed`.
- **Not guest activity:** a continuously streaming mic, and the carhop's own speech, including the resume nudge.

`security.max_concurrent_sessions` counts attached sessions only. Held sessions don't use a slot.

Resume works within one backend process only. That's why the Dockerfile runs one gunicorn worker and the ingress uses
sticky affinity; see [customizing_deploy.md](customizing_deploy.md#scaling-session-affinity-and-the-session-token-secret).

## Resume credential

- A 256-bit `secrets.token_urlsafe(32)` string (43 chars).
- It's delivered **only over the websocket**. Never put it in a URL, a cookie, or logs. The server stores and logs only
  its SHA-256; logs show `sha256(id)[:8]`.
- It's single-use. Every successful resume consumes it and returns a new one, and re-announcing a session also rotates
  it. The compare is constant-time.
- It isn't bound to the signed-in user, and it isn't embedded in the HMAC session token. The HMAC token stays a pure
  connection gate.

## Wire protocol

All messages are JSON text frames on `/realtime`.

### 1. Server → browser: `extension.session_metadata` (fresh session)

```json
{"type": "extension.session_metadata",
 "sessionToken": "…", "roundTripIndex": 0, "roundTripToken": "…",
 "resumeId": "<43-char id>"}
```

The server sends this once it has decided the connection is fresh **and** the upstream `session.created` has arrived.

The connection counts as fresh when either of these happens:

- the browser's first frame is anything other than `extension.resume`;
- no frame arrives within `resume.first_frame_timeout_seconds` (2 s).

A browser that never resumes therefore sees metadata up to 2 s after connecting, or as soon as it sends its first
`session.update`.

`resumeId` is omitted when `resume.enabled` is false. The fields are camelCase, as before.

**Browser:** store `resumeId` in `sessionStorage`. Resume is per tab by design.

### 2. Browser → server: `extension.resume` (must be the FIRST frame)

```json
{"type": "extension.resume", "resume_id": "<stored id>"}
```

Send it as the very first frame after `open`, before `session.update`, audio, or anything queued. The server honours
it only as the first frame, and only before the 2 s first-frame deadline. The frame is never forwarded to the model.

### 3a. Server → browser: `extension.session_resumed` (accepted)

```json
{"type": "extension.session_resumed",
 "order_summary": {"items": [...], "total": 4.49, "tax": 0.36, "finalTotal": 4.85},
 "session_token": "…", "round_trip_index": 3, "round_trip_token": "…",
 "resume_id": "<rotated id>"}
```

The fields are snake_case, as in the plan.

`order_summary` is the parsed object: the same shape the ticket already renders from
`JSON.parse(extension.middle_tier_tool_response.tool_result)`.

This takes the place of `extension.session_metadata`; no metadata frame follows.

**Browser:**
1. Replace the stored id with `resume_id`.
2. Render `order_summary` onto the ticket.
3. Set the session/round-trip identifiers.
4. Restart the mic straight away. The backend has already sent its bootstrap `session.update`, so the session is ready
   for audio. The browser may still send its own `session.update` (voice, etc.) as usual. That doesn't trigger a
   greeting.

The carhop stays **silent** until the guest speaks. There is no "welcome back". If the guest hasn't spoken within
`resume.nudge_after_seconds` (30 s), the carhop asks once, briefly, whether they need anything else. That arrives as a
normal response (audio/transcript events).

### 3b. Server → browser: `extension.resume_rejected`

```json
{"type": "extension.resume_rejected", "reason": "unknown"}
```

| `reason` | Meaning |
| --- | --- |
| `unknown` | Wrong id, already-used id, the session was ended or evicted, or the order is gone. |
| `expired` | The grace hold or the idle budget ran out. |
| `malformed` | Missing id, or not a 32–128 char string. |
| `disabled` | `resume.enabled` is false. |
| `not_first_frame` | `extension.resume` arrived after another frame or after the first-frame deadline. The socket's current session continues. |

The server **always** follows a rejection with an `extension.session_metadata` for the socket's fresh (or current)
session, carrying a new `resumeId`.

**Browser:**
1. Drop the old id.
2. Clear the ticket and show a fresh order.
3. Store the new `resumeId` from the metadata that follows.

### 4. Browser → server: `extension.end_session` (optional)

```json
{"type": "extension.end_session"}
```

The guest finished or pressed stop. The server ends the session (the order is deleted immediately) and closes the
socket with 1000 `session_ended`.

**Browser:** clear the stored id and don't reconnect.

### Upstream ordering on a resume (for reference)

The new model connection receives, in order:

1. The bootstrap `session.update` (instructions, voice, tools).
2. **One** system `conversation.item.create`. It holds the current order JSON plus the last `resume.history_turns`
   guest/carhop turns, capped at `resume.history_chars`, newest kept.
3. Only after that: guest audio, and the browser's `session.update`.

No greeting and no `response.create` are sent until the guest speaks, or until the nudge fires. The nudge waits for
`session.updated`, just like the greeting does.

If the resumed session had never been greeted (the drop came before the conversation started), no rehydration item is
sent and the normal greeting runs.

## Close codes

| Code | Reason | Resumable? | Browser action |
| --- | --- | --- | --- |
| 1001/1002/1006/1011 etc. | transport | **Yes**, within the hold | Reconnect, send `extension.resume` first, and auto-restart the mic on `session_resumed`. |
| 4000 | `idle_timeout` | No (session already ended) | Don't reconnect. Clear the stored id and show "session ended". |
| 4001, or a reason containing `expired` | token refresh (existing frontend path) | Yes | Refresh the token, reconnect, then resume as above. Don't reuse 4001. The backend's close reasons deliberately avoid the word `expired`. |
| 4002 | `superseded` | n/a | Another socket (usually this tab's reconnect) took over the session. Don't reconnect, and don't clear the id; the new socket owns it. |
| 1000 | `session_ended` | No | Reply to `extension.end_session`. Clear the id and don't reconnect. |

`shouldReconnect` must return false for 4000, 4002 and 1000 `session_ended`.

**Queued frames:** react-use-websocket (keep=true) flushes queued frames on open. Make sure nothing queued is sent
ahead of `extension.resume`.

## Configuration (`app/backend/config.yaml`)

| Key | Default | Meaning |
| --- | --- | --- |
| `security.idle_timeout_seconds` | 300 | Idle budget from the guest's last activity. It keeps running while disconnected. |
| `resume.enabled` | true | false: transport closes end the session, and no `resumeId` is issued. |
| `resume.grace_seconds` | 120 | Maximum hold after a transport drop. The actual hold is `min(grace, remaining idle budget)`. |
| `resume.max_detached` | 20 | LRU cap on held sessions per worker. |
| `resume.history_turns` | 6 | Transcript turns replayed into the new upstream. 0 disables the transcript. |
| `resume.history_chars` | 2000 | Char cap on the replayed transcript. |
| `resume.nudge_after_seconds` | 30 | Silent-guest nudge after a resume. 0 disables it. |
| `resume.first_frame_timeout_seconds` | 2.0 | How long to wait for `extension.resume` before treating the connection as fresh. |
| `resume.sweep_interval_seconds` | 15 | How often idle and grace expiry are checked. |
| env `APP_SESSION_SECRET` | random per process | HMAC key for `/api/auth/session` tokens, shared across replicas. Set by bicep. |
