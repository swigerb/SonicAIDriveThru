# Rate-limit recovery

The three drive-thru demos share one Azure OpenAI quota. When the service rate-limits a response, it
fails that response with no output. Before this change the middle tier passed the failure straight through, so the
guest heard silence and the carhop looked frozen. Now the middle tier retries and, if it has to, the browser
apologises with a pre-recorded clip.

Code: `app/backend/rate_limit.py`, which is wired in `app/backend/rtmt.py`. Browser side: `app/frontend/src/App.tsx`
(`onReceivedRateLimited`) and `app/frontend/src/lib/apology.ts`.

## Detection

A response counts as rate-limited when either of these arrives:

- A `response.done` with `response.status == "failed"`, where the `code` or `type` of
  `response.status_details.error` contains `rate_limit` (for example `rate_limit_exceeded`).
- An `error` event whose `error.code` or `error.type` contains `rate_limit`, and which is not correlated to one of
  our `session.update` `event_id`s. Those still go to the existing session.update fallback, which resends a minimal
  update so the tools register.

A rate-limit `error` that arrives while a response is still in flight is left to that response's `response.done`, so
one failure is never handled twice.

Every rate-limit is logged at WARNING with the code and any retry hint. Handled frames are not forwarded to the
browser. Any other failure behaves as it did before.

## The ladder

The ladder runs per failed response, never per session.

| step | when | upstream | browser |
|---|---|---|---|
| 1. silent retry | the response is rate-limited | `response.create` after `retry_delay_seconds` (1.5 s) | nothing |
| 2. apology + retry | retry 1 is also rate-limited | `response.create` after `second_retry_delay_seconds` (4 s) | `{"type":"extension.rate_limited","attempt":1}` |
| 3. give up | retry 2 is also rate-limited | nothing more | `{"type":"extension.rate_limited","attempt":2,"final":true}` |

- A "try again in X s" or "X ms" hint in the error message replaces the delay. For retry 1 the hint is clamped to
  [0.5 s, 5 s], and for retry 2 to [2 s, 8 s].
- A failed response produces no output, so the guest's input item is still the last thing in the conversation, and a
  plain `response.create` regenerates the answer.
- A failed tool follow-up works the same way. The `function_call_output` is already in the conversation, so the retry
  regenerates the spoken answer and the tool is not run again.
- After `final`, the session stays up and the guest's next turn proceeds normally. There are no loops.

A pending retry is cancelled as soon as anything else takes the turn:

- the guest starts speaking (`input_audio_buffer.speech_started`);
- another response starts (a `response.created` that is not our retry, for example from VAD or a tool follow-up);
- the browser sends its own `response.create`;
- the socket detaches.

A stale retry is never stacked on a fresh response.

### With order resume

Order resume ([order_resume.md](order_resume.md)) adds a grace hold after a transport drop and a 30 s "are you still
there?" nudge. The two features compose as follows:

- **A retry is not guest activity.** Retries are sent straight to the upstream socket and never touch the session's
  idle clock, so a retry storm can't keep an abandoned session alive.
- **No double responses.** The nudge stays quiet while a retry is pending or its response is running
  (`RateLimitRecovery.busy`). A rate-limit `error` that arrives while the nudge's response is running does not
  schedule a retry on top of it.
- **Detach cancels.** When the socket closes, whether a transport drop or a hang-up, the pending retry is cancelled.
  It never fires into a held session. After a resume, the guest's next turn starts clean.

`app/backend/tests/test_rate_limit.py` (`ResumeInteractionTests`) covers each of these.

## Browser

On `extension.rate_limited` with `attempt: 1`, the browser does the following:

1. Shows the notice "One moment, please…".
2. Mutes the microphone, so the clip isn't transcribed as guest speech.
3. Plays `/audio/apology-<lang>.wav` for the selected UI language, falling back to `en`.
4. Unmutes, unless the carhop has started talking again in the meantime.

The clip is given at most 5 s. If it can't play, the flow carries on.

On `final: true`, the browser shows "We're a little busy — please say that again." and unmutes the microphone,
unless the clip is still playing, in which case the clip's end unmutes it.

The notice clears when the guest speaks, when a response with a transcript arrives, or when the session stops.

## Apology clips

The clips live in `app/frontend/public/audio/apology-{en,es,fr,ja}.wav`, one per UI locale. They are 24 kHz mono PCM16,
about 2–3 s each (roughly 100–150 KB). They have to be local audio: when the clip is needed, the model is the thing
being rate-limited.

| lang | phrase |
|---|---|
| en | Sorry, give me just a second. |
| es | Perdón, dame un segundito. |
| fr | Pardon, juste une petite seconde. |
| ja | 申し訳ありません、少々お待ちください。 |

They were recorded once with the live model (`gpt-realtime-2.1`, voice `marin`, the app's default voice). The phrase
went only in the response instructions, and a warm carhop tone was set in the session instructions. Each recording was
then checked with `whisper-1`, and kept only when the transcript matched the phrase word for word. To re-record, for
example after changing the default voice:

```shell
python scripts/generate_apology_clips.py                 # all languages
python scripts/generate_apology_clips.py --lang ja --voice cedar
```

A clip is always in the default voice, even when the guest picked another voice in settings.

## Configuration

`app/backend/config.yaml`:

```yaml
resilience:
  rate_limit:
    enabled: true
    retry_delay_seconds: 1.5
    second_retry_delay_seconds: 4
    max_retries: 2
```

`RATE_LIMIT_RECOVERY_ENABLED=true|false` overrides `enabled`, and an empty value keeps the config value. The name is
the same in all three demos. With recovery disabled, rate-limited frames pass through to the browser as they did
before.

## What only a deployment can confirm

The tests use a fake upstream that fails responses on demand. A real deployment is needed to confirm the following:

- The exact shape of Azure's rate-limit failures on `gpt-realtime-2.1` (`status_details.error.code` or `type`, and
  whether a retry hint appears in the message).
- That a retried `response.create` on the live service regenerates the answer to the pending guest turn.
- That the clip plays promptly on real devices (autoplay policies, Bluetooth latency) while the mic is muted.
