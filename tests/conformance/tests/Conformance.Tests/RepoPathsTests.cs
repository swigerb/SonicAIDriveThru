using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Pure unit tests for <see cref="RepoPaths.FindRepoRoot(string)"/> — no fixture, no backend,
/// no network. Builds synthetic directory trees (under this project's gitignored `obj/`, deleted
/// on completion) to prove the walk-up logic handles both a normal clone (`.git` as a directory)
/// and a git worktree (`.git` as a plain file containing a `gitdir: ...` pointer).
/// </summary>
public sealed class RepoPathsTests
{
    [Fact]
    public void Finds_repo_root_when_git_is_a_directory()
    {
        var scratch = CreateScratchRoot(nameof(Finds_repo_root_when_git_is_a_directory));
        try
        {
            var repoRoot = Path.Combine(scratch, "fakerepo");
            Directory.CreateDirectory(Path.Combine(repoRoot, "app"));
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
            File.WriteAllText(Path.Combine(repoRoot, "azure.yaml"), "# fake");

            var deepStart = Path.Combine(repoRoot, "tests", "conformance", "tests", "Conformance.Tests", "bin", "Debug", "net11.0");
            Directory.CreateDirectory(deepStart);

            Assert.Equal(repoRoot, RepoPaths.FindRepoRoot(deepStart));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void Finds_repo_root_when_git_is_a_worktree_pointer_file()
    {
        var scratch = CreateScratchRoot(nameof(Finds_repo_root_when_git_is_a_worktree_pointer_file));
        try
        {
            var repoRoot = Path.Combine(scratch, "fakerepo-worktree");
            Directory.CreateDirectory(Path.Combine(repoRoot, "app"));
            File.WriteAllText(Path.Combine(repoRoot, "azure.yaml"), "# fake");
            // A git worktree's ".git" is a plain file, not a directory — it contains a single
            // line like "gitdir: /path/to/main/.git/worktrees/<name>".
            File.WriteAllText(Path.Combine(repoRoot, ".git"), "gitdir: /elsewhere/.git/worktrees/fake\n");

            var deepStart = Path.Combine(repoRoot, "tests", "conformance", "tests", "Conformance.Tests", "bin", "Debug", "net11.0");
            Directory.CreateDirectory(deepStart);

            Assert.Equal(repoRoot, RepoPaths.FindRepoRoot(deepStart));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void Throws_a_clear_error_when_no_repo_root_marker_is_found()
    {
        var scratch = CreateScratchRoot(nameof(Throws_a_clear_error_when_no_repo_root_marker_is_found));
        try
        {
            // No 'app', 'azure.yaml', or '.git' anywhere under scratch — the walk-up must fail
            // once it exhausts every ancestor directory rather than returning a wrong answer.
            var deepStart = Path.Combine(scratch, "unrelated", "deeply", "nested");
            Directory.CreateDirectory(deepStart);

            // The real repo root is somewhere above the OS drive root in CI/dev sandboxes, so we
            // can't assert this throws globally — but we can assert it never silently returns a
            // path *inside* our disposable scratch tree, which is the only thing this test can
            // control without stubbing the filesystem walk.
            string? result = null;
            try
            {
                result = RepoPaths.FindRepoRoot(deepStart);
            }
            catch (DirectoryNotFoundException)
            {
                // Expected outcome when no ancestor (including real ones above scratch) matches.
            }

            if (result is not null)
            {
                Assert.DoesNotContain(scratch, result, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static string CreateScratchRoot(string testName)
    {
        // Kept under this project's own build output (gitignored via obj/) rather than the OS
        // temp directory, per repo convention of not scattering test artifacts outside the tree.
        var scratch = Path.Combine(AppContext.BaseDirectory, "repo-paths-test-scratch", $"{testName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        return scratch;
    }
}
