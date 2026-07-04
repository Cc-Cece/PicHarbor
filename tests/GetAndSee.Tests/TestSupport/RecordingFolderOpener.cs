using GetAndSee.Cli.Commands;

namespace GetAndSee.Tests.TestSupport;

/// <summary>
/// An <see cref="IFolderOpener"/> test double that records the folders it was asked to open instead of
/// launching Explorer — lets the <c>search --open</c> path be asserted without spawning windows.
/// </summary>
internal sealed class RecordingFolderOpener : IFolderOpener
{
    /// <summary>The folders passed across all calls, in order.</summary>
    public List<string> OpenedFolders { get; } = [];

    /// <summary>Number of times <see cref="OpenFolders"/> was invoked.</summary>
    public int CallCount { get; private set; }

    /// <inheritdoc />
    public void OpenFolders(IReadOnlyList<string> absoluteFolderPaths)
    {
        CallCount++;
        OpenedFolders.AddRange(absoluteFolderPaths);
    }
}
