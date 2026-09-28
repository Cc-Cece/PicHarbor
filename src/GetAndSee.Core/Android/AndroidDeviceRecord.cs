namespace GetAndSee.Core.Android;

/// <summary>
/// Represents a registered Android device in an archive's SQLite database.
/// </summary>
/// <param name="DeviceId">Unique device identifier (e.g. GAS-7F32A91C).</param>
/// <param name="Name">Display name of the Android device (e.g. Pixel 8).</param>
/// <param name="CreatedAt">Timestamp when the device was first paired/synced.</param>
/// <param name="LastSeenAt">Timestamp when the device was last connected.</param>
public sealed record AndroidDeviceRecord(
    string DeviceId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt);
