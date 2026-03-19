# Contributing Guidelines

Thank you for helping improve the Sonic AI Drive Thru! These steps keep the repository healthy as it moves into its new GitHub home.

## Getting started

1. Fork the repository and clone your fork (`git clone https://github.com/swigerb/SonicAIDriveThru.git`).
2. Install prerequisites listed in the README (Azure Developer CLI, Node.js 20+, Python 3.11+, Docker, and Git).
3. Copy the backend and frontend `.env-sample` files to `.env` and supply your Azure resource details.
4. Run `pwsh ./scripts/start.ps1` (or `./scripts/start.sh`) to launch the full stack locally before making UI or API changes.

## Branching and pull requests

- Create feature branches off `main` using the pattern `feature/<short-description>`.
- Keep pull requests focused on a single change; document any UI updates with screenshots or GIFs when possible.
- Reference GitHub Issues that the change addresses so the public backlog stays in sync.

## Testing checklist

Before submitting a pull request:

1. `cd app/frontend && npm run test`
2. `cd app/frontend && npm run build` (updates the static assets served by the backend)
3. `cd app/backend && python -m unittest discover -s tests`
4. Run `ruff check app/backend` if you modify Python files.

## Documentation updates

- Update `README.md`, `DEPLOY.md`, or `DEMO.md` when flows or commands change.
- Keep new architecture diagrams or screenshots inside the `docs/` folder so they stay version-controlled.
- Open an issue when you add new backlog items so the "Backlog" document mirrors GitHub Issues.

By following these steps, we can keep the repo ready for public consumption and make future releases predictable.
