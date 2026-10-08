using PicHarbor.Core.Device;

namespace PicHarbor.FakeDevice;

/// <summary>
/// A fluent description of a fake iPhone's read-only <c>/DCIM</c> tree: the device identity plus an ordered
/// set of <see cref="FakeDeviceFile"/>s (sizes, capture dates, seeded content, optional faults). Build a
/// <see cref="FakeAfcDevice"/> from it with <see cref="Build"/>.
/// </summary>
/// <remarks>
/// The spec is read-faithful by construction: <c>GetFileInfoAsync</c>/<c>ListDirectoryAsync</c> answer from
/// the same file list that <c>OpenReadAsync</c> streams, so a harness pass is meaningful. Faults are
/// deterministic (no wall-clock dependence) and can be applied per file, to every file
/// (<see cref="FailEveryReadWith"/>), or as a whole-device disconnect after N files
/// (<see cref="DisconnectAfterFiles"/>).
/// </remarks>
public sealed class FakeDeviceSpec
{
    /// <summary>Default declared size for a small-photo preset (a few KiB — small but multi-write).</summary>
    public const long SmallPhotoSize = 3 * 1024;

    /// <summary>Default declared size for a large-ish video preset — comfortably larger than the 1&#160;MiB copy buffer.</summary>
    public const long LargeVideoSize = (5 * 1024 * 1024) + 4321;

    private readonly List<FakeDeviceFile> files = [];

    /// <summary>The connected device's identity. Synthetic UDID/name only — never real EUII.</summary>
    public DeviceInfo Device { get; private set; } = new("00008101-000A1B2C3D4E5F60", "Test iPhone", "iPhone13,3");

    /// <summary>When set, the device throws a connection-fatal error once this many file opens have succeeded.</summary>
    public int? DisconnectAfterOpenCount { get; private set; }

    /// <summary>A fault applied to every file that has no fault of its own (e.g. a whole-device "went bad" burst).</summary>
    public ReadFault? DefaultReadFault { get; private set; }

    /// <summary>The files in the virtual tree, in insertion order.</summary>
    public IReadOnlyList<FakeDeviceFile> Files => files;

    /// <summary>Starts a new, empty spec.</summary>
    /// <returns>The new spec, for fluent chaining.</returns>
    public static FakeDeviceSpec Create() => new();

    /// <summary>Sets the device identity reported by <c>ConnectAsync</c>. Use synthetic values only.</summary>
    /// <param name="udid">Synthetic UDID.</param>
    /// <param name="name">Synthetic device name, or <see langword="null"/>.</param>
    /// <param name="productType">Apple product type (device class, not EUII), or <see langword="null"/>.</param>
    /// <returns>This spec, for chaining.</returns>
    public FakeDeviceSpec WithDevice(string udid, string? name, string? productType)
    {
        Device = new DeviceInfo(udid, name, productType);
        return this;
    }

    /// <summary>Adds a file to the virtual tree.</summary>
    /// <param name="path">Absolute AFC path.</param>
    /// <param name="size">Declared size in bytes.</param>
    /// <param name="captureDate">Capture date served as <c>st_mtime</c>, or <see langword="null"/> (unsorted-eligible).</param>
    /// <param name="fault">Optional per-file read fault.</param>
    /// <param name="contentSeed">Optional explicit content seed (defaults to a stable hash of <paramref name="path"/>).</param>
    /// <returns>This spec, for chaining.</returns>
    public FakeDeviceSpec AddFile(string path, long size, DateTimeOffset? captureDate = null, ReadFault? fault = null, int? contentSeed = null)
    {
        files.Add(new FakeDeviceFile(path, size, captureDate, contentSeed, fault));
        return this;
    }

    /// <summary>Adds a small dated photo (HEIC) preset.</summary>
    /// <param name="path">Absolute AFC path.</param>
    /// <param name="captureDate">Capture date.</param>
    /// <param name="fault">Optional per-file read fault.</param>
    /// <returns>This spec, for chaining.</returns>
    public FakeDeviceSpec AddSmallPhoto(string path, DateTimeOffset captureDate, ReadFault? fault = null) =>
        AddFile(path, SmallPhotoSize, captureDate, fault);

    /// <summary>Adds a large-ish dated video preset (exercises the multi-chunk copy loop).</summary>
    /// <param name="path">Absolute AFC path.</param>
    /// <param name="captureDate">Capture date.</param>
    /// <param name="size">Declared size (defaults to <see cref="LargeVideoSize"/>).</param>
    /// <param name="fault">Optional per-file read fault.</param>
    /// <returns>This spec, for chaining.</returns>
    public FakeDeviceSpec AddLargeVideo(string path, DateTimeOffset captureDate, long? size = null, ReadFault? fault = null) =>
        AddFile(path, size ?? LargeVideoSize, captureDate, fault);

    /// <summary>Adds an unsorted-eligible file (no capture date) — it must land in <c>unsorted/</c>.</summary>
    /// <param name="path">Absolute AFC path.</param>
    /// <param name="fault">Optional per-file read fault.</param>
    /// <returns>This spec, for chaining.</returns>
    public FakeDeviceSpec AddUnsorted(string path, ReadFault? fault = null) =>
        AddFile(path, SmallPhotoSize, captureDate: null, fault);

    /// <summary>
    /// Adds a Live-Photo pair (a <c>.HEIC</c> still and its <c>.MOV</c> motion clip sharing a stem and date),
    /// so the summary's Live-Photo detection and same-folder co-location can be asserted.
    /// </summary>
    /// <param name="basePathWithoutExtension">The shared path stem, e.g. <c>/DCIM/100APPLE/IMG_0002</c>.</param>
    /// <param name="captureDate">The shared capture date.</param>
    /// <returns>This spec, for chaining.</returns>
    public FakeDeviceSpec AddLivePhotoPair(string basePathWithoutExtension, DateTimeOffset captureDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePathWithoutExtension);
        AddFile(basePathWithoutExtension + ".HEIC", SmallPhotoSize, captureDate);
        AddFile(basePathWithoutExtension + ".MOV", LargeVideoSize, captureDate);
        return this;
    }

    /// <summary>
    /// Makes the device drop its connection (a connection-fatal error) on the next file open once
    /// <paramref name="successfulOpens"/> opens have succeeded — the "disconnect at file N" shape. Use 0 to
    /// model a device that is gone before the first file.
    /// </summary>
    /// <param name="successfulOpens">Number of file opens that succeed before the disconnect.</param>
    /// <returns>This spec, for chaining.</returns>
    public FakeDeviceSpec DisconnectAfterFiles(int successfulOpens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(successfulOpens);
        DisconnectAfterOpenCount = successfulOpens;
        return this;
    }

    /// <summary>
    /// Applies <paramref name="fault"/> to every file that has no fault of its own — models a whole-device
    /// failure burst (e.g. every read fast-returning empty), which the between-file breaker must catch.
    /// </summary>
    /// <param name="fault">The fault to apply to each otherwise-healthy file.</param>
    /// <returns>This spec, for chaining.</returns>
    public FakeDeviceSpec FailEveryReadWith(ReadFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        DefaultReadFault = fault;
        return this;
    }

    /// <summary>
    /// Adds a representative small library: a few dated photos/videos across two months, an unsorted file,
    /// and a Live-Photo pair. Useful as a baseline for full-copy and organize-layout assertions.
    /// </summary>
    /// <returns>This spec, for chaining.</returns>
    public FakeDeviceSpec AddSmallLibrary()
    {
        AddSmallPhoto("/DCIM/100APPLE/IMG_0001.HEIC", new DateTimeOffset(2024, 8, 15, 9, 30, 0, TimeSpan.Zero));
        AddLargeVideo("/DCIM/100APPLE/IMG_0002.MOV", new DateTimeOffset(2024, 8, 20, 18, 5, 0, TimeSpan.Zero));
        AddSmallPhoto("/DCIM/101APPLE/IMG_0100.HEIC", new DateTimeOffset(2024, 9, 2, 12, 0, 0, TimeSpan.Zero));
        AddLivePhotoPair("/DCIM/101APPLE/IMG_0101", new DateTimeOffset(2024, 9, 3, 7, 45, 0, TimeSpan.Zero));
        AddUnsorted("/DCIM/100APPLE/SCRATCH.DAT");
        return this;
    }

    /// <summary>Returns a copy of this spec with every fault removed — used to "heal" the device for a resume run.</summary>
    /// <returns>A new, fault-free spec with the same device identity and files.</returns>
    public FakeDeviceSpec Healed()
    {
        FakeDeviceSpec healed = new() { Device = Device };
        foreach (FakeDeviceFile file in files)
        {
            healed.files.Add(file.Healed());
        }

        return healed;
    }

    /// <summary>Builds a connected-capable fake device from this spec.</summary>
    /// <returns>A new <see cref="FakeAfcDevice"/>.</returns>
    public FakeAfcDevice Build() => new(this);
}
