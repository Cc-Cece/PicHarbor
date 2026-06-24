namespace GetAndSee.FakeDevice;

/// <summary>
/// Deterministic, position-addressable synthetic file content for the fake device. The byte at any
/// absolute file offset is a pure function of the file's <paramref name="seed"/> and that offset, so an
/// arbitrarily large file needs no backing allocation (a 400&#160;MB declared size can be "parked" after a
/// few delivered MB) and a test can recompute the exact expected bytes to assert a copy is byte-identical.
/// </summary>
/// <remarks>
/// The mixing function is a SplitMix64-style avalanche of <c>(seed, offset)</c>. It is position-addressable
/// — the result for a given offset never depends on the chunk boundaries a read happened to use — so a
/// resumed copy that re-chunks the same file still produces identical bytes and a meaningful SHA-256.
/// </remarks>
public static class FakeContent
{
    /// <summary>
    /// Fills <paramref name="destination"/> with the deterministic content bytes for <paramref name="seed"/>,
    /// starting at the absolute file <paramref name="offset"/>.
    /// </summary>
    /// <param name="destination">The span to fill.</param>
    /// <param name="offset">The absolute file offset of <paramref name="destination"/>'s first byte.</param>
    /// <param name="seed">The per-file content seed.</param>
    public static void Fill(Span<byte> destination, long offset, int seed)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = ByteAt(offset + i, seed);
        }
    }

    /// <summary>
    /// Materializes the first <paramref name="length"/> bytes of the content for <paramref name="seed"/>
    /// into a new array. For assertion-sized files (the on-disk copy is read back with
    /// <see cref="File.ReadAllBytes(string)"/>, which is itself bounded to <see cref="int"/>).
    /// </summary>
    /// <param name="length">Number of bytes to materialize.</param>
    /// <param name="seed">The per-file content seed.</param>
    /// <returns>A freshly allocated array of the deterministic bytes.</returns>
    public static byte[] Materialize(int length, int seed)
    {
        byte[] bytes = new byte[length];
        Fill(bytes, 0, seed);
        return bytes;
    }

    private static byte ByteAt(long offset, int seed)
    {
        ulong x = unchecked(((ulong)offset * 0x9E3779B97F4A7C15UL) + (uint)seed + 0x165667B19E3779F9UL);
        x = unchecked((x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL);
        x = unchecked((x ^ (x >> 27)) * 0x94D049BB133111EBUL);
        x ^= x >> 31;
        return unchecked((byte)x);
    }
}
