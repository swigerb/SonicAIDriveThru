# Customizing the Sonic AI Drive-Thru deployment

This guide shows you how to customize the [Sonic AI Drive-Thru](../README.md#deploying-the-app) deployment to specify different options.
If your goal is to reuse existing services (OpenAI or Search), see the [existing services guide](./existing_services.md) instead.

## Customizing the real-time voice choice

The default carhop voice is `marin` (set in `app/backend/config.yaml` `model.default_voice` and in
`infra/main.parameters.json`). Guests can also switch voices live from the settings dialog.
To change the deployed default, run:

```bash
azd env set AZURE_OPENAI_REALTIME_VOICE_CHOICE <marin, cedar, alloy, ash, ballad, coral, echo, sage, shimmer, or verse>
```

These are the ten built-in voices `gpt-realtime-2.1` accepts (other names such as `fable`, `onyx` or `nova`
are rejected). OpenAI recommends `marin` and `cedar` for the best quality.

> An `azd env` value overrides the default in `infra/main.parameters.json`, so an environment created before
> the default changed keeps its old voice until you `azd env set` it.

Once you have set the voice choice, run `azd up` to apply the changes to the deployed app.
If you've already run `azd up` and want to first preview the voice with the development server, then update your local `.env` file by running `./scripts/write_env.sh` or `pwsh ./scripts/write_env.ps1`, and then restart the development server.

## Reasoning effort, parallel tool calls and transcription

`model.reasoning_effort` (default `low`; env `AZURE_OPENAI_REALTIME_REASONING_EFFORT`, `off` disables) is sent as
`session.reasoning.effort` only when the deployment is a reasoning model. `model.reasoning_model`
(env `AZURE_OPENAI_REALTIME_REASONING_MODEL`: `auto` | `true` | `false`) says whether it is; `auto` infers it from the name
(`gpt-realtime-1.5`, `gpt-realtime`, `gpt-realtime-mini`, `gpt-4o-*` are treated as non-reasoning). If the service still
rejects the session, the backend resends a minimal update (instructions + tools only), so tools always register.

Probed on `gpt-realtime-2.1` and `gpt-realtime-1.5`:

- 2.1 accepts `none`, `minimal`, `low`, `medium`, `high` and `xhigh`. 1.5 rejects `reasoning` at every level. That
  error carries no `event_id`, and the backend still recovers.
- 2.1 accepts `parallel_tool_calls` `true` or `false`. 1.5 rejects `true` and accepts `false`.
- Transcription: `whisper-1` works with no extra deployment. `gpt-4o-transcribe` and `gpt-4o-mini-transcribe` pass
  `session.update`, but every turn then fails with `DeploymentNotFound` unless you deploy that model on the resource.

Benchmark on `gpt-realtime-2.1` (`scripts/benchmark_reasoning.py`):

- Setup: real Sonic prompt and tool schemas, text turns, audio output on, stub search.
- Six utterances: single, modification, multi-item, combo, size change and a menu question.
- Reps: 3 each; 5 each for `none`, `low` and `medium`.
- TTFA is `response.create` → first audio delta. "Tool first" counts trials where the model called a tool before
  speaking, leaving the guest in silence.

| effort | trials | correct | TTFA median / p90 | first tool call median / p90 | total median / p90 | tool first |
|---|---|---|---|---|---|---|
| none | 30 | 30/30 | 0.89s / 2.09s | 1.51s / 2.54s | 4.60s / 8.97s | 7 |
| minimal | 18 | 17/18¹ | 2.11s / 5.34s | 1.38s / 8.95s | 4.79s / 9.08s | 11 |
| **low** | 30 | 30/30 | 0.98s / **1.57s** | 2.04s / 2.80s | 5.30s / 8.62s | 0 |
| medium | 30 | 29/30¹ | 0.87s / 1.67s | 2.36s / 3.72s | 5.62s / 8.72s | 0 |
| high | 18 | 18/18 | 1.01s / 1.50s | 2.50s / 4.39s | 6.32s / 9.21s | 0 |
| xhigh | 18 | 18/18 | 0.95s / 1.64s | 2.86s / 4.13s | 6.38s / 8.40s | 0 |
| (omitted) | 18 | 18/18 | 0.91s / 1.60s | 2.47s / 2.76s | 5.81s / 7.50s | 0 |

¹ One service `output_timeout` each; no wrong orders.

Why `low` is the default:

- The TTFA medians for `none` through `xhigh` fall within the service jitter.
- Of the efforts with no errors, only `low` combines a tight TTFA p90 with the fastest tool call (after `none`).
- `none` and `minimal` often call tools before speaking, which gives a long silent p90.
- Higher efforts only add latency.

`parallel_tool_calls` at `low` (multi-item and combo, 6 trials each, all correct):

| value | TTFA median | total median / p90 |
|---|---|---|
| `true` | 0.92s | 6.93s / 8.14s |
| `false` | 0.71s | 8.27s / 10.13s |

`false` serialises search→add pairs and uses about 2× the tokens. Leaving it unset (`null`) already batches calls on 2.1
and is safe on 1.5, so `null` is the default.

## Post-deploy realtime smoke check

`azd deploy` runs `scripts/smoke_realtime.ps1` (Windows) or `scripts/smoke_realtime.sh` (posix) as a `postdeploy`
hook. The hook never fails the deployment: on a problem it prints a loud warning and exits 0. Skip it with
`azd env set SONIC_SKIP_REALTIME_SMOKE true`. Run it by hand with:

```shell
python scripts/smoke_realtime.py                       # endpoint, deployment, tenant, subscription from the azd env
python scripts/smoke_realtime.py --deployment gpt-realtime-2.1-dz
python scripts/smoke_realtime.py --tenant <tenant-id> --subscription <subscription-id>
```

It checks, against the live deployment:

- **Session config.** The bootstrap `session.update`, a relayed browser `session.update` and the minimal fallback each
  come back as `session.updated` with the four tools, `tool_choice: auto`, the instructions, the voice and (on a
  reasoning deployment) the reasoning effort.
- **Transcription.** The realtime model reads a test order aloud ("Hi, can I get a large cherry limeade and a medium
  tots, please?"), and that audio is sent as guest speech with the app's transcription model. The transcript must
  match the phrase word for word, ignoring case, punctuation and spacing, with a similarity of at least 0.85. That
  allows a transcriber's slip ("tops" for "tots") but not an answer. A model that replies "Sure, one large cherry
  limeade…" instead of reading the phrase now fails the check. It used to pass with a note. The phrase is sent as
  `response.instructions` rather than a user turn, because given a user turn `gpt-realtime-2.1` took the order
  instead of reading it (5 of 6 live runs).

Exit codes: 0 all passed, 1 a check failed, 2 could not run (settings, auth or network).

**Tenant.** With `AZURE_OPENAI_EASTUS2_API_KEY` unset, the script uses an Entra ID token, and the token must come from
the Azure OpenAI resource's tenant. On a machine signed in to several tenants, following the active `az`/`azd` default
gave HTTP 400 "Tenant provided in token does not match resource token". The tenant and subscription now come from
`--tenant`/`--subscription`, else `AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID` in the environment, else the azd env.
Each value falls back on its own. The credentials are tried in turn, and every failure is reported:

1. `az` for that subscription. This picks the sign-in that owns it without changing the global `az account` default.
2. `azd`, pinned to the tenant.
3. `az`, pinned to the tenant.

With neither a tenant nor a subscription, the script falls back to `DefaultAzureCredential`. `azd up` grants the
deploying principal "Cognitive Services OpenAI User" on the resource. Right after a first provision, that role
assignment can take a few minutes to apply.

## Scaling, session affinity and the session-token secret

Order state, the resume credential and the reconnect grace hold live in the
backend process's memory. See [order_resume.md](order_resume.md) for the protocol and the `resume:` config keys. Two
rules follow from that:

- **One worker per replica.** `app/Dockerfile` runs gunicorn with `--workers 1`. With two workers, a reconnect has
  about a 50% chance of reaching a process that doesn't have the order. aiohttp is async, so one worker easily carries
  the per-replica session cap (`security.max_concurrent_sessions`).
- **Sticky ingress.** `infra/main.bicep` sets `stickySessionsAffinity: 'sticky'` on the backend Container App
  (`ingress.stickySessions.affinity`). Envoy sets an affinity cookie on the page load, and the browser sends it on the
  websocket upgrade, so a reconnect lands on the same replica. Sticky sessions need single revision mode, which is the
  default in `infra/core/host/container-app.bicep`. Min/max replicas are unchanged (1/5). A resume still fails, and
  falls back to a fresh order, when that replica is gone (scale-in, restart, redeploy).

`/api/auth/session` signs its HMAC tokens with `APP_SESSION_SECRET`. The value is a Container App secret
(`app-session-secret`), so every replica and restart validates every other's tokens. That is required before
`security.require_session_token` can be turned on.

- By default each `azd provision` generates a random value (`newGuid()` twice).
- To keep one value across provisions, pin it in the azd environment:

  ```shell
  azd env set APP_SESSION_SECRET "$(openssl rand -base64 48)"
  ```

- A changed secret changes `APP_SESSION_SECRET_FINGERPRINT` in the template. That rolls a new revision, so all replicas
  restart on the new value together.
- Locally, when `APP_SESSION_SECRET` is unset, the app falls back to a random per-process secret.
- Because sending a secrets list replaces the app's secrets, an `aad-client-secret` that was set out-of-band (EasyAuth
  with `AZURE_AUTH_CLIENT_SECRET` empty) is read back and re-sent on each provision.
