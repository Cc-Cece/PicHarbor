namespace GetAndSee.Core.GooglePhotos;

/// <summary>
/// Authentication method for Google Photos via gpmc.
/// </summary>
public enum GooglePhotosAuthMethod
{
    /// <summary>
    /// Browser sign-in via Google Embedded Setup oauth_token cookie (recommended, no Android needed).
    /// </summary>
    OAuthCookie = 0,

    /// <summary>
    /// Android mobile client credential string (GmsCore / photos.native token body).
    /// </summary>
    AndroidAuthData = 1
}
