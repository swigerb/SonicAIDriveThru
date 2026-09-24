namespace Conformance.Harness;

/// <summary>
/// Pure decision logic for whether the Category=Browser suite should skip or fail when no
/// supported browser channel is installed on this machine — the browser equivalent of
/// <see cref="DotnetPlaceholderPolicy"/> (issue #26). The suite must never download a browser
/// (msedge/chrome are expected to already be installed, exactly like the Python
/// scripts/e2e_order_resume.py script this suite ports); it only ever launches an
/// already-installed channel. CI runner images (GitHub-hosted ubuntu-latest/windows-latest) both
/// ship at least one of msedge/chrome, so seeing "no channel" in CI means the runner image
/// itself regressed, not a legitimate reason to skip — exactly the same reasoning
/// <see cref="DotnetPlaceholderPolicy"/> applies to the S2 .NET-backend placeholder.
/// </summary>
public static class BrowserChannelPolicy
{
    // The same well-known per-OS install locations Playwright's own "channel" launch option
    // resolves against — checked directly (rather than by attempting a launch) so a genuinely
    // browser-less developer machine gets a clear, fast, pre-flight skip instead of an opaque
    // Playwright launch exception after already having started the backend-under-test.
    private static readonly (string Channel, string[] WindowsPaths)[] WindowsCandidates =
    [
        ("msedge", [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        ]),
        ("chrome", [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        ]),
    ];

    // Linux (GitHub-hosted ubuntu-latest runners) ship Google Chrome stable at this path;
    // self-hosted/other Linux images are covered by the macOS/Linux Playwright-channel-resolution
    // fallback below when neither well-known path exists.
    private static readonly (string Channel, string[] LinuxPaths)[] LinuxCandidates =
    [
        ("chrome", ["/usr/bin/google-chrome", "/usr/bin/google-chrome-stable"]),
        ("msedge", ["/usr/bin/microsoft-edge", "/usr/bin/microsoft-edge-stable"]),
    ];

    /// <summary>
    /// The first installed channel Playwright can launch without downloading anything (msedge,
    /// then chrome) via a direct filesystem probe, or null if neither well-known path was found.
    /// A null result on a CI runner is treated as "let Playwright's own launch attempt be the
    /// final word" by the caller (<see cref="ShouldSkip"/> only permits a skip outside CI) rather
    /// than as proof no browser exists — this probe is a fast pre-flight check, not exhaustive.
    /// </summary>
    public static string? DetectInstalledChannel()
    {
        var candidates = OperatingSystem.IsWindows()
            ? WindowsCandidates.Select(c => (c.Channel, Paths: c.WindowsPaths))
            : LinuxCandidates.Select(c => (c.Channel, Paths: c.LinuxPaths));
        foreach (var (channel, paths) in candidates)
        {
            if (paths.Any(File.Exists))
            {
                return channel;
            }
        }
        return null;
    }

    public const string ReasonPrefix =
        "No supported browser channel (msedge or chrome) was found installed on this machine.";

    /// <summary>
    /// True only when no channel was detected AND this doesn't look like CI — mirrors
    /// <see cref="DotnetPlaceholderPolicy.ShouldSkip"/>: CI must never silently skip real-browser
    /// coverage, so there is deliberately no way to force a skip there.
    /// </summary>
    public static bool ShouldSkip(string? detectedChannel, bool isCi) => detectedChannel is null && !isCi;

    /// <summary>
    /// The message to use for an xUnit dynamic skip (<see cref="ShouldSkip"/> true) or to throw
    /// as a hard failure otherwise (mirrors <see cref="DotnetPlaceholderPolicy.BuildException"/>).
    /// </summary>
    public static string BuildMessage(string? detectedChannel, bool isCi) =>
        ShouldSkip(detectedChannel, isCi)
            ? ReasonPrefix + " Skipping because this doesn't look like CI — install Microsoft " +
              "Edge or Google Chrome to run the Category=Browser suite locally."
            : ReasonPrefix + " This FAILS the suite by default so CI can never silently skip " +
              "real-browser coverage — every supported CI runner image ships Edge or Chrome, so " +
              "seeing this in CI means the runner image itself is missing an expected browser.";
}
