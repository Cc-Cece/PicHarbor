using System.Globalization;
using iMobileDevice;
using iMobileDevice.Afc;
using iMobileDevice.iDevice;
using iMobileDevice.Lockdown;
using iMobileDevice.Plist;
using PicHarbor.Core.Errors;

namespace PicHarbor.Core.Device;

/// <summary>
/// <see cref="IPhoneClient"/> implementation backed by <c>imobiledevice-net</c> (native AFC).
/// </summary>
/// <remarks>
/// Binds <b>only</b> the read path of the library: device enumeration, the lockdown handshake,
/// starting <c>com.apple.afc</c>, directory listing, file stat, and read-only file open. No
/// write, delete, rename, truncate, or directory-create call is referenced anywhere in this class
/// — the prohibition is verified at build time by <c>ReadOnlyContractTests</c> (PROJECT_BRIEF §9.1).
/// </remarks>
public sealed class AfcIPhoneClient : IPhoneClient
{
    private const string ServiceLabel = "picharbor";
    private const string AfcServiceName = "com.apple.afc";

    private static int nativeLoaded;

    private readonly ILibiMobileDevice library = LibiMobileDevice.Instance;

    private iDeviceHandle? device;
    private LockdownClientHandle? lockdown;
    private LockdownServiceDescriptorHandle? afcService;
    private AfcClientHandle? afc;
    private bool disposed;

    private readonly TimeSpan readTimeout;
    private readonly TimeProvider clock;

    /// <summary>Creates a read-only AFC client.</summary>
    /// <param name="readTimeout">
    /// Inactivity timeout applied to the blocking native open/list/stat calls and the read stream
    /// (#25 / #11 / R2). When <see cref="TimeSpan.Zero"/> (the default) the watchdog is disabled and
    /// the native calls block indefinitely, preserving the original unguarded behavior.
    /// </param>
    /// <param name="clock">Time source for the watchdog; defaults to <see cref="TimeProvider.System"/>.</param>
    public AfcIPhoneClient(TimeSpan readTimeout = default, TimeProvider? clock = null)
    {
        this.readTimeout = readTimeout;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public DeviceInfo? Device { get; private set; }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureNativeLibrariesLoaded();

        string udid = SelectFirstDeviceUdid();

        IiDeviceApi idevice = library.iDevice;
        var newError = idevice.idevice_new(out iDeviceHandle deviceHandle, udid);
        if (newError != iDeviceError.Success)
        {
            throw new DeviceException($"Could not open the device (udid {udid}): {newError}.");
        }

        device = deviceHandle;

        ILockdownApi lockdownApi = library.Lockdown;
        var handshake = lockdownApi.lockdownd_client_new_with_handshake(deviceHandle, out LockdownClientHandle lockdownHandle, ServiceLabel);
        if (handshake != LockdownError.Success)
        {
            throw new DeviceException(
                "Could not pair with the iPhone. Unlock it and tap \"Trust This Computer\", then try again " +
                $"(lockdown error: {handshake}).");
        }

        lockdown = lockdownHandle;
        Device = ReadDeviceInfo(lockdownApi, lockdownHandle, udid);

        var startError = lockdownApi.lockdownd_start_service(lockdownHandle, AfcServiceName, out LockdownServiceDescriptorHandle serviceHandle);
        if (startError != LockdownError.Success)
        {
            throw new DeviceException($"Could not start the AFC file service on the device: {startError}.");
        }

        afcService = serviceHandle;

        IAfcApi afcApi = library.Afc;
        var clientError = afcApi.afc_client_new(deviceHandle, serviceHandle, out AfcClientHandle afcHandle);
        if (clientError != AfcError.Success)
        {
            throw new DeviceException($"Could not open the AFC client: {clientError}.");
        }

        afc = afcHandle;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AfcClientHandle client = RequireConnected();
        return GuardAsync(() => ReadDirectoryNative(client, path), cancellationToken);
    }

    private IReadOnlyList<string> ReadDirectoryNative(AfcClientHandle client, string path)
    {
        AfcError error = library.Afc.afc_read_directory(client, path, out System.Collections.ObjectModel.ReadOnlyCollection<string> entries);
        if (error != AfcError.Success)
        {
            throw AfcErrors.ToException(error, $"Could not list \"{path}\" on the device: {error}.");
        }

        List<string> result = new(entries.Count);
        foreach (string entry in entries)
        {
            if (entry is "." or "..")
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    /// <inheritdoc />
    public Task<RemoteFileInfo> GetFileInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AfcClientHandle client = RequireConnected();
        return GuardAsync(() => GetFileInfoNative(client, path), cancellationToken);
    }

    private RemoteFileInfo GetFileInfoNative(AfcClientHandle client, string path)
    {
        AfcError error = library.Afc.afc_get_file_info(client, path, out System.Collections.ObjectModel.ReadOnlyCollection<string> info);
        if (error != AfcError.Success)
        {
            throw AfcErrors.ToException(error, $"Could not read file info for \"{path}\": {error}.");
        }

        return ParseFileInfo(info);
    }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AfcClientHandle client = RequireConnected();
        return GuardAsync(
            () => OpenReadNative(client, path),
            cancellationToken,
            onAbandoned: static settled =>
            {
                // If an abandoned open eventually returns a live handle (the device came back after the
                // stall was already surfaced), close it so the AFC file handle is not leaked.
                if (settled.Status == TaskStatus.RanToCompletion)
                {
                    settled.Result.Dispose();
                }
            });
    }

    private Stream OpenReadNative(AfcClientHandle client, string path)
    {
        // FopenRdonly is the only mode used. Opening in any write/append mode would create or
        // truncate a file on the device; ReadOnlyContractTests fails the build if that ever happens.
        ulong handle = 0;
        AfcError error = library.Afc.afc_file_open(client, path, AfcFileMode.FopenRdonly, ref handle);
        if (error != AfcError.Success)
        {
            throw AfcErrors.ToException(error, $"Could not open \"{path}\" for reading: {error}.");
        }

        return new AfcReadStream(library.Afc, client, handle, path);
    }

    private Task<T> GuardAsync<T>(Func<T> nativeCall, CancellationToken cancellationToken, Action<Task<T>>? onAbandoned = null)
    {
        if (readTimeout <= TimeSpan.Zero)
        {
            // Watchdog disabled (default): run the blocking native call inline, exactly as before.
            return Task.FromResult(nativeCall());
        }

        return DeviceWatchdog.RunWithTimeoutAsync(nativeCall, readTimeout, clock, cancellationToken, onAbandoned);
    }

    private static DeviceInfo ReadDeviceInfo(ILockdownApi lockdownApi, LockdownClientHandle client, string udid)
    {
        // Best-effort enrichment for the summary line; never fatal if the values are unavailable.
        string? name = TryGetLockdownString(lockdownApi, client, "DeviceName");
        string? productType = TryGetLockdownString(lockdownApi, client, "ProductType");
        return new DeviceInfo(udid, name, productType);
    }

    private static string? TryGetLockdownString(ILockdownApi lockdownApi, LockdownClientHandle client, string key)
    {
        try
        {
            if (lockdownApi.lockdownd_get_value(client, null, key, out PlistHandle node) != LockdownError.Success)
            {
                return null;
            }

            using (node)
            {
                LibiMobileDevice.Instance.Plist.plist_get_string_val(node, out string value);
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string SelectFirstDeviceUdid()
    {
        int count = 0;
        var error = library.iDevice.idevice_get_device_list(out System.Collections.ObjectModel.ReadOnlyCollection<string> udids, ref count);
        if (error != iDeviceError.Success || udids is null || udids.Count == 0)
        {
            throw new DeviceException(
                "No iPhone detected. Connect the device with a USB cable, unlock it, and make sure the " +
                "Apple device driver service is running (see the pre-flight guidance).");
        }

        return udids[0];
    }

    private AfcClientHandle RequireConnected()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return afc ?? throw new InvalidOperationException("ConnectAsync must be called before using the device.");
    }

    private static RemoteFileInfo ParseFileInfo(IReadOnlyList<string> info)
    {
        long size = 0;
        DateTimeOffset? modifiedAt = null;
        bool isDirectory = false;

        // afc_get_file_info returns a flat [key, value, key, value, ...] list.
        for (int i = 0; i + 1 < info.Count; i += 2)
        {
            string key = info[i];
            string value = info[i + 1];
            switch (key)
            {
                case "st_size":
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    break;
                case "st_mtime":
                    modifiedAt = ParseAfcTimestamp(value);
                    break;
                case "st_ifmt":
                    isDirectory = value == "S_IFDIR";
                    break;
            }
        }

        return new RemoteFileInfo(size, modifiedAt, isDirectory);
    }

    private static DateTimeOffset? ParseAfcTimestamp(string nanosecondsSinceEpoch)
    {
        if (!long.TryParse(nanosecondsSinceEpoch, NumberStyles.Integer, CultureInfo.InvariantCulture, out long nanos) || nanos <= 0)
        {
            return null;
        }

        long milliseconds = nanos / 1_000_000L;
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void EnsureNativeLibrariesLoaded()
    {
        if (Interlocked.Exchange(ref nativeLoaded, 1) == 1)
        {
            return;
        }

        try
        {
            NativeLibraries.Load();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref nativeLoaded, 0);
            throw new DeviceException(
                "Failed to load the native iMobileDevice libraries. Ensure the application is running as " +
                "win-x64 with the bundled native DLLs present.", ex);
        }
    }

    /// <summary>Releases all AFC, lockdown, and device handles in reverse order of acquisition.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        afc?.Dispose();
        afcService?.Dispose();
        lockdown?.Dispose();
        device?.Dispose();
    }
}
