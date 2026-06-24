using System.Globalization;
using GetAndSee.Core.Device;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Summary;
using GetAndSee.FakeDevice;
using GetAndSee.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace GetAndSee.Tests.E2E;

/// <summary>
/// Builds the opt-in <c>-p:FakeDevice=true</c> <c>get-and-see.exe</c> once and exposes its path to the E2E
/// class. The build goes to an isolated temp directory (deleted on dispose), so it never overwrites a normal
/// build and nothing it produces is shipped. <c>SelfContained=false</c> (per the CLI csproj) is enough: the
/// fake-device path never loads the native AFC DLLs, so a framework-dependent build runs on the installed
/// .NET 10 runtime.
/// </summary>
public sealed class FakeDeviceExeFixture : IAsyncLifetime
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    private string? outputDirectory;

    /// <summary>Absolute path to the built fake-device <c>get-and-see.exe</c>.</summary>
    public string ExePath { get; private set; } = string.Empty;

    /// <summary>Builds the fake-device EXE before any test in the class runs.</summary>
    public async ValueTask InitializeAsync()
    {
        string repoRoot = LocateRepoRoot();
        string projectPath = Path.Combine(repoRoot, "src", "GetAndSee.Cli", "GetAndSee.Cli.csproj");
        outputDirectory = Path.Combine(Path.GetTempPath(), "gas-e2e-exe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        // The implicit restore must run WITH -p:FakeDevice=true so the conditional GetAndSee.FakeDevice
        // ProjectReference is resolved (a normal restore omits it), hence no --no-restore here.
        ProcessRunResult build = await ProcessRunner.RunAsync(
            "dotnet",
            ["build", projectPath, "-c", "Release", "-r", "win-x64", "-p:FakeDevice=true", "-o", outputDirectory, "-v", "minimal"],
            workingDirectory: repoRoot,
            environment: null,
            BuildTimeout,
            TestContext.Current.CancellationToken).ConfigureAwait(false);

        if (build.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Building the FakeDevice EXE failed (exit {build.ExitCode}).\n" +
                $"stdout:\n{build.StandardOutput}\nstderr:\n{build.StandardError}");
        }

        ExePath = Path.Combine(outputDirectory, "get-and-see.exe");
        if (!File.Exists(ExePath))
        {
            throw new InvalidOperationException($"FakeDevice EXE not found at '{ExePath}' after a successful build.");
        }
    }

    /// <summary>Deletes the isolated build output.</summary>
    public ValueTask DisposeAsync()
    {
        if (outputDirectory is not null)
        {
            try
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of build artifacts.
            }
        }

        return ValueTask.CompletedTask;
    }

    private static string LocateRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "get-and-see.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repo root (get-and-see.sln) above '{AppContext.BaseDirectory}'.");
    }
}

/// <summary>
/// Real-process E2E tests: build the opt-in <c>-p:FakeDevice=true</c> <c>get-and-see.exe</c> and run it as a
/// subprocess against the <c>GAS_FAKE_DEVICE</c> fake — asserting the things the in-process harness cannot,
/// because it cannot restart a process: the real CLI's <b>exit codes</b> (0 clean, 3 lost device) and a
/// <b>cross-process resume</b> (one process interrupts, a second healed process resumes byte-identical). The
/// in-process harness still owns the fast inner-loop coverage; this lane proves the shipped binary's
/// process-level contract. No hardware-parity is claimed — the fake is a software double, not an iPhone.
/// </summary>
[Trait("Category", "E2E")]
public sealed class FakeDeviceExeE2ETests : IClassFixture<FakeDeviceExeFixture>
{
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(2);

    private readonly FakeDeviceExeFixture fixture;

    /// <summary>Creates the test class over the shared built-EXE <paramref name="fixture"/>.</summary>
    /// <param name="fixture">The fixture that built the fake-device EXE once for the class.</param>
    public FakeDeviceExeE2ETests(FakeDeviceExeFixture fixture) => this.fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Clean_run_exits_zero_organizes_files_on_disk_and_writes_summary()
    {
        using TempDirectory destination = new();

        ProcessRunResult run = await RunCopyAsync("small", destination.Path);

        run.ExitCode.ShouldBe(0, Diagnostics(run));
        File.Exists(Path.Combine(destination.Path, SummaryWriter.FileName))
            .ShouldBeTrue("the real EXE must write summary.txt at the destination root");
        AssertArchiveByteIdentical(destination.Path, FakeDeviceSpecParser.Parse("small"));
    }

    [Fact]
    public async Task Disconnect_spec_makes_the_real_process_exit_three()
    {
        using TempDirectory destination = new();

        ProcessRunResult run = await RunCopyAsync("small;disconnect-after=2", destination.Path);

        run.ExitCode.ShouldBe(3, Diagnostics(run));
    }

    [Fact]
    public async Task Cross_process_resume_skips_done_files_and_finishes_byte_identical()
    {
        using TempDirectory destination = new();

        // Run 1 — a real process interrupted mid-copy (the device drops after two files) → exit 3, leaving a
        // resumable SQLite journal on disk.
        ProcessRunResult interrupted = await RunCopyAsync("small;disconnect-after=2", destination.Path);
        interrupted.ExitCode.ShouldBe(3, Diagnostics(interrupted));

        // Run 2 — a SECOND, healed process resumes against the same destination → exit 0, the already-done
        // files skipped (not re-streamed), and the final archive byte-identical to the source.
        ProcessRunResult resumed = await RunCopyAsync("small", destination.Path);
        resumed.ExitCode.ShouldBe(0, Diagnostics(resumed));

        ParseSkippedCount(resumed.StandardOutput).ShouldBeGreaterThanOrEqualTo(
            2, $"the resume run should skip the files run 1 already finished.\n{Diagnostics(resumed)}");
        AssertArchiveByteIdentical(destination.Path, FakeDeviceSpecParser.Parse("small"));
    }

    private Task<ProcessRunResult> RunCopyAsync(string fakeSpec, string destination) =>
        ProcessRunner.RunAsync(
            fixture.ExePath,
            ["copy", "--dest", destination, "--no-dashboard"],
            workingDirectory: Path.GetDirectoryName(fixture.ExePath),
            environment: new Dictionary<string, string> { [FakeDeviceGate.EnvironmentVariable] = fakeSpec },
            CopyTimeout,
            Token);

    private static string Diagnostics(ProcessRunResult run) =>
        $"exit {run.ExitCode}\nstdout:\n{run.StandardOutput}\nstderr:\n{run.StandardError}";

    private static int ParseSkippedCount(string stdout)
    {
        foreach (string rawLine in stdout.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("Skipped:", StringComparison.Ordinal))
            {
                continue;
            }

            // Format: "Skipped:    2 (already done)". Take the leading integer of the value; it is N0-format,
            // so strip a possible thousands separator before parsing.
            string value = line["Skipped:".Length..].TrimStart();
            string digits = new(value.TakeWhile(character => char.IsDigit(character) || character == ',').ToArray());
            digits = digits.Replace(",", string.Empty, StringComparison.Ordinal);
            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
            {
                return count;
            }
        }

        return -1;
    }

    private static void AssertArchiveByteIdentical(string destinationRoot, FakeDeviceSpec spec)
    {
        // Expected layout mirrors the in-process harness: the real EXE runs ExifMetadataExtractor over the
        // synthetic FakeContent bytes, which are not valid EXIF, so it returns MediaMetadata.Empty and the
        // organizer falls back to each file's capture-date (mtime) — exactly what passing MediaMetadata.Empty
        // here reproduces. The copy runs without --organize-by, so a brand-new archive uses the default flat
        // YYYY-MM (month) layout — what the shipped organize path produces.
        DateFolderOrganizer organizer = new();
        foreach (FakeDeviceFile file in spec.Files)
        {
            RemoteFile remote = new(file.Path, file.Size, file.CaptureDate);
            string relative = organizer.GetRelativeDestination(remote, MediaMetadata.Empty, OrganizeScheme.Month);
            string full = Path.Combine(destinationRoot, relative);

            File.Exists(full).ShouldBeTrue($"{file.Path} should be copied to {relative}");
            byte[] expected = FakeContent.Materialize((int)file.Size, file.ContentSeed);
            File.ReadAllBytes(full).ShouldBe(expected, $"{file.Path} content must be byte-identical");
        }
    }
}
