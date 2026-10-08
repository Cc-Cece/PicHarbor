using PicHarbor.Core.Device;
using PicHarbor.Core.Errors;

namespace PicHarbor.FakeDevice;

/// <summary>
/// A full fake implementation of the read-only <see cref="IPhoneClient"/> contract, backed by a
/// <see cref="FakeDeviceSpec"/>'s virtual <c>/DCIM</c> tree. It answers <c>ListDirectoryAsync</c>,
/// <c>GetFileInfoAsync</c>, and <c>OpenReadAsync</c> spec-faithfully and injects deterministic faults, so the
/// whole copy pipeline can run end-to-end in CI with no real device.
/// </summary>
/// <remarks>
/// <para>
/// Lives in test-support only — never referenced by <c>PicHarbor.Core</c>/<c>Cli</c>, so the shipped binary
/// and the build-time read-only contract are unaffected. Like the contract, this fake exposes <b>only</b>
/// read operations; there is no write/delete/rename surface to call.
/// </para>
/// <para>
/// For stall/spin scenarios a test starts a run, awaits <see cref="ReadParked"/> or
/// <see cref="DisposeSpinStarted"/>, advances the shared <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>
/// to trip the watchdog, asserts, then calls <see cref="ReleaseStalledReads"/> / <see cref="ReleaseDisposeSpins"/>
/// to unwind the orphaned native-read analogue exactly as the OS would on a real terminate.
/// </para>
/// </remarks>
public sealed class FakeAfcDevice : IPhoneClient
{
    private readonly FakeDeviceSpec spec;
    private readonly Dictionary<string, FakeDeviceFile> filesByPath;
    private readonly Dictionary<string, SortedSet<string>> childrenByDirectory;
    private readonly List<ScriptedReadStream> createdStreams = [];
    private readonly TaskCompletionSource readParked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource disposeSpinStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();

    private int openReadCount;
    private bool connected;
    private bool disposed;

    /// <summary>Creates a fake device over <paramref name="spec"/>.</summary>
    /// <param name="spec">The virtual-tree and fault description.</param>
    public FakeAfcDevice(FakeDeviceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        this.spec = spec;
        (filesByPath, childrenByDirectory) = BuildTree(spec.Files);
    }

    /// <inheritdoc />
    public DeviceInfo? Device { get; private set; }

    /// <summary>How many times <c>OpenReadAsync</c> has been called (a parked/healthy open both count).</summary>
    public int OpenReadCount => Volatile.Read(ref openReadCount);

    /// <summary>Completes once any scripted read has begun blocking on a <see cref="ReadFaultKind.Park"/>.</summary>
    public Task ReadParked => readParked.Task;

    /// <summary>Completes once any scripted dispose has begun busy-spinning (a <see cref="ReadFaultKind.SpinOnDispose"/>).</summary>
    public Task DisposeSpinStarted => disposeSpinStarted.Task;

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        Device = spec.Device;
        connected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        cancellationToken.ThrowIfCancellationRequested();

        string key = Normalize(path);
        if (!childrenByDirectory.TryGetValue(key, out SortedSet<string>? children))
        {
            throw new DeviceException($"Could not list \"{path}\" on the device: ObjectNotFound.");
        }

        return Task.FromResult<IReadOnlyList<string>>([.. children]);
    }

    /// <inheritdoc />
    public Task<RemoteFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        cancellationToken.ThrowIfCancellationRequested();

        string key = Normalize(path);
        if (filesByPath.TryGetValue(key, out FakeDeviceFile? file))
        {
            return Task.FromResult(new RemoteFileInfo(file.Size, file.CaptureDate, IsDirectory: false));
        }

        if (childrenByDirectory.ContainsKey(key))
        {
            return Task.FromResult(new RemoteFileInfo(0, null, IsDirectory: true));
        }

        throw new DeviceException($"Could not stat \"{path}\" on the device: ObjectNotFound.");
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        cancellationToken.ThrowIfCancellationRequested();

        int call = Interlocked.Increment(ref openReadCount);
        if (spec.DisconnectAfterOpenCount is int limit && call > limit)
        {
            // The whole device is gone (a cable yank): every subsequent open fails connection-fatal.
            throw new DeviceConnectionLostException();
        }

        if (!filesByPath.TryGetValue(Normalize(path), out FakeDeviceFile? file))
        {
            throw new DeviceException($"Could not open \"{path}\" for reading: ObjectNotFound.");
        }

        ScriptedReadStream stream = new(file.ContentSeed, file.Size, file.Fault ?? spec.DefaultReadFault, file.Path);
        lock (gate)
        {
            createdStreams.Add(stream);
        }

        SignalWhenCompleted(stream.ParkedReadStarted, readParked);
        SignalWhenCompleted(stream.DisposeSpinStarted, disposeSpinStarted);
        return Task.FromResult<Stream>(stream);
    }

    /// <summary>Releases every parked read (returning empty), so an orphaned stall can unwind in teardown.</summary>
    public void ReleaseStalledReads()
    {
        foreach (ScriptedReadStream stream in SnapshotStreams())
        {
            stream.ReleasePark();
        }
    }

    /// <summary>Releases every spinning dispose, the analogue of the OS reaping the pinned thread on terminate.</summary>
    public void ReleaseDisposeSpins()
    {
        foreach (ScriptedReadStream stream in SnapshotStreams())
        {
            stream.ReleaseSpin();
        }
    }

    /// <inheritdoc />
    public void Dispose() => disposed = true;

    private ScriptedReadStream[] SnapshotStreams()
    {
        lock (gate)
        {
            return [.. createdStreams];
        }
    }

    private static void SignalWhenCompleted(Task source, TaskCompletionSource target) =>
        _ = source.ContinueWith(
            static (_, state) => ((TaskCompletionSource)state!).TrySetResult(),
            target,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void RequireConnected()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!connected)
        {
            throw new DeviceException("The device is not connected. Call ConnectAsync first.");
        }
    }

    private static (Dictionary<string, FakeDeviceFile>, Dictionary<string, SortedSet<string>>) BuildTree(
        IReadOnlyList<FakeDeviceFile> specFiles)
    {
        Dictionary<string, FakeDeviceFile> byPath = new(StringComparer.Ordinal);
        Dictionary<string, SortedSet<string>> children = new(StringComparer.Ordinal)
        {
            // Always present so an empty library lists an empty /DCIM rather than throwing.
            [Normalize(DcimEnumerator.DefaultRoot)] = new SortedSet<string>(StringComparer.Ordinal),
        };

        foreach (FakeDeviceFile file in specFiles)
        {
            string filePath = Normalize(file.Path);
            byPath[filePath] = file;

            // Register the file and every ancestor directory as a child of its parent.
            string current = filePath;
            while (true)
            {
                string parent = ParentOf(current);
                string name = NameOf(current);
                if (!children.TryGetValue(parent, out SortedSet<string>? siblings))
                {
                    siblings = new SortedSet<string>(StringComparer.Ordinal);
                    children[parent] = siblings;
                }

                siblings.Add(name);
                if (parent.Length == 0 || parent == "/")
                {
                    break;
                }

                current = parent;
            }
        }

        return (byPath, children);
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        string trimmed = path.Replace('\\', '/');
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    private static string ParentOf(string normalizedPath)
    {
        int slash = normalizedPath.LastIndexOf('/');
        return slash <= 0 ? "/" : normalizedPath[..slash];
    }

    private static string NameOf(string normalizedPath)
    {
        int slash = normalizedPath.LastIndexOf('/');
        return slash < 0 ? normalizedPath : normalizedPath[(slash + 1)..];
    }
}
