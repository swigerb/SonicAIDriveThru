namespace Conformance.Harness;

/// <summary>Locates repo-relative paths the harness needs, independent of the test runner's cwd.</summary>
public static class RepoPaths
{
    /// <summary>
    /// Walks up from the test assembly's directory until it finds a folder containing both
    /// `app` and `azure.yaml` (a unique repo-root marker), plus a `.git` entry of either kind,
    /// which is the repo root regardless of whether tests run from `tests/conformance`, the repo
    /// root, or a `dotnet test` working directory in CI. `.git` is a *directory* in a normal
    /// clone but a plain *file* (containing a `gitdir: ...` pointer) inside a git worktree, so
    /// both are accepted rather than requiring `Directory.Exists`.
    /// </summary>
    public static string FindRepoRoot() => FindRepoRoot(AppContext.BaseDirectory);

    /// <summary>Testable overload: walks up from an arbitrary starting directory.</summary>
    public static string FindRepoRoot(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(Path.Combine(dir.FullName, "app")) &&
                File.Exists(Path.Combine(dir.FullName, "azure.yaml")) &&
                (Directory.Exists(gitPath) || File.Exists(gitPath)))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the SonicAIDriveThru repo root by walking up from '{startDirectory}' " +
            "looking for a folder containing 'app', 'azure.yaml', and a '.git' directory or file (worktrees " +
            "use a '.git' file).");
    }

    public static string BackendDirectory(string repoRoot) => Path.Combine(repoRoot, "app", "backend");

    public static string PythonExecutable(string repoRoot) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(repoRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(repoRoot, ".venv", "bin", "python");

    public static string MenuItemsJsonPath(string repoRoot) =>
        Path.Combine(repoRoot, "app", "frontend", "src", "data", "menuItems.json");

    /// <summary>
    /// Issue #9: the golden order-pricing/combo/Route-44 dataset ported from
    /// app/backend/tests/test_order_state*.py, test_tool_calling.py, and test_combo_orders.py,
    /// so both the S1-3 conformance scenarios and a future C# backend's own test suite (S4) can
    /// assert against the exact same cent-accurate cases from one shared file.
    /// </summary>
    public static string GoldenOrderPricingJsonPath(string repoRoot) =>
        Path.Combine(repoRoot, "tests", "conformance", "testdata", "golden-order-pricing.json");

    /// <summary>
    /// Issue #39: the golden category/combo-slot-bucket table (every app/frontend/src/data/
    /// menuItems.json entry mapped to app/backend/menu_utils.py::infer_combo_component's
    /// sides/drinks/none bucket), so both the Python unit tests and this C# scenario suite assert
    /// against the exact same 60-item table from one shared file.
    /// </summary>
    public static string GoldenMenuCategoriesJsonPath(string repoRoot) =>
        Path.Combine(repoRoot, "tests", "conformance", "testdata", "golden-menu-categories.json");

    /// <summary>
    /// app/backend/static is gitignored — populated only by `npm run build` in app/frontend
    /// (vite's outDir points there). aiohttp's `add_static` raises at app-creation time if this
    /// directory doesn't exist, so the Python backend fails immediately on startup without it.
    /// </summary>
    public static string FrontendStaticIndexHtmlPath(string repoRoot) =>
        Path.Combine(repoRoot, "app", "backend", "static", "index.html");
}
