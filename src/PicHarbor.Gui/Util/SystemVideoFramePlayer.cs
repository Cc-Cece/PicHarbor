using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using WinRT;

namespace PicHarbor.Gui.Util;

/// <summary>
/// Plays a local video through Windows.Media.Playback and copies each frame into a WPF bitmap.
/// WPF MediaElement stays black for these HEVC .MOV files. This uses the same media pipeline as
/// MediaComposition, which already returns a real frame.
/// </summary>
public sealed class SystemVideoFramePlayer : IDisposable
{
    private const int D3D11SdkVersion = 7;
    private const int DriverTypeHardware = 1;
    private const int DriverTypeWarp = 5;
    private const uint CreateDeviceBgraSupport = 0x20;
    private const int FormatBgra = 87;
    private const int UsageDefault = 0;
    private const int UsageStaging = 3;
    private const uint BindRenderTarget = 0x20;
    private const uint BindShaderResource = 0x8;
    private const uint CpuAccessRead = 0x20000;
    private const int CreateTexture2DSlot = 5;
    private const int MapSlot = 14;
    private const int UnmapSlot = 15;
    private const int CopyResourceSlot = 47;
    private const uint MapRead = 1;

    private static readonly Guid DxgiSurfaceId = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");

    private readonly Dispatcher dispatcher;
    private readonly object gate = new();
    private Windows.Media.Playback.MediaPlayer? player;
    private int generation;
    private bool disposed;
    private bool blitQueued;
    private byte[]? latestPixels;
    private int latestWidth;
    private int latestHeight;
    private IntPtr device;
    private IntPtr context;
    private IntPtr renderTexture;
    private IntPtr stagingTexture;
    private IDirect3DSurface? surface;
    private int surfaceWidth;
    private int surfaceHeight;
    private CreateTexture2DDelegate? createTexture2D;
    private MapDelegate? map;
    private UnmapDelegate? unmap;
    private CopyResourceDelegate? copyResource;

    public SystemVideoFramePlayer(Dispatcher dispatcher, bool muted = true)
    {
        this.dispatcher = dispatcher;
        player = new Windows.Media.Playback.MediaPlayer
        {
            IsVideoFrameServerEnabled = true,
            AutoPlay = false,
            IsMuted = muted,
            Volume = muted ? 0 : 1
        };
        player.MediaOpened += OnMediaOpened;
        player.MediaEnded += OnMediaEnded;
        player.MediaFailed += OnMediaFailed;
        player.VideoFrameAvailable += OnVideoFrameAvailable;
    }

    public WriteableBitmap? Frame { get; private set; }

    public TimeSpan Duration { get; private set; }

    public TimeSpan Position
    {
        get
        {
            try
            {
                return player?.PlaybackSession.Position ?? TimeSpan.Zero;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                return TimeSpan.Zero;
            }
        }
        set
        {
            try
            {
                if (player != null)
                {
                    player.PlaybackSession.Position = value < TimeSpan.Zero ? TimeSpan.Zero : value;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        }
    }

    public event Action<TimeSpan>? Opened;

    public event Action? FrameUpdated;

    public event Action? Ended;

    public event Action? Failed;

    public void Open(string path)
    {
        int token = generation;
        _ = OpenCoreAsync(path, token);
    }

    public void Play()
    {
        try
        {
            player?.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void Pause()
    {
        try
        {
            player?.Pause();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void SetMuted(bool muted)
    {
        try
        {
            if (player != null)
            {
                player.IsMuted = muted;
                player.Volume = muted ? 0 : 1;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        Windows.Media.Playback.MediaPlayer? current;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            generation++;
            current = player;
            player = null;
            if (current != null)
            {
                current.MediaOpened -= OnMediaOpened;
                current.MediaEnded -= OnMediaEnded;
                current.MediaFailed -= OnMediaFailed;
                current.VideoFrameAvailable -= OnVideoFrameAvailable;
            }

            try
            {
                ReleaseSurface();
                Release(ref context);
                Release(ref device);
            }
            catch (InvalidComObjectException)
            {
            }
        }

        if (current != null)
        {
            try
            {
                current.Pause();
                current.Source = null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }

            try
            {
                current.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        Frame = null;
    }

    private async Task OpenCoreAsync(string path, int token)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            if (token != generation || player == null)
            {
                return;
            }

            player.Source = MediaSource.CreateFromStorageFile(file);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SystemVideoFramePlayer] open failed: {ex.Message}");
            if (token == generation)
            {
                _ = dispatcher.BeginInvoke(() =>
                {
                    if (token == generation)
                    {
                        Failed?.Invoke();
                    }
                });
            }
        }
    }

    private void OnMediaOpened(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        if (disposed || !ReferenceEquals(sender, player))
        {
            return;
        }

        uint width = 0;
        uint height = 0;
        TimeSpan duration = TimeSpan.Zero;
        try
        {
            MediaPlaybackSession session = sender.PlaybackSession;
            width = session.NaturalVideoWidth;
            height = session.NaturalVideoHeight;
            duration = session.NaturalDuration;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        if (width == 0 || height == 0 || !EnsureSurface((int)width, (int)height))
        {
            dispatcher.BeginInvoke(() =>
            {
                if (!disposed)
                {
                    Failed?.Invoke();
                }
            });
            return;
        }

        Duration = duration;
        int token = generation;
        dispatcher.BeginInvoke(() =>
        {
            if (token == generation && !disposed)
            {
                Opened?.Invoke(duration);
            }
        });
    }

    private void OnMediaEnded(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        if (disposed || !ReferenceEquals(sender, player))
        {
            return;
        }

        int token = generation;
        dispatcher.BeginInvoke(() =>
        {
            if (token == generation && !disposed)
            {
                Ended?.Invoke();
            }
        });
    }

    private void OnMediaFailed(Windows.Media.Playback.MediaPlayer sender, Windows.Media.Playback.MediaPlayerFailedEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine($"[SystemVideoFramePlayer] media failed: {args.ErrorMessage}");
        if (disposed || !ReferenceEquals(sender, player))
        {
            return;
        }

        int token = generation;
        dispatcher.BeginInvoke(() =>
        {
            if (token == generation && !disposed)
            {
                Failed?.Invoke();
            }
        });
    }

    private void OnVideoFrameAvailable(Windows.Media.Playback.MediaPlayer sender, object args)
    {
        byte[]? pixels;
        int width;
        int height;
        lock (gate)
        {
            // The media thread can deliver a frame while the UI thread is closing this player.
            // The D3D surface has to stay alive for the whole copy, or CopyFrameToVideoSurface faults.
            if (disposed
                || !ReferenceEquals(sender, player)
                || surface == null
                || stagingTexture == IntPtr.Zero
                || renderTexture == IntPtr.Zero
                || context == IntPtr.Zero
                || copyResource == null
                || map == null
                || unmap == null)
            {
                return;
            }

            try
            {
                sender.CopyFrameToVideoSurface(surface);
                copyResource(context, stagingTexture, renderTexture);
                int hr = map(context, stagingTexture, 0, MapRead, 0, out MappedSubresource mapped);
                if (hr < 0 || mapped.Data == IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    width = surfaceWidth;
                    height = surfaceHeight;
                    int rowBytes = width * 4;
                    if (width <= 0 || height <= 0 || mapped.RowPitch < (uint)rowBytes)
                    {
                        return;
                    }

                    pixels = new byte[rowBytes * height];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(mapped.Data, y * (int)mapped.RowPitch), pixels, y * rowBytes, rowBytes);
                    }
                }
                finally
                {
                    unmap(context, stagingTexture, 0);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemVideoFramePlayer] frame copy failed: {ex.Message}");
                return;
            }
        }

        lock (gate)
        {
            latestPixels = pixels;
            latestWidth = width;
            latestHeight = height;
            if (blitQueued)
            {
                return;
            }

            blitQueued = true;
        }

        int token = generation;
        dispatcher.BeginInvoke(DispatcherPriority.Render, () => PublishFrame(token));
    }

    private void PublishFrame(int token)
    {
        byte[]? pixels;
        int width;
        int height;
        lock (gate)
        {
            blitQueued = false;
            pixels = latestPixels;
            width = latestWidth;
            height = latestHeight;
        }

        if (token != generation || disposed || pixels == null || width <= 0 || height <= 0)
        {
            return;
        }

        if (Frame == null || Frame.PixelWidth != width || Frame.PixelHeight != height)
        {
            Frame = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        }

        Frame.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        FrameUpdated?.Invoke();
    }

    private bool EnsureSurface(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        lock (gate)
        {
            if (surface != null && surfaceWidth == width && surfaceHeight == height)
            {
                return true;
            }

            ReleaseSurface();
            if (!EnsureDevice())
            {
                return false;
            }

            var renderDesc = new Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = FormatBgra,
                SampleCount = 1,
                Usage = UsageDefault,
                BindFlags = BindRenderTarget | BindShaderResource
            };
            var stagingDesc = renderDesc;
            stagingDesc.Usage = UsageStaging;
            stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = CpuAccessRead;

            int hr = createTexture2D!(device, ref renderDesc, IntPtr.Zero, out renderTexture);
            if (hr < 0)
            {
                return false;
            }

            hr = createTexture2D!(device, ref stagingDesc, IntPtr.Zero, out stagingTexture);
            if (hr < 0)
            {
                ReleaseSurface();
                return false;
            }

            IntPtr dxgiSurface = IntPtr.Zero;
            IntPtr inspectable = IntPtr.Zero;
            try
            {
                Guid surfaceId = DxgiSurfaceId;
                hr = Marshal.QueryInterface(renderTexture, in surfaceId, out dxgiSurface);
                if (hr < 0)
                {
                    ReleaseSurface();
                    return false;
                }

                hr = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out inspectable);
                if (hr < 0 || inspectable == IntPtr.Zero)
                {
                    ReleaseSurface();
                    return false;
                }

                surface = MarshalInterface<IDirect3DSurface>.FromAbi(inspectable);
                Marshal.Release(inspectable);
                inspectable = IntPtr.Zero;
                surfaceWidth = width;
                surfaceHeight = height;
                return true;
            }
            finally
            {
                if (dxgiSurface != IntPtr.Zero)
                {
                    Marshal.Release(dxgiSurface);
                }

                if (inspectable != IntPtr.Zero)
                {
                    Marshal.Release(inspectable);
                }
            }
        }
    }

    private bool EnsureDevice()
    {
        if (device != IntPtr.Zero && context != IntPtr.Zero && createTexture2D != null)
        {
            return true;
        }

        int hr = D3D11CreateDevice(
            IntPtr.Zero,
            DriverTypeHardware,
            IntPtr.Zero,
            CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3D11SdkVersion,
            out device,
            out _,
            out context);
        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                DriverTypeWarp,
                IntPtr.Zero,
                CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3D11SdkVersion,
                out device,
                out _,
                out context);
        }

        if (hr < 0 || device == IntPtr.Zero || context == IntPtr.Zero)
        {
            return false;
        }

        createTexture2D = GetMethod<CreateTexture2DDelegate>(device, CreateTexture2DSlot);
        map = GetMethod<MapDelegate>(context, MapSlot);
        unmap = GetMethod<UnmapDelegate>(context, UnmapSlot);
        copyResource = GetMethod<CopyResourceDelegate>(context, CopyResourceSlot);
        return true;
    }

    private void ReleaseSurface()
    {
        // CsWinRT surfaces are not classic COM objects. Dropping the reference lets the
        // runtime release the DXGI surface. ReleaseComObject throws for that wrapper.
        try
        {
            if (surface is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex) when (ex is InvalidComObjectException or ObjectDisposedException)
        {
        }

        surface = null;

        Release(ref renderTexture);
        Release(ref stagingTexture);
        surfaceWidth = 0;
        surfaceHeight = 0;
    }

    private static void Release(ref IntPtr comPointer)
    {
        IntPtr pointer = comPointer;
        comPointer = IntPtr.Zero;
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        _ = GetMethod<ReleaseDelegate>(pointer, 2)(pointer);
    }

    private static T GetMethod<T>(IntPtr comObject, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(comObject);
        IntPtr method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11SurfaceFromDXGISurface", ExactSpelling = true, PreserveSig = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(IntPtr self, ref Texture2DDesc desc, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(IntPtr self, IntPtr resource, uint subresource, uint mapType, uint mapFlags, out MappedSubresource mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(IntPtr self, IntPtr resource, uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(IntPtr self, IntPtr destination, IntPtr source);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(IntPtr self);
}
