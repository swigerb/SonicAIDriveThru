# Team Roster

> Sonic AI Drive-Thru Voice Assistant — AI-powered drive-thru ordering experience

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. Does not generate domain artifacts. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Rick | Lead | `.squad/agents/rick/charter.md` | 🏗️ Active |
| Morty | Frontend Dev | `.squad/agents/morty/charter.md` | ⚛️ Active |
| Summer | Backend Dev | `.squad/agents/summer/charter.md` | 🔧 Active |
| Birdperson | Tester | `.squad/agents/birdperson/charter.md` | 🧪 Active |
| Squanchy | DevOps | `.squad/agents/squanchy/charter.md` | ⚙️ Active |
| Unity | AI / Realtime Expert | `.squad/agents/unity/charter.md` | 🤖 Active |
| Scribe | Session Logger | `.squad/agents/scribe/charter.md` | 📋 Silent |
| Ralph | Work Monitor | — | 🔄 Monitor |

## Coding Agent

<!-- copilot-auto-assign: false -->

| Name | Role | Charter | Status |
|------|------|---------|--------|
| @copilot | Coding Agent | — | 🤖 Coding Agent |

### Capabilities

**🟢 Good fit — auto-route when enabled:**
- Bug fixes with clear reproduction steps
- Test coverage (adding missing tests, fixing flaky tests)
- Lint/format fixes and code style cleanup
- Dependency updates and version bumps
- Small isolated features with clear specs
- Boilerplate/scaffolding generation
- Documentation fixes and README updates

**🟡 Needs review — route to @copilot but flag for squad member PR review:**
- Medium features with clear specs and acceptance criteria
- Refactoring with existing test coverage
- API endpoint additions following established patterns
- Migration scripts with well-defined schemas

**🔴 Not suitable — route to squad member instead:**
- Architecture decisions and system design
- Multi-system integration requiring coordination
- Ambiguous requirements needing clarification
- Security-critical changes (auth, encryption, access control)
- Performance-critical paths requiring benchmarking
- Changes requiring cross-team discussion

## Project Context

- **Owner:** Brian Swiger
- **Stack:** Python backend (aiohttp, WebSockets, Azure OpenAI Realtime, Azure AI Search, Azure Speech SDK), React/TypeScript frontend (Vite, Tailwind CSS, shadcn/ui), Bicep IaC, Docker, azd CLI
- **Description:** Voice-driven drive-thru ordering experience showcasing Azure OpenAI GPT-4o Realtime, Azure AI Search, and Azure Container Apps
- **Created:** 2026-03-19
