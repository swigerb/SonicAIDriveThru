# McManus — History

## Project Context
- **Project:** Sonic AI Drive-Thru Voice Assistant — React 18 + TypeScript + Vite frontend
- **Stack:** Radix UI + Tailwind CSS + shadcn/ui, react-use-websocket, i18next, Framer Motion
- **User:** Brian Swiger
- **Key files:** app/frontend/src/App.tsx, app/frontend/src/hooks/useRealtime.tsx, app/frontend/src/types.ts
- **Build output:** app/backend/static/ (Vite builds to backend static dir)

## Learnings

### Frontend Cleanup Pass (code quality, no visual changes)
- **Double ThemeProvider removed:** `index.tsx` wrapped App in `<ThemeProvider>` but `RootApp` in `App.tsx` also wraps with `<ThemeProvider>`. Removed the outer duplicate from `index.tsx`.
- **Dead code removed from App.tsx:** Commented-out `ImageDialog` import and JSX block cleaned out.
- **`any` types eliminated in useAzureSpeech.tsx:** Replaced all `any` callback params with proper types (`ExtensionMiddleTierToolResponse`, `{ transcript: string }`, `unknown` for errors). Also fixed matching callback signatures in `App.tsx`.
- **`useMemo` for static data:** `dummyTranscripts` was in `useState` with setter never used; converted to `useMemo` with `[]` deps. `transcripts` initializer simplified from `(() => [])` to `([])`.
- **menu-panel.tsx simplified:** Removed unnecessary `useEffect`/`useState` for static JSON import — data is now a module-level const. Also removed an empty `<p>` element.
- **grounding-files.tsx bug fixed:** `isAnimating` (a `useRef`) was used directly in a className conditional instead of `isAnimating.current`, causing `overflow-hidden` to always be applied.
- **history-panel.tsx:** Moved `memo(GroundingFile)` from inside the component render body to module scope to avoid re-creating the memo wrapper every render.
- **dummy-data-context.tsx modernized:** Replaced `React.FC<>` pattern with direct function component + named `ReactNode` import.
- **Unused import removed:** `render, screen` import cleaned from `calculate-order-summary.test.tsx` (pure logic test, no DOM rendering).
- **Files modified:** `index.tsx`, `App.tsx`, `useAzureSpeech.tsx`, `dummy-data-context.tsx`, `menu-panel.tsx`, `grounding-files.tsx`, `history-panel.tsx`, `calculate-order-summary.test.tsx`
- **Build:** `tsc -b && vite build` passes. **Tests:** All 13 tests pass.

## Team Feedback (2026-02-25 Cleanup Sprint)
- **Keaton (Lead):** Identified critical ref bug in grounding-files.tsx and comprehensive cleanup roadmap.
- **Fenster (Backend):** Modernized backend to Python 3.11+. All 56 tests pass. No behavioral changes.
- **Hockney (Tester):** Expanded frontend test suite 4→13 tests. All passing. Comprehensive component coverage.
