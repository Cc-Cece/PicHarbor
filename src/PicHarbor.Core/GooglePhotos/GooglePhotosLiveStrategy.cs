namespace PicHarbor.Core.GooglePhotos;

/// <summary>
/// How a two-part Live Photo is submitted to Google Photos.
/// The archive keeps both original files either way.
/// </summary>
public enum GooglePhotosLiveStrategy
{
    /// <summary>Combine the still and video into one motion-photo file, then upload that file.</summary>
    Motion,

    /// <summary>Upload both original parts and link the video to the still in one commit.</summary>
    ApplePair,

    /// <summary>Upload the still first, then attach the video to that existing photo.</summary>
    AppleReconcile,
}
