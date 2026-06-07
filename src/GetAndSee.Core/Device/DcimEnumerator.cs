using System.Runtime.CompilerServices;

namespace GetAndSee.Core.Device;

/// <summary>
/// Recursively walks the device's <c>/DCIM/</c> tree over the read-only <see cref="IPhoneClient"/>,
/// yielding every regular file it finds.
/// </summary>
public sealed class DcimEnumerator
{
    /// <summary>The conventional AFC root of the camera roll.</summary>
    public const string DefaultRoot = "/DCIM/";

    private readonly IPhoneClient client;

    /// <summary>Creates an enumerator over the supplied read-only device client.</summary>
    /// <param name="client">A connected read-only device client.</param>
    public DcimEnumerator(IPhoneClient client) => this.client = client;

    /// <summary>
    /// Enumerates all regular files beneath <paramref name="root"/>, descending into subdirectories.
    /// Directories themselves are not yielded.
    /// </summary>
    /// <param name="root">The AFC directory to walk. Defaults to <see cref="DefaultRoot"/>.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>An async stream of <see cref="RemoteFile"/> descriptors.</returns>
    public async IAsyncEnumerable<RemoteFile> EnumerateAsync(
        string root = DefaultRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pending = new Stack<string>();
        pending.Push(NormalizeDirectory(root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();

            IReadOnlyList<string> names = await client.ListDirectoryAsync(directory, cancellationToken)
                .ConfigureAwait(false);

            foreach (string name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = CombinePath(directory, name);
                RemoteFileInfo info = await client.GetFileInfoAsync(path, cancellationToken)
                    .ConfigureAwait(false);

                if (info.IsDirectory)
                {
                    pending.Push(path);
                }
                else
                {
                    yield return new RemoteFile(path, info.Size, info.ModifiedAt);
                }
            }
        }
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        string trimmed = path.Replace('\\', '/');
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    private static string CombinePath(string directory, string name)
    {
        if (directory.EndsWith('/'))
        {
            return directory + name;
        }

        return directory + "/" + name;
    }
}
