"""Rebrand verification tests.

These tests ensure no Dunkin' references remain after the Sonic rebrand.
Every source file (Python, TypeScript, HTML, CSS, JSON) is scanned for
forbidden terms.  Failures report the exact file and line number so the
team can surgically fix stragglers.

Author: Birdperson (Tester)
"""

import re
import sys
import unittest
from pathlib import Path

# ── Paths ────────────────────────────────────────────────────────────────
PROJECT_ROOT = Path(__file__).resolve().parents[3]  # SonicAIDriveThru/
BACKEND_DIR = PROJECT_ROOT / "app" / "backend"
FRONTEND_DIR = PROJECT_ROOT / "app" / "frontend"

# ── Forbidden patterns ───────────────────────────────────────────────────
# Each tuple: (compiled regex, human-readable label)
FORBIDDEN_PATTERNS = [
    (re.compile(r"\bdunkin\b", re.IGNORECASE), "dunkin"),
    (re.compile(r"\bcrew\s+member\b", re.IGNORECASE), "crew member (should be carhop)"),
    (re.compile(r"\bcoffee[-\s]?chat\b", re.IGNORECASE), "coffee-chat (old repo name)"),
]

# File extensions to scan
SCAN_EXTENSIONS = {
    ".py", ".ts", ".tsx", ".js", ".jsx",
    ".html", ".css", ".json", ".md",
    ".yaml", ".yml", ".bicep", ".env-sample",
}

# Directories and files to exclude from scanning
EXCLUDED_DIRS = {
    ".squad", ".git", "node_modules", "__pycache__",
    ".venv", "venv", "env", ".devcontainer", ".copilot",
    ".github", ".vscode",
}

EXCLUDED_FILES = {
    # The upstream attribution file is allowed to reference original names
    "voice_rag_README.md",
    # This test file itself contains the forbidden words by necessity
    "test_rebrand_verification.py",
}


def _should_scan(path: Path) -> bool:
    """Return True if *path* should be included in the rebrand scan."""
    # Suffix check
    if path.suffix not in SCAN_EXTENSIONS:
        return False
    # Excluded file names
    if path.name in EXCLUDED_FILES:
        return False
    # Excluded directories anywhere in the path
    parts = path.relative_to(PROJECT_ROOT).parts
    if any(part in EXCLUDED_DIRS for part in parts):
        return False
    return True


def _collect_source_files() -> list[Path]:
    """Gather every scannable source file under PROJECT_ROOT."""
    return sorted(p for p in PROJECT_ROOT.rglob("*") if p.is_file() and _should_scan(p))


def _scan_for_forbidden(files: list[Path]) -> list[tuple[Path, int, str, str]]:
    """Return a list of (file, line_number, matched_text, label) hits."""
    hits: list[tuple[Path, int, str, str]] = []
    for filepath in files:
        try:
            lines = filepath.read_text(encoding="utf-8", errors="replace").splitlines()
        except Exception:
            continue
        for line_no, line in enumerate(lines, start=1):
            # Skip upstream attribution URLs (e.g., links to the original repo)
            if "github.com/john-carroll-sw/coffee-chat" in line:
                continue
            for pattern, label in FORBIDDEN_PATTERNS:
                if pattern.search(line):
                    hits.append((filepath, line_no, line.strip(), label))
    return hits


# ── Test class ───────────────────────────────────────────────────────────

class TestRebrandVerification(unittest.TestCase):
    """Verify the Dunkin → Sonic rebrand is complete across the codebase."""

    # ── Broad codebase scan ──────────────────────────────────────────

    def test_no_dunkin_references_in_source_files(self):
        """No source file should contain the word 'dunkin' (case-insensitive)."""
        files = _collect_source_files()
        self.assertTrue(len(files) > 0, "Scan found zero files — check PROJECT_ROOT")

        pattern, label = FORBIDDEN_PATTERNS[0]  # dunkin
        hits = []
        for filepath in files:
            try:
                lines = filepath.read_text(encoding="utf-8", errors="replace").splitlines()
            except Exception:
                continue
            for line_no, line in enumerate(lines, start=1):
                if pattern.search(line):
                    rel = filepath.relative_to(PROJECT_ROOT)
                    hits.append(f"  {rel}:{line_no}  →  {line.strip()}")

        self.assertEqual(
            hits, [],
            f"\n{len(hits)} file(s) still reference '{label}':\n" + "\n".join(hits),
        )

    def test_no_crew_member_references(self):
        """'crew member' should have been replaced with 'carhop' everywhere."""
        files = _collect_source_files()
        pattern, label = FORBIDDEN_PATTERNS[1]  # crew member
        hits = []
        for filepath in files:
            try:
                lines = filepath.read_text(encoding="utf-8", errors="replace").splitlines()
            except Exception:
                continue
            for line_no, line in enumerate(lines, start=1):
                if pattern.search(line):
                    rel = filepath.relative_to(PROJECT_ROOT)
                    hits.append(f"  {rel}:{line_no}  →  {line.strip()}")

        self.assertEqual(
            hits, [],
            f"\n{len(hits)} file(s) still reference '{label}':\n" + "\n".join(hits),
        )

    def test_no_coffee_chat_references(self):
        """Old repo name 'coffee-chat' should not appear in source files."""
        files = _collect_source_files()
        pattern, label = FORBIDDEN_PATTERNS[2]  # coffee-chat
        hits = []
        for filepath in files:
            try:
                lines = filepath.read_text(encoding="utf-8", errors="replace").splitlines()
            except Exception:
                continue
            for line_no, line in enumerate(lines, start=1):
                # Skip upstream attribution URLs
                if "github.com/john-carroll-sw/coffee-chat" in line:
                    continue
                if pattern.search(line):
                    rel = filepath.relative_to(PROJECT_ROOT)
                    hits.append(f"  {rel}:{line_no}  →  {line.strip()}")

        self.assertEqual(
            hits, [],
            f"\n{len(hits)} file(s) still reference '{label}':\n" + "\n".join(hits),
        )

    def test_no_forbidden_terms_combined(self):
        """Catch-all: scan every source file for ALL forbidden terms at once."""
        files = _collect_source_files()
        hits = _scan_for_forbidden(files)
        formatted = []
        for filepath, line_no, line_text, label in hits:
            rel = filepath.relative_to(PROJECT_ROOT)
            formatted.append(f"  [{label}] {rel}:{line_no}  →  {line_text}")

        self.assertEqual(
            formatted, [],
            f"\n{len(formatted)} forbidden reference(s) remain:\n" + "\n".join(formatted),
        )

    # ── Targeted file checks ─────────────────────────────────────────

    def test_readme_title_contains_sonic(self):
        """README.md project title/heading must mention 'Sonic'."""
        readme = PROJECT_ROOT / "README.md"
        self.assertTrue(readme.exists(), "README.md not found at project root")
        content = readme.read_text(encoding="utf-8", errors="replace")
        first_heading = ""
        for line in content.splitlines():
            if line.startswith("# "):
                first_heading = line
                break
        self.assertTrue(
            "sonic" in first_heading.lower(),
            f"README.md first heading does not mention Sonic: '{first_heading}'",
        )

    def test_readme_does_not_mention_dunkin(self):
        """README.md must be completely free of Dunkin references."""
        readme = PROJECT_ROOT / "README.md"
        self.assertTrue(readme.exists(), "README.md not found at project root")
        content = readme.read_text(encoding="utf-8", errors="replace")
        hits = []
        for line_no, line in enumerate(content.splitlines(), start=1):
            if re.search(r"\bdunkin\b", line, re.IGNORECASE):
                hits.append(f"  README.md:{line_no}  →  {line.strip()}")
        self.assertEqual(
            hits, [],
            f"\nREADME.md still references Dunkin:\n" + "\n".join(hits),
        )

    def test_frontend_index_html_title_contains_sonic(self):
        """app/frontend/index.html <title> must contain 'Sonic'."""
        index = FRONTEND_DIR / "index.html"
        self.assertTrue(index.exists(), "app/frontend/index.html not found")
        content = index.read_text(encoding="utf-8", errors="replace")
        title_match = re.search(r"<title>(.*?)</title>", content, re.IGNORECASE)
        self.assertIsNotNone(title_match, "No <title> tag found in index.html")
        title_text = title_match.group(1)
        self.assertTrue(
            "sonic" in title_text.lower(),
            f"index.html <title> does not mention Sonic: '{title_text}'",
        )

    def test_frontend_index_html_no_dunkin(self):
        """app/frontend/index.html must not reference Dunkin anywhere."""
        index = FRONTEND_DIR / "index.html"
        self.assertTrue(index.exists(), "app/frontend/index.html not found")
        content = index.read_text(encoding="utf-8", errors="replace")
        hits = []
        for line_no, line in enumerate(content.splitlines(), start=1):
            if re.search(r"\bdunkin\b", line, re.IGNORECASE):
                hits.append(f"  index.html:{line_no}  →  {line.strip()}")
        self.assertEqual(
            hits, [],
            f"\nindex.html still references Dunkin:\n" + "\n".join(hits),
        )

    def test_backend_system_prompt_mentions_sonic(self):
        """The system prompt must reference 'Sonic'."""
        # System prompt externalized to YAML — read from the source file
        prompt_yaml = BACKEND_DIR / "prompts" / "sonic" / "system_prompt.yaml"
        if prompt_yaml.exists():
            prompt_text = prompt_yaml.read_text(encoding="utf-8", errors="replace")
        else:
            # Fallback: check app.py for inline system_message
            app_py = BACKEND_DIR / "app.py"
            self.assertTrue(app_py.exists(), "app/backend/app.py not found")
            content = app_py.read_text(encoding="utf-8", errors="replace")
            match = re.search(r"system_message\s*=\s*\((.*?)\)", content, re.DOTALL)
            self.assertIsNotNone(match, "Could not locate system_message in app.py or prompts/sonic/system_prompt.yaml")
            prompt_text = match.group(1)

        self.assertTrue(
            "sonic" in prompt_text.lower(),
            "system prompt does not mention 'Sonic'",
        )

    def test_backend_system_prompt_no_dunkin(self):
        """The backend system prompt must NOT reference 'Dunkin'."""
        prompt_yaml = BACKEND_DIR / "prompts" / "sonic" / "system_prompt.yaml"
        if prompt_yaml.exists():
            prompt_text = prompt_yaml.read_text(encoding="utf-8", errors="replace")
        else:
            app_py = BACKEND_DIR / "app.py"
            self.assertTrue(app_py.exists(), "app/backend/app.py not found")
            content = app_py.read_text(encoding="utf-8", errors="replace")
            match = re.search(r"system_message\s*=\s*\((.*?)\)", content, re.DOTALL)
            self.assertIsNotNone(match, "Could not locate system_message in app.py or prompts/sonic/system_prompt.yaml")
            prompt_text = match.group(1)

        hits = []
        for i, line in enumerate(prompt_text.splitlines(), start=1):
            if re.search(r"\bdunkin\b", line, re.IGNORECASE):
                hits.append(f"  system prompt line {i}: {line.strip()}")

        self.assertEqual(
            hits, [],
            f"\nsystem prompt still references Dunkin:\n" + "\n".join(hits),
        )

    def test_backend_system_prompt_uses_carhop_not_crew_member(self):
        """The system prompt should say 'carhop', not 'crew member'."""
        prompt_yaml = BACKEND_DIR / "prompts" / "sonic" / "system_prompt.yaml"
        if prompt_yaml.exists():
            prompt_text = prompt_yaml.read_text(encoding="utf-8", errors="replace").lower()
        else:
            app_py = BACKEND_DIR / "app.py"
            content = app_py.read_text(encoding="utf-8", errors="replace")
            match = re.search(r"system_message\s*=\s*\((.*?)\)", content, re.DOTALL)
            self.assertIsNotNone(match, "Could not locate system_message in app.py or prompts/sonic/system_prompt.yaml")
            prompt_text = match.group(1).lower()

        self.assertNotIn(
            "crew member", prompt_text,
            "system prompt still uses 'crew member' — should be 'carhop'",
        )

    # ── Scan finds files sanity check ────────────────────────────────

    def test_scan_finds_expected_file_types(self):
        """Sanity: the scanner should find .py, .ts/.tsx, .html, and .md files."""
        files = _collect_source_files()
        extensions_found = {p.suffix for p in files}
        for ext in (".py", ".html", ".md"):
            self.assertIn(
                ext, extensions_found,
                f"Scanner did not find any {ext} files — check SCAN_EXTENSIONS / EXCLUDED_DIRS",
            )


if __name__ == "__main__":
    unittest.main()
