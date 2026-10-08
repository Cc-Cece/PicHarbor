namespace PicHarbor.FakeDevice;

/// <summary>
/// One file in a <see cref="FakeDeviceSpec"/>'s virtual <c>/DCIM</c> tree: an absolute device path, the
/// size AFC reports for it, an optional capture date (served as the file's <c>st_mtime</c>, which drives
/// date-folder organization), a deterministic content seed, and an optional per-file read fault.
/// </summary>
/// <remarks>
/// The content is never materialized here — <see cref="ContentSeed"/> plus <see cref="FakeContent"/> make
/// the bytes a pure function of offset, so even a multi-hundred-MB declared <see cref="Size"/> costs nothing
/// until (and only as far as) it is actually read.
/// </remarks>
public sealed class FakeDeviceFile
{
    /// <summary>Creates a file descriptor for the virtual tree.</summary>
    /// <param name="path">Absolute AFC path, e.g. <c>/DCIM/100APPLE/IMG_0001.HEIC</c>.</param>
    /// <param name="size">Size AFC reports for the file (the copier verifies the received byte count against it).</param>
    /// <param name="captureDate">Capture date served as <c>st_mtime</c>, or <see langword="null"/> for an unsorted-eligible file.</param>
    /// <param name="contentSeed">Deterministic content seed; defaults to a stable hash of <paramref name="path"/>.</param>
    /// <param name="fault">Per-file read fault, or <see langword="null"/> for a faithful read.</param>
    public FakeDeviceFile(string path, long size, DateTimeOffset? captureDate, int? contentSeed = null, ReadFault? fault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        Size = size;
        CaptureDate = captureDate;
        ContentSeed = contentSeed ?? StableSeed(path);
        Fault = fault;
    }

    /// <summary>Absolute AFC path of the file.</summary>
    public string Path { get; }

    /// <summary>Size AFC reports for the file.</summary>
    public long Size { get; }

    /// <summary>Capture date served as the file's <c>st_mtime</c>, or <see langword="null"/>.</summary>
    public DateTimeOffset? CaptureDate { get; }

    /// <summary>Deterministic <see cref="FakeContent"/> seed for the file's bytes.</summary>
    public int ContentSeed { get; }

    /// <summary>The read fault to inject when this file is opened, or <see langword="null"/>.</summary>
    public ReadFault? Fault { get; }

    /// <summary>Returns a copy of this file with its read fault removed — used to "heal" a device for a resume run.</summary>
    /// <returns>An identical file (same path, size, date, seed) with no fault.</returns>
    public FakeDeviceFile Healed() => new(Path, Size, CaptureDate, ContentSeed, fault: null);

    private static int StableSeed(string path)
    {
        // A process-stable FNV-1a hash (string.GetHashCode is randomized per run, which would break the
        // "same paths => identical content" invariant that byte-identical resume assertions rely on).
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in path)
            {
                hash = (hash ^ c) * 16777619;
            }

            return (int)hash;
        }
    }
}
