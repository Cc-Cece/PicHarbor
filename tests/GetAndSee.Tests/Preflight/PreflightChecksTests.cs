using GetAndSee.Core.Errors;
using GetAndSee.Core.Preflight;
using GetAndSee.Core.Util;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.Preflight;

public sealed class PreflightChecksTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly PreflightChecks checks = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public void Writable_destination_passes()
    {
        Should.NotThrow(() => checks.EnsureDestinationWritable(directory.Path));
    }

    [Fact]
    public void Non_writable_destination_throws()
    {
        // Create a file, then try to use a path *inside* that file as a directory.
        string filePath = Path.Combine(directory.Path, "a-file");
        File.WriteAllText(filePath, "x");
        string impossible = Path.Combine(filePath, "child");

        Should.Throw<PreflightException>(() => checks.EnsureDestinationWritable(impossible));
    }

    [Fact]
    public void Sufficient_free_space_passes()
    {
        Should.NotThrow(() => checks.EnsureSufficientFreeSpace(directory.Path, 0));
    }

    [Fact]
    public void Insufficient_free_space_throws()
    {
        const long nineHundredTerabytes = 900L * 1024 * 1024 * 1024 * 1024;
        Should.Throw<PreflightException>(() => checks.EnsureSufficientFreeSpace(directory.Path, nineHundredTerabytes));
    }

    [Fact]
    public void Writable_check_passes_when_the_write_probe_path_exceeds_260_chars()
    {
        // R6 / #39 follow-up: pre-flight is the FIRST destination-root touch, and its write-probe
        // filename (~61 chars) crosses MAX_PATH at a SHALLOWER root than the 14-char journal DB. Without
        // routing the dir-create + probe through LongPath.ToExtended, a deep destination throws a
        // misleading "not writable" here on a stock Windows box (LongPathsEnabled=0) — before the
        // long-path-safe journal is ever opened. SQLite's native VFS ignores the registry key, which is
        // why QA (which had the key on) only saw the DataSource crash, not this.
        string deepRoot = CreateRootWhereProbeExceedsMaxPath();
        try
        {
            // Machine-independent wiring check: the probe path the method builds genuinely exceeds the
            // legacy limit while the root itself stays under it (so the root dir is creatable even
            // unprefixed and the PROBE is isolated as the first offender), and the transform the method
            // now applies yields a verbatim \\?\ path. These are deterministic on any machine — they do
            // not depend on the LongPathsEnabled registry key.
            int probePathLength = deepRoot.Length + 1 + WriteProbeFileNameLength;
            deepRoot.Length.ShouldBeLessThan(260, "the root must stay under MAX_PATH so the probe is the first offender");
            probePathLength.ShouldBeGreaterThan(260, "the write-probe path must cross MAX_PATH for the test to exercise the fix");
            LongPath.ToExtended(deepRoot).ShouldStartWith(@"\\?\");

            // Behavioral guard (the real regression test): end-to-end, this must not throw, and the
            // directory must be created. On a stock box (e.g. the GitHub windows-latest CI runner) this
            // FAILS before the fix — the unprefixed probe write throws PreflightException — and PASSES
            // after. On a long-paths-enabled dev box it passes either way; the test never relies on the
            // registry key being off in order to pass, so it stays green everywhere.
            Should.NotThrow(() => checks.EnsureDestinationWritable(deepRoot));
            Directory.Exists(LongPath.ToExtended(deepRoot)).ShouldBeTrue();
            // The probe is created and deleted within the call — nothing leaks at the destination root.
            Directory.GetFiles(LongPath.ToExtended(deepRoot), ".get-and-see-write-probe-*.tmp").ShouldBeEmpty();
        }
        finally
        {
            // The root is < 260 but a leaked probe (only if the call threw mid-way) would be > 260, so
            // delete via the \\?\ prefix to be safe.
            string extendedRoot = LongPath.ToExtended(deepRoot);
            if (Directory.Exists(extendedRoot))
            {
                Directory.Delete(extendedRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Writable_check_routes_the_directory_create_through_the_long_path_prefix()
    {
        // Machine-independent wiring guard — proves EnsureDestinationWritable routes its filesystem
        // touches through LongPath.ToExtended WITHOUT depending on the LongPathsEnabled registry key, so
        // it catches a regression on the long-paths-enabled dev/QA box too (where the >260 behavioral
        // test above cannot reproduce the limit). Windows strips a trailing dot from a path segment
        // during normalization, but the verbatim \\?\ form preserves it. So the destination is created
        // with its trailing dot intact — observable only via the \\?\ prefix — if and only if the method
        // prefixes the path. Remove the ToExtended wiring and this fails on any machine.
        string dottedRoot = Path.Combine(directory.Path, "longpath-wiring-probe.");
        string verbatimDotted = LongPath.ToExtended(dottedRoot);
        try
        {
            Should.NotThrow(() => checks.EnsureDestinationWritable(dottedRoot));

            Directory.Exists(verbatimDotted).ShouldBeTrue(
                "EnsureDestinationWritable must create the destination via LongPath.ToExtended (verbatim " +
                @"\\?\), which preserves the trailing dot; an unprefixed create would strip it.");
        }
        finally
        {
            if (Directory.Exists(verbatimDotted))
            {
                Directory.Delete(verbatimDotted, recursive: true);
            }
        }
    }

    /// <summary>Length of the write-probe filename <c>.get-and-see-write-probe-{N:32}.tmp</c>.</summary>
    private const int WriteProbeFileNameLength = 61;

    /// <summary>
    /// Builds a destination root under the temp dir that stays below MAX_PATH (so the root directory is
    /// creatable even without the long-path prefix) while <c>&lt;root&gt;\.get-and-see-write-probe-….tmp</c>
    /// lands past 260 chars — isolating the write-probe as the earliest #39 offender.
    /// </summary>
    private string CreateRootWhereProbeExceedsMaxPath()
    {
        const int targetRootLength = 240; // < 260 root, but 240 + 1 + 61 = 302 > 260 for the probe path
        int padLength = targetRootLength - directory.Path.Length - 1;
        padLength.ShouldBeGreaterThan(0, "the temp path is too long to build the long-path fixture");
        padLength.ShouldBeLessThan(255, "a single path component must stay within the NTFS 255-char limit");
        return Path.Combine(directory.Path, new string('p', padLength));
    }
}
