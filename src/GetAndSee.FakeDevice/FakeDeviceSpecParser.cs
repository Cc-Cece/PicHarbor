namespace GetAndSee.FakeDevice;

/// <summary>
/// Parses the compact <c>GAS_FAKE_DEVICE</c> spec string into a <see cref="FakeDeviceSpec"/> for the opt-in
/// EXE seam. Grammar: <c>preset[;modifier[;modifier]...]</c>.
/// </summary>
/// <remarks>
/// <para>Presets: <c>small</c> (a representative small library) and <c>empty</c> (no files).</para>
/// <para>
/// Modifiers: <c>disconnect-after=N</c> (the device drops its connection after N file opens succeed) and
/// <c>fail-every</c> (every read returns empty, driving the between-file breaker). Example:
/// <c>GAS_FAKE_DEVICE=small;disconnect-after=2</c>.
/// </para>
/// <para>This is intentionally tiny; it covers the cross-process scenarios the seam exists to demonstrate.</para>
/// </remarks>
public static class FakeDeviceSpecParser
{
    /// <summary>Parses a <c>GAS_FAKE_DEVICE</c> spec string.</summary>
    /// <param name="spec">The spec string (e.g. <c>small;disconnect-after=2</c>).</param>
    /// <returns>The configured <see cref="FakeDeviceSpec"/>.</returns>
    /// <exception cref="FormatException">The preset or a modifier is unrecognized or malformed.</exception>
    public static FakeDeviceSpec Parse(string spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec);
        string[] parts = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string preset = parts[0];
        FakeDeviceSpec result =
            Eq(preset, "small") ? FakeDeviceSpec.Create().AddSmallLibrary() :
            Eq(preset, "empty") ? FakeDeviceSpec.Create() :
            throw new FormatException($"Unknown GAS_FAKE_DEVICE preset '{preset}'. Known presets: small, empty.");

        for (int i = 1; i < parts.Length; i++)
        {
            ApplyModifier(result, parts[i]);
        }

        return result;
    }

    private static void ApplyModifier(FakeDeviceSpec spec, string modifier)
    {
        string[] keyValue = modifier.Split('=', 2, StringSplitOptions.TrimEntries);
        string key = keyValue[0];

        if (Eq(key, "disconnect-after"))
        {
            spec.DisconnectAfterFiles(ParseCount(keyValue, modifier));
        }
        else if (Eq(key, "fail-every"))
        {
            spec.FailEveryReadWith(ReadFault.Empty);
        }
        else
        {
            throw new FormatException(
                $"Unknown GAS_FAKE_DEVICE modifier '{modifier}'. Known modifiers: disconnect-after=N, fail-every.");
        }
    }

    private static int ParseCount(string[] keyValue, string modifier)
    {
        if (keyValue.Length != 2 || !int.TryParse(keyValue[1], out int count) || count < 0)
        {
            throw new FormatException($"Modifier '{modifier}' needs a non-negative integer, e.g. disconnect-after=2.");
        }

        return count;
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
