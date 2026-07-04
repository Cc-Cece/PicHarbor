using GetAndSee.Core.Journal;
using GetAndSee.Core.Organize;
using GetAndSee.Core.Util;

namespace GetAndSee.Core.Reorganize;

/// <summary>
/// Migrates an existing archive to a different <see cref="OrganizeScheme"/> by moving files within the
/// archive root only — an <b>offline</b>, atomic, resumable operation (the first thing in the product that
/// moves files on the PC). Data integrity is sacred: every move is a same-volume atomic rename followed by a
/// single journal update, a crash between the two is reconciled on the next run, and no file is ever lost,
/// truncated, or clobbered.
/// </summary>
/// <remarks>
/// <para>Flow (see <see cref="Execute"/>):</para>
/// <list type="number">
///   <item>write the in-flight <c>reorganize_target</c> marker so an interruption is detectable and resumable;</item>
///   <item>for each planned move, in the deterministic plan order: ensure the target folder, atomic
///     <see cref="File.Move(string,string)"/>, then <see cref="TransferJournal.UpdateDestPath"/>;</item>
///   <item>crash-reconcile — a file already at its target (source gone, size matches) heals the journal; a
///     file missing from both places is a per-file failure that never drops the row;</item>
///   <item>remove folders the migration emptied (best-effort);</item>
///   <item>on a fully clean run (no per-file failures), stamp the new scheme and clear the marker.</item>
/// </list>
/// <para>
/// <b>Offline:</b> the reorganizer has no device dependency — it never references <c>IPhoneClient</c> or
/// makes any AFC/lockdown call. It reads placement inputs from the journal only, so the read-only device
/// contract is untouched.
/// </para>
/// <para>
/// <b>Collision determinism:</b> new collisions (a coarser layout mapping two files to one target) are
/// resolved with the shared <see cref="CollisionSuffix"/> <c>_2/_3</c> rule against the <i>in-run
/// assignment set only</i> — never on-disk existence. A resumed run therefore re-derives the exact same
/// suffixed target a prior run assigned; a file physically moved to that target but not yet journaled is
/// healed at move time rather than bumped again. Clobber-safety is enforced at move time by the
/// non-overwrite <see cref="File.Move(string,string)"/> plus a size-checked reconcile.
/// </para>
/// </remarks>
public sealed class Reorganizer
{
    private const string StagingFolderName = ".get-and-see-tmp";

    private readonly TransferJournal journal;
    private readonly DateFolderOrganizer organizer;
    private readonly string destinationRoot;          // clean, user-facing form (journal stores relative paths)
    private readonly string destinationRootExtended;  // \\?\ form for all file-system access (R6)

    /// <summary>Creates a reorganizer for the archive at <paramref name="destinationRoot"/>.</summary>
    /// <param name="journal">The archive's open, writable journal.</param>
    /// <param name="organizer">The date-folder organizer whose placement rules the reorganize reuses.</param>
    /// <param name="destinationRoot">The archive root directory (all moves stay within it).</param>
    public Reorganizer(TransferJournal journal, DateFolderOrganizer organizer, string destinationRoot)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(organizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        this.journal = journal;
        this.organizer = organizer;
        this.destinationRoot = destinationRoot;
        destinationRootExtended = LongPath.ToExtended(destinationRoot);
    }

    /// <summary>
    /// Computes the deterministic move plan to reach <paramref name="targetScheme"/> from the journal's
    /// current placement, without touching disk or the journal. Files already at their target are counted
    /// (<see cref="ReorganizePlan.AlreadyPlaced"/>) but not moved; new collisions are resolved with the
    /// shared <c>_2/_3</c> rule.
    /// </summary>
    /// <param name="targetScheme">The layout to migrate the archive to.</param>
    /// <returns>The move plan.</returns>
    public ReorganizePlan Plan(OrganizeScheme targetScheme)
    {
        IReadOnlyList<ReorganizeEntry> entries = journal.EnumerateDoneForReorganize();

        // Raw target (before suffixing) for each file: its stored leaf placed by its resolved date under the
        // target scheme. The leaf comes from the CURRENT dest_path so any _2/_3 suffix assigned at copy time
        // is preserved (identity stays stable); the date reuses the exact copy-time ResolveDate rule.
        List<(ReorganizeEntry Entry, string RawTarget)> computed = new(entries.Count);
        foreach (ReorganizeEntry entry in entries)
        {
            string leaf = Path.GetFileName(entry.DestPath);
            string rawTarget;
            if (IsUnderUnsorted(entry.DestPath))
            {
                // Respect copy's original "no trustworthy date" decision: a file copy placed in unsorted/
                // never moves, even if its stored timestamp would now resolve as sane (e.g. a clock-skewed
                // future date whose sanity window has since passed). This keeps reorganize placement
                // time-independent and never in disagreement with where copy put the file.
                rawTarget = entry.DestPath;
            }
            else
            {
                DateTime? date = organizer.ResolveDate(entry.ExifDateTimeOriginal, entry.SourceMtime);
                rawTarget = organizer.GetRelativeDestination(leaf, date, targetScheme);
            }

            computed.Add((entry, rawTarget));
        }

        // Reserve the paths of files already where the target scheme wants them so movers never collide with
        // a file staying put. "Already placed" is decided from the JOURNAL (not disk), so a file a prior
        // interrupted run physically moved but had not yet journaled is still treated as a mover and healed —
        // never wrongly counted as already placed.
        HashSet<string> assigned = new(StringComparer.OrdinalIgnoreCase);
        int alreadyPlaced = 0;
        foreach ((ReorganizeEntry entry, string rawTarget) in computed)
        {
            if (PathsEqual(rawTarget, entry.DestPath))
            {
                assigned.Add(entry.DestPath);
                alreadyPlaced++;
            }
        }

        // Resolve movers in the deterministic enumerate order (by source_path), using ONLY the in-run
        // assignment set — see the class remarks on why on-disk existence is deliberately not consulted here.
        List<PlannedMove> moves = new();
        int collisionsResuffixed = 0;
        foreach ((ReorganizeEntry entry, string rawTarget) in computed)
        {
            if (PathsEqual(rawTarget, entry.DestPath))
            {
                continue; // already placed — not a move
            }

            string finalTarget = CollisionSuffix.Resolve(rawTarget, candidate => !assigned.Contains(candidate));
            assigned.Add(finalTarget);
            if (!PathsEqual(finalTarget, rawTarget))
            {
                collisionsResuffixed++;
            }

            moves.Add(new PlannedMove(entry.SourcePath, entry.SourceSize, entry.DestPath, finalTarget));
        }

        return new ReorganizePlan(targetScheme, moves, alreadyPlaced, collisionsResuffixed);
    }

    /// <summary>
    /// Executes <paramref name="plan"/>: sets the in-flight marker, performs each atomic move plus journal
    /// update (reconciling a crash-interrupted move), removes emptied folders, and — only on a fully clean
    /// run — stamps the new scheme and clears the marker. Writes nothing when <paramref name="plan"/> has no
    /// moves other than finalizing the scheme/marker.
    /// </summary>
    /// <param name="plan">A plan from <see cref="Plan"/> for this archive.</param>
    /// <param name="cancellationToken">Observed between files so Ctrl+C stops cleanly and resumably.</param>
    /// <returns>A report of what happened.</returns>
    public ReorganizeReport Execute(ReorganizePlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Mark the migration in-flight before the first move so any interruption is detectable and resumable.
        journal.SetReorganizeTarget(plan.TargetScheme);

        int moved = 0;
        int failed = 0;
        foreach (PlannedMove move in plan.Moves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryApplyMove(move))
            {
                moved++;
            }
            else
            {
                failed++;
            }
        }

        int directoriesRemoved = RemoveEmptyDirectories();

        OrganizeScheme finalScheme;
        if (failed == 0)
        {
            // Clean run: the archive now IS the target layout. Stamp it, then clear the in-flight marker.
            // Stamp-before-clear is the safe order — a crash between the two leaves marker == scheme, which
            // is benign (copy is not blocked, and a reorganize re-run is a no-op).
            journal.SetOrganizeScheme(plan.TargetScheme);
            journal.ClearReorganizeTarget();
            finalScheme = plan.TargetScheme;
        }
        else
        {
            // Stragglers remain in the old layout: leave the marker set so `copy` refuses and a re-run of
            // `reorganize` retries them. The recorded scheme is unchanged.
            finalScheme = journal.GetOrganizeScheme() ?? plan.TargetScheme;
        }

        return new ReorganizeReport(moved, plan.AlreadyPlaced, plan.CollisionsResuffixed, failed, directoriesRemoved, finalScheme);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="move"/> ends with the file at its target (moved
    /// now, or already there from an interrupted run and healed), <see langword="false"/> on a per-file
    /// failure. A failure never drops the row — its journal entry stays <c>done</c> at its current
    /// <c>dest_path</c>, so nothing is lost and a re-run retries it.
    /// </summary>
    private bool TryApplyMove(PlannedMove move)
    {
        string currentAbs = Path.Combine(destinationRoot, move.CurrentDestPath);
        string targetAbs = Path.Combine(destinationRoot, move.TargetDestPath);
        string currentExtended = LongPath.ToExtended(currentAbs);
        string targetExtended = LongPath.ToExtended(targetAbs);

        try
        {
            if (File.Exists(currentExtended))
            {
                if (File.Exists(targetExtended))
                {
                    // The target is unexpectedly occupied (a foreign file, or an earlier failure): never
                    // overwrite. Leave the row done at its current path — resumable, nothing lost.
                    return false;
                }

                Directory.CreateDirectory(LongPath.ToExtended(Path.GetDirectoryName(targetAbs)!));
                File.Move(currentExtended, targetExtended); // same volume => atomic OS rename
                journal.UpdateDestPath(move.SourcePath, move.SourceSize, move.TargetDestPath);
                return true;
            }

            // The source is gone. Crash-reconcile: if the file already sits at its target with the expected
            // size, a prior run moved it before the journal caught up — heal the journal only. Otherwise the
            // file is genuinely missing (or the target holds foreign/wrong bytes): a per-file failure, never
            // a heal of the journal onto the wrong file.
            if (File.Exists(targetExtended) && new FileInfo(targetExtended).Length == move.SourceSize)
            {
                journal.UpdateDestPath(move.SourcePath, move.SourceSize, move.TargetDestPath);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A move that throws mid-flight leaves the source in place (File.Move is atomic), so the journal
            // still matches disk — resumable. Count it as a per-file failure.
            return false;
        }
    }

    /// <summary>
    /// Removes directories the migration emptied, deepest-first, so an emptied <c>YYYY\YYYY-MM\</c> and then
    /// its now-empty <c>YYYY\</c> both go. Only ever deletes a genuinely empty directory (so a folder still
    /// holding a moved-in, failed, or user file is kept), never the archive root or the staging folder.
    /// Best-effort cosmetics — a failure to remove is not a run failure.
    /// </summary>
    /// <returns>The number of directories removed.</returns>
    private int RemoveEmptyDirectories()
    {
        if (!Directory.Exists(destinationRootExtended))
        {
            return 0;
        }

        int removed = 0;
        foreach (string dir in CollectSubdirectoriesDeepestFirst(destinationRootExtended))
        {
            // Never remove the staging dir (it holds a copy's in-progress .partial files) or the unsorted
            // folder (a named-protected archive dir); reorganize is offline, but stay conservative. The
            // archive root is never in this list.
            string name = Path.GetFileName(dir);
            if (string.Equals(name, StagingFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, DateFolderOrganizer.UnsortedFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir); // only ever an empty directory
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort — leave the folder in place.
            }
        }

        return removed;
    }

    // All directories strictly under the root, deepest-first. A child path string is always strictly longer
    // than its parent's (parent + separator + name), so ordering by descending length guarantees every child
    // is visited — and thus removed if empty — before its parent, enabling nested pruning in one pass.
    private static List<string> CollectSubdirectoriesDeepestFirst(string rootExtended)
    {
        List<string> all = new();
        Collect(rootExtended, all);
        all.Sort(static (left, right) => right.Length.CompareTo(left.Length));
        return all;

        static void Collect(string dir, List<string> into)
        {
            string[] children;
            try
            {
                children = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (string child in children)
            {
                into.Add(child);
                Collect(child, into);
            }
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderUnsorted(string relativeDestPath)
    {
        // Unsorted files are placed at "unsorted\<leaf>" under every scheme, so the first path segment
        // identifies them.
        ReadOnlySpan<char> span = relativeDestPath.AsSpan();
        int separator = span.IndexOfAny('\\', '/');
        ReadOnlySpan<char> firstSegment = separator >= 0 ? span[..separator] : span;
        return firstSegment.Equals(DateFolderOrganizer.UnsortedFolder, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One planned file move within a <see cref="ReorganizePlan"/>.</summary>
/// <param name="SourcePath">Device source path (identity, for the journal update).</param>
/// <param name="SourceSize">Source size in bytes (identity, and the expected on-disk size when reconciling).</param>
/// <param name="CurrentDestPath">The file's current path, relative to the archive root.</param>
/// <param name="TargetDestPath">The file's target path (collision-resolved), relative to the archive root.</param>
public sealed record PlannedMove(string SourcePath, long SourceSize, string CurrentDestPath, string TargetDestPath);

/// <summary>The deterministic plan for one <c>reorganize</c> run.</summary>
/// <param name="TargetScheme">The layout being migrated to.</param>
/// <param name="Moves">The files to move, in deterministic (source-path) order.</param>
/// <param name="AlreadyPlaced">Files already at their target under <paramref name="TargetScheme"/> (not moved).</param>
/// <param name="CollisionsResuffixed">Moves whose target required a <c>_2/_3</c> suffix to avoid a new collision.</param>
public sealed record ReorganizePlan(
    OrganizeScheme TargetScheme,
    IReadOnlyList<PlannedMove> Moves,
    int AlreadyPlaced,
    int CollisionsResuffixed);

/// <summary>The outcome of a <c>reorganize</c> run.</summary>
/// <param name="Moved">Files moved to (or healed at) their target this run.</param>
/// <param name="AlreadyPlaced">Files that were already at their target.</param>
/// <param name="CollisionsResuffixed">Moves that needed a numeric suffix to avoid a new collision.</param>
/// <param name="Failed">Files that could not be moved (left resumable, never dropped).</param>
/// <param name="DirectoriesRemoved">Emptied folders removed during cleanup.</param>
/// <param name="FinalScheme">The archive's recorded scheme after the run.</param>
public sealed record ReorganizeReport(
    int Moved,
    int AlreadyPlaced,
    int CollisionsResuffixed,
    int Failed,
    int DirectoriesRemoved,
    OrganizeScheme FinalScheme);
