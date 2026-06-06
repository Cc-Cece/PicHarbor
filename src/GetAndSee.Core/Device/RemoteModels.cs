namespace GetAndSee.Core.Device;

/// <summary>
/// A single media file discovered on the device, as yielded by the DCIM enumerator.
/// </summary>
/// <param name="Path">Absolute AFC path on the device, e.g. <c>/DCIM/100APPLE/IMG_0001.HEIC</c>.</param>
/// <param name="Size">Size in bytes as reported by AFC (<c>st_size</c>).</param>
/// <param name="ModifiedAt">Last-modified timestamp from AFC (<c>st_mtime</c>), or <see langword="null"/> if unavailable.</param>
public sealed record RemoteFile(string Path, long Size, DateTimeOffset? ModifiedAt);

/// <summary>
/// File metadata returned by a read-only <c>afc_get_file_info</c> call.
/// </summary>
/// <param name="Size">Size in bytes (<c>st_size</c>). Zero for directories.</param>
/// <param name="ModifiedAt">Last-modified timestamp (<c>st_mtime</c>), or <see langword="null"/> if it could not be parsed.</param>
/// <param name="IsDirectory">True when AFC reports the entry as a directory (<c>st_ifmt == S_IFDIR</c>).</param>
public sealed record RemoteFileInfo(long Size, DateTimeOffset? ModifiedAt, bool IsDirectory);

/// <summary>
/// Identity of the connected device, captured once at connect time for the run summary and manifest.
/// </summary>
/// <param name="Udid">Unique device identifier reported by usbmuxd.</param>
/// <param name="Name">User-assigned device name (e.g. "Denis's iPhone"), or <see langword="null"/> if unavailable.</param>
/// <param name="ProductType">Apple product type (e.g. "iPhone13,3"), or <see langword="null"/> if unavailable.</param>
public sealed record DeviceInfo(string Udid, string? Name, string? ProductType);
