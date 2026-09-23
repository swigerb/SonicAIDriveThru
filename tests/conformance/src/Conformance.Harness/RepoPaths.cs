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
    /// app/backend/static is gitignored — populated only by `npm run build` in app/frontend
    /// (vite's outDir points there). aiohttp's `add_static` raises at app-creation time if this
    /// directory doesn't exist, so the Python backend fails immediately on startup without it.
    /// </summary>
    public static string FrontendStaticIndexHtmlPath(string repoRoot) =>
        Path.Combine(repoRoot, "app", "backend", "static", "index.html");
}
