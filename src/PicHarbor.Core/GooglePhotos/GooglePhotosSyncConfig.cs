namespace PicHarbor.Core.GooglePhotos;

/// <summary>
/// Configuration parameters for synchronizing media to Google Photos via gpmc.
/// </summary>
public sealed class GooglePhotosSyncConfig
{
    /// <summary>Authentication method (default: OAuthCookie).</summary>
    public GooglePhotosAuthMethod AuthMethod { get; set; } = GooglePhotosAuthMethod.OAuthCookie;

    /// <summary>Google Embedded Setup oauth_token cookie value.</summary>
    public string OAuthTokenCookie { get; set; } = string.Empty;

    /// <summary>Google Photos mobile auth data (GmsCore / photos.native token body).</summary>
    public string AuthData { get; set; } = string.Empty;

    /// <summary>
    /// Returns the effective credential.
    /// Reusable Android GmsCore auth_data is prioritized when available (contains androidId= and Token=),
    /// because Google's oauth_token cookie is single-use and invalidated upon the initial exchange.
    /// If no reusable auth_data exists yet, returns OAuthTokenCookie for the initial exchange.
    /// </summary>
    public string EffectiveAuthCredential
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(AuthData) && AuthData.Contains("androidId=") && AuthData.Contains("Token="))
            {
                return AuthData;
            }

            if (AuthMethod == GooglePhotosAuthMethod.OAuthCookie)
            {
                return !string.IsNullOrWhiteSpace(OAuthTokenCookie) ? OAuthTokenCookie : AuthData;
            }

            return !string.IsNullOrWhiteSpace(AuthData) ? AuthData : OAuthTokenCookie;
        }
    }

    /// <summary>Proxy URL in protocol://username:password@host:port format.</summary>
    public string Proxy { get; set; } = string.Empty;

    /// <summary>HTTP request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Album assignment mode.</summary>
    public GooglePhotosAlbumMode AlbumMode { get; set; } = GooglePhotosAlbumMode.None;

    /// <summary>Custom album name when AlbumMode is CustomName.</summary>
    public string? CustomAlbumName { get; set; }

    /// <summary>Album media key when AlbumMode is AlbumId.</summary>
    public string? AlbumId { get; set; }

    /// <summary>Number of concurrent upload threads.</summary>
    public int Threads { get; set; } = 3;

    /// <summary>Upload in original quality without counting towards storage quota (Pixel spoofing).</summary>
    public bool UnlimitedQuality { get; set; } = true;

    /// <summary>Upload in Storage Saver quality.</summary>
    public bool StorageSaver { get; set; } = false;

    /// <summary>Skip uploading duplicate filenames based on synced local cache.</summary>
    public bool SkipExistingFilenames { get; set; } = true;

    /// <summary>Force re-upload even if remote hash exists.</summary>
    public bool ForceUpload { get; set; } = false;

    /// <summary>Python executable path.</summary>
    public string PythonPath { get; set; } = "python";

    /// <summary>Path to the gpmc repository or installation (optional, bundled by default).</summary>
    public string? GpmcPath { get; set; }

    /// <summary>Scope of files selected for upload.</summary>
    public GooglePhotosScopeMode ScopeMode { get; set; } = GooglePhotosScopeMode.All;

    /// <summary>Start date when ScopeMode is DateRange.</summary>
    public DateTimeOffset? DateRangeStart { get; set; }

    /// <summary>End date when ScopeMode is DateRange.</summary>
    public DateTimeOffset? DateRangeEnd { get; set; }

    /// <summary>Subfolder relative path when ScopeMode is Subfolder.</summary>
    public string? SelectedSubfolder { get; set; }

    /// <summary>Set of relative destination paths when ScopeMode is ManualSelection.</summary>
    public IReadOnlySet<string>? ManualSelectedPaths { get; set; }

    /// <summary>Target folder or file path when ScopeMode is CustomTarget.</summary>
    public string? CustomTargetPath { get; set; }

    /// <summary>Maximum automatic retry attempts for failed uploads (default 3, range 1-5).</summary>
    public int AutoRetryAttempts { get; set; } = 3;

    /// <summary>Base retry delay in seconds (random jitter backoff is added automatically).</summary>
    public double RetryDelaySeconds { get; set; } = 2.0;

    /// <summary>Explicit list of absolute target files to upload (e.g. for re-trying recorded failures or resuming).</summary>
    public IReadOnlyList<string>? ExplicitTargetFiles { get; set; }

    /// <summary>Base number of completed/uploaded files from prior run (for resuming paused session).</summary>
    public int BaseUploadedFiles { get; set; } = 0;

    /// <summary>Base number of skipped files from prior run (for resuming paused session).</summary>
    public int BaseSkippedFiles { get; set; } = 0;

    /// <summary>Base number of failed files from prior run (for resuming paused session).</summary>
    public int BaseFailedFiles { get; set; } = 0;

    /// <summary>Base uploaded bytes from prior run (for resuming paused session).</summary>
    public long BaseUploadedBytes { get; set; } = 0;

    /// <summary>Total session file count override (for accurate progress calculation when resuming).</summary>
    public int? SessionTotalFiles { get; set; }

    /// <summary>Total session bytes override (for accurate progress calculation when resuming).</summary>
    public long? SessionTotalBytes { get; set; }
}
