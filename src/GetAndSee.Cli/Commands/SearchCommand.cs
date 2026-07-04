using System.CommandLine;
using System.Globalization;
using GetAndSee.Core.Journal;
using GetAndSee.Core.Search;
using GetAndSee.Core.Util;
using Spectre.Console;

namespace GetAndSee.Cli.Commands;

/// <summary>
/// The <c>search</c> verb: query an existing archive's manifest by date, type, size, camera, or GPS and
/// print the matches — optionally opening their folders. Read-only inspection; <b>never</b> connects to a
/// device and never alters the database (it opens the journal with <see cref="TransferJournal.OpenReadOnly"/>).
/// </summary>
internal static class SearchCommand
{
    /// <summary>Maximum number of distinct folders <c>--open</c> will launch, to avoid a window storm.</summary>
    internal const int MaxFoldersToOpen = 10;

    /// <summary>Builds the <c>search</c> command and its options.</summary>
    /// <returns>The configured command.</returns>
    public static Command Build()
    {
        Option<string> destinationOption = new("--dest", "-d")
        {
            Description = "Destination root folder of an existing get-and-see archive.",
            Required = true,
        };
        Option<string?> fromOption = new("--from")
        {
            Description = "Only files captured on or after this date (yyyy-MM-dd).",
        };
        Option<string?> toOption = new("--to")
        {
            Description = "Only files captured on or before this date (yyyy-MM-dd).",
        };
        Option<string?> typeOption = new("--type")
        {
            Description = "Media type: photo, video, screenshot, or other.",
        };
        typeOption.AcceptOnlyFromAmong("photo", "video", "screenshot", "other");
        Option<string?> cameraOption = new("--camera")
        {
            Description = "Case-insensitive substring of the camera make or model (e.g. \"iPhone 12\").",
        };
        Option<long?> minSizeOption = new("--min-size")
        {
            Description = "Minimum file size in bytes.",
        };
        Option<long?> maxSizeOption = new("--max-size")
        {
            Description = "Maximum file size in bytes.",
        };
        Option<bool> hasGpsOption = new("--has-gps")
        {
            Description = "Only files that carry GPS coordinates.",
        };
        Option<bool> openOption = new("--open")
        {
            Description = "Open the matches' containing folders in Explorer (Windows).",
        };

        Command command = new(
            "search",
            "Search the archive manifest by date, type, size, camera, or GPS (read-only; no device needed).");
        command.Add(destinationOption);
        command.Add(fromOption);
        command.Add(toOption);
        command.Add(typeOption);
        command.Add(cameraOption);
        command.Add(minSizeOption);
        command.Add(maxSizeOption);
        command.Add(hasGpsOption);
        command.Add(openOption);

        command.SetAction(parseResult =>
        {
            if (!TryBuildCriteria(
                    parseResult.GetValue(fromOption),
                    parseResult.GetValue(toOption),
                    parseResult.GetValue(typeOption),
                    parseResult.GetValue(cameraOption),
                    parseResult.GetValue(minSizeOption),
                    parseResult.GetValue(maxSizeOption),
                    parseResult.GetValue(hasGpsOption),
                    out MediaSearchCriteria criteria,
                    out string? error))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {error}");
                return 2;
            }

            return Run(
                parseResult.GetValue(destinationOption)!,
                criteria,
                parseResult.GetValue(openOption),
                new ExplorerFolderOpener(),
                AnsiConsole.Console);
        });

        return command;
    }

    /// <summary>
    /// Opens the archive read-only, applies the criteria, prints the matches, and (when <paramref name="open"/>)
    /// reveals their folders through <paramref name="opener"/>. Exposed internally so it can be exercised
    /// without launching Explorer. Returns <c>0</c> on success, <c>2</c> when no archive exists.
    /// </summary>
    /// <param name="destination">Destination root folder of an existing archive.</param>
    /// <param name="criteria">The search filters.</param>
    /// <param name="open">When <see langword="true"/>, open matching files' folders via <paramref name="opener"/>.</param>
    /// <param name="opener">The folder-opening seam.</param>
    /// <param name="console">The console to render to.</param>
    /// <returns>The process exit code.</returns>
    internal static int Run(string destination, MediaSearchCriteria criteria, bool open, IFolderOpener opener, IAnsiConsole console)
    {
        destination = Path.GetFullPath(destination);
        string databasePath = Path.Combine(destination, TransferJournal.DatabaseFileName);
        // Probe via the \\?\ long-path prefix so a deep archive root is detected (a non-prefixed File.Exists
        // silently fails past MAX_PATH) before the read-only open (R6 / #39).
        if (!File.Exists(LongPath.ToExtended(databasePath)))
        {
            console.MarkupLineInterpolated($"[yellow]No get-and-see archive found at[/] {destination}.");
            console.MarkupLineInterpolated($"Run [bold]get-and-see copy --dest \"{destination}\"[/] first.");
            return 2;
        }

        IReadOnlyList<MediaSearchHit> hits;
        using (TransferJournal journal = TransferJournal.OpenReadOnly(destination))
        {
            hits = MediaSearch.Find(journal.ReadSearchRows(), criteria);
        }

        RenderResults(console, hits);

        if (open && hits.Count > 0)
        {
            IReadOnlyList<string> folders = DistinctContainingFolders(destination, hits);
            IReadOnlyList<string> toOpen = folders.Count > MaxFoldersToOpen
                ? [.. folders.Take(MaxFoldersToOpen)]
                : folders;
            if (folders.Count > MaxFoldersToOpen)
            {
                console.MarkupLineInterpolated(
                    $"[yellow]Matches span {folders.Count} folders; opening the first {MaxFoldersToOpen}.[/]");
            }

            opener.OpenFolders(toOpen);
        }

        return 0;
    }

    /// <summary>
    /// Parses the raw option values into a <see cref="MediaSearchCriteria"/>. Returns <see langword="false"/>
    /// with a user-facing <paramref name="error"/> for an invalid date or size range. Internal for testing.
    /// </summary>
    internal static bool TryBuildCriteria(
        string? from,
        string? to,
        string? typeToken,
        string? camera,
        long? minSize,
        long? maxSize,
        bool hasGps,
        out MediaSearchCriteria criteria,
        out string? error)
    {
        criteria = new MediaSearchCriteria();
        error = null;

        DateTimeOffset? fromBound = null;
        if (!string.IsNullOrWhiteSpace(from))
        {
            if (!TryParseDate(from, out DateOnly fromDate))
            {
                error = $"invalid --from date '{from}' (expected yyyy-MM-dd).";
                return false;
            }

            fromBound = new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        DateTimeOffset? toBound = null;
        if (!string.IsNullOrWhiteSpace(to))
        {
            if (!TryParseDate(to, out DateOnly toDate))
            {
                error = $"invalid --to date '{to}' (expected yyyy-MM-dd).";
                return false;
            }

            // Inclusive of the whole --to day.
            toBound = new DateTimeOffset(toDate.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
        }

        if (fromBound is DateTimeOffset lower && toBound is DateTimeOffset upper && lower > upper)
        {
            error = "--from is after --to.";
            return false;
        }

        if (minSize is < 0)
        {
            error = "--min-size must be 0 or greater.";
            return false;
        }

        if (maxSize is < 0)
        {
            error = "--max-size must be 0 or greater.";
            return false;
        }

        if (minSize is long min && maxSize is long max && min > max)
        {
            error = "--min-size is greater than --max-size.";
            return false;
        }

        criteria = new MediaSearchCriteria(
            fromBound,
            toBound,
            ParseType(typeToken),
            string.IsNullOrWhiteSpace(camera) ? null : camera,
            minSize,
            maxSize,
            hasGps);
        return true;
    }

    private static MediaType? ParseType(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "photo" => MediaType.Photo,
        "video" => MediaType.Video,
        "screenshot" => MediaType.Screenshot,
        "other" => MediaType.Other,
        _ => null,
    };

    private static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static void RenderResults(IAnsiConsole console, IReadOnlyList<MediaSearchHit> hits)
    {
        if (hits.Count == 0)
        {
            console.MarkupLine("[yellow]No files matched.[/]");
            return;
        }

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Path");
        table.AddColumn("Date");
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("Type");
        foreach (MediaSearchHit hit in hits)
        {
            table.AddRow(
                Markup.Escape(hit.RelativePath),
                hit.CapturedAt is DateTimeOffset captured ? captured.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—",
                ByteSize.Humanize(hit.SizeBytes),
                hit.Type.ToString().ToLowerInvariant());
        }

        console.Write(table);
        string noun = hits.Count == 1 ? "file" : "files";
        console.MarkupLineInterpolated($"[green]{hits.Count:N0}[/] {noun} matched.");
    }

    private static IReadOnlyList<string> DistinctContainingFolders(string destination, IReadOnlyList<MediaSearchHit> hits)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> folders = new();
        foreach (MediaSearchHit hit in hits)
        {
            string full = Path.Combine(destination, hit.RelativePath);
            string folder = Path.GetDirectoryName(full) ?? destination;
            if (seen.Add(folder))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }
}
