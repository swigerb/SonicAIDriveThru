# ADR-001: Persona architecture for the unified drive-thru demo

- **Status:** Proposed. Must be reviewed with Brian before any P2 work starts.
- **Date:** 2026-09-25
- **Issues:** #19 (P1 spike), including the design for #51. Part of epic #6.
- **Deciders:** Brian Swiger (owner), Rick (lead)
- **Full design:** [`docs/persona-architecture.md`](../persona-architecture.md)

## Context

We run three forks of the same VoiceRAG drive-thru demo: Sonic (this repo), McDonald's and Dunkin. They share
about 90% of their code, but each brand's rules live in hard-coded Python: name-keyed tables and substring
keywords in `menu_utils.py`, `order_state.py` and `tools.py`, an inline prompt string in Dunkin's `app.py`, and
brand colors hard-coded in the React components. Sonic is also far ahead on hardening (S1 and S1.5: relay
security, resume, rate-limit recovery, exact money, the conformance suite).

Brian's goal is one app, with a Python backend (the reference) and a C# (.NET 11 RC 1) backend, where you switch
personas to get Sonic, McDonald's or Dunkin, and every brand keeps its rules. The roadmap is personas first
(P1 design, P2 unified Python app), then one C# port of the unified app (S2 to S6).

The design doc diffs all three repos and classifies every brand difference (51 rows). The result: almost
everything that differs is data (prompts, menu, sizes, prices, happy-hour windows, extras lists, theme, copy,
assets). Only one behavior needs code that one brand alone uses (McDonald's meal-number lookup), and even that
can be a named strategy driven by data.

## Decision

1. **Data-first persona packs.** Each brand is a folder, `personas/<id>/`, containing `persona.json` (rules
   and UI manifest, validated by `personas/persona.schema.json`), `prompts/*.yaml` (today's prompt files,
   moved), `menu/menuItems.json` (with the #51 per-item fields) and `assets/` (logo, favicon, apology clips,
   demo data). Adding a brand means adding a folder, not code.
2. **One contract, two backends.** Python and C# load the same pack files, reject any pack that fails the
   schema at startup, and expose the same HTTP and WebSocket contract. Brand behavior comes from shared engines
   (sizes, bundles, extras, happy hour, off-menu rules) that read the pack. Where data truly cannot express a
   behavior, the pack names a **strategy** from a small closed set that both backends implement and the
   conformance suite pins. P2 needs exactly one: `searchQueryRewrite: "meal_numbers"` for McDonald's.
3. **Persona is chosen per session, within a per-deployment allow-list.** The browser opens
   `/realtime?persona=<id>`. The backend binds that persona for the life of the session, including resume.
   `PERSONAS` (allow-list) and `DEFAULT_PERSONA` are deployment env vars. Leaving out `?persona=` gives the
   default, so today's Sonic URL keeps working. A deployment with one persona behaves like today's
   single-brand app. Switching personas mid-conversation is not supported. The frontend shows a picker only
   when more than one persona is enabled, and a switch starts a new session.
4. **#51 is part of the contract.** Every on-menu classification comes from per-item fields in the persona's
   `menuItems.json` (`comboSlot`, `happyHourDiscounted`, `aliases`, `bundle`, `requiresMachine`, `isExtra`).
   The name-keyed tables leave the code. The off-menu keyword fallback survives only as ordered,
   word-bounded rules in `persona.json`, and it can never fill a combo side slot. The golden category table is
   checked against the data, not generated from it.
5. **The frontend themes itself at runtime.** A `PersonaProvider` fetches `/api/personas/<id>` and applies
   theme tokens as CSS variables. It also sets the title, favicon, copy, legal line, menu and clips. The
   hard-coded brand hex values go away. This is the approved exception to the "no frontend changes" rule.
6. **The unified app lives in this repo.** The McDonald's and Dunkin repos stay live and get security fixes
   only (McDonald's #6, Dunkin #11) until the unified app reaches persona parity and Brian signs off. Then they
   are tagged, marked with a README redirect and archived.
7. **Brand-only side features are out of P2.** McDonald's local mode (Phi-4, Piper, Whisper), Dunkin's crew
   dashboard, CRM simulator and Azure Local / k8s / flux edge deployment stay in the archived repos' history.
   The Azure Speech toggle is dead code in all three repos, so it is dropped.

## Consequences

**Good**

- One codebase and one conformance suite. A fourth brand is a folder plus golden files.
- The C# port (#12 to #16) is done once and is persona-aware from the start. There are no name tables to
  transcribe, which was the point of #51.
- Demos get one URL, and switching brands takes two clicks. Once the siblings retire, one container app
  replaces three, and S7 adds one C# app instead of three.
- The three existing Search indexes are reused, so the free Search tier's three-index cap is not a problem.

**Costs and risks**

- The Python backend's module-level globals (business rules, timezone, menu map, prompt loader, search client)
  become per-session persona objects. P2 does this refactor in three behavior-neutral, Sonic-only steps behind
  the existing suite before any new brand lands.
- The conformance suite gains a persona dimension. The ordering scenarios run about three times, and the
  persona-specific scenarios are new.
- The existing "no Dunkin words" guards (`test_rebrand_verification.py`, `locales.test.ts`) must be inverted:
  brand words may appear only inside their own pack.
- Porting the McDonald's and Dunkin rules exposes their substring-keyword bugs. We port the intent, and every
  deliberate deviation gets a row in the persona's golden file.
- While the sibling demos are still live, they share Search indexes with the unified app. Ingestion must keep
  document ids and fields compatible.

## Alternatives considered

| Option | Why not |
| --- | --- |
| One deployment per brand (`PERSONA=sonic` env var) | Simplest code, but three URLs, three always-on apps (six after S7), three Entra redirect URIs, and no in-demo switch. Still possible through the allow-list: a deployment with `PERSONAS=sonic` is exactly this. |
| Per-brand code plug-ins (Python module or C# class per brand) | Every brand rule would be written twice, once per backend, and could drift. Data plus shared engines keeps one source of truth. |
| Keep three repos and share a library | Three CI gates, three deployments and three hardening backlogs. The S1.5 fixes had to be hand-ported twice (McDonald's #6, Dunkin #11). |
| Switch persona mid-session | The voice locks after the first audio, the order rules differ by brand, and a prompt swap mid-conversation confuses the model. A new session is cleaner. |
| Generate the golden tables from the menu data | Then a wrong field would be copied into the golden file and never fail. Checking the golden file against the data keeps it an independent oracle. |

## Open questions

These are numbered in the design doc, section 14. The ones that block P2: the switching model (Q1), which
personas go on the main URL (Q2), and off-menu fallback: keep or remove (Q4). #64 (floats) is listed there as
Q3 and is not decided here.
