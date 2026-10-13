using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Storage;

namespace PicHarbor.Core.Reorganize;

/// <summary>Reorganizes registered members in place, retaining pending layouts for disconnected members.</summary>
public static class LibraryReorganizer
{
    /// <summary>Previews online members without changing files or databases.</summary>
    public static ReorganizePlan Preview(string root,OrganizeScheme scheme)
    {
        var catalog=new LibraryCatalog(root);
        if(catalog.IsUnincludedCopy) throw new InvalidOperationException("Include the copied archive before reorganizing it.");
        var moves=new List<PlannedMove>();int placed=0,collisions=0;
        foreach(var member in catalog.Members.Where(m=>File.Exists(TransferJournal.ResolveDatabasePath(m.Root))))
        {
            using var journal=TransferJournal.OpenReadOnly(member.Root);
            var plan=new Reorganizer(journal,new(),member.Root).Plan(scheme);
            moves.AddRange(plan.Moves.Select(m=>m with{ArchiveRoot=member.Root}));placed+=plan.AlreadyPlaced;collisions+=plan.CollisionsResuffixed;
        }
        return new(scheme,moves,placed,collisions);
    }

    /// <summary>Queues every member and applies the new layout to available members under one library lease.</summary>
    public static ReorganizeReport Execute(string root,OrganizeScheme scheme,CancellationToken ct=default)
    {
        var catalog=new LibraryCatalog(root);
        if(catalog.IsUnincludedCopy) throw new InvalidOperationException("Include the copied archive before reorganizing it.");
        using var lease=LibraryWriteCoordinator.AcquireLibrary(root);
        catalog.QueueLayout(scheme);catalog.Save();
        int moved=0,placed=0,collisions=0,failed=0,removed=0;
        foreach(var member in catalog.Members.Where(m=>File.Exists(TransferJournal.ResolveDatabasePath(m.Root))))
        {
            ct.ThrowIfCancellationRequested();using var journal=TransferJournal.Open(member.Root);
            var reorganizer=new Reorganizer(journal,new(),member.Root);
            var report=reorganizer.ExecuteWithLease(reorganizer.Plan(scheme),ct);
            moved+=report.Moved;placed+=report.AlreadyPlaced;collisions+=report.CollisionsResuffixed;failed+=report.Failed;removed+=report.DirectoriesRemoved;
            if(report.Failed==0) catalog.CompleteLayout(member.StorageId);
            catalog.ReadMedia();catalog.Save();
        }
        return new(moved,placed,collisions,failed,removed,scheme) {PendingMembers=catalog.PendingLayouts.Count};
    }
}
