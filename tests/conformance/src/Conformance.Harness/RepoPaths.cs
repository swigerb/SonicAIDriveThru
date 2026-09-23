namespace Conformance.Harness;

/// <summary>Locates repo-relative paths the harness needs, independent of the test runner's cwd.</summary>
public static class RepoPaths
{
    /// <summary>
    /// Walks up from the test assembly's directory until it finds a folder containing both
    /// `app` and `.git`, which is the repo root regardless of whether tests run from
    /// `tests/conformance`, the repo root, or a `dotnet test` working directory in CI.
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "app")) &&
                Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the SonicAIDriveThru repo root by walking up from '{AppContext.BaseDirectory}' " +
            "looking for a folder containing both 'app' and '.git'.");
    }

    public static string BackendDirectory(string repoRoot) => Path.Combine(repoRoot, "app", "backend");

    public static string PythonExecutable(string repoRoot) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(repoRoot, ".venv", "Scripts", "python.exe")
            : Path.Combine(repoRoot, ".venv", "bin", "python");

    public static string MenuItemsJsonPath(string repoRoot) =>
        Path.Combine(repoRoot, "app", "frontend", "src", "data", "menuItems.json");
}
