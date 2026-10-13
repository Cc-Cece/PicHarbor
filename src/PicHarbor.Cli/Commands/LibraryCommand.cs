using System.CommandLine;
using PicHarbor.Core.Storage;

namespace PicHarbor.Cli.Commands;

internal static class LibraryCommand
{
    public static Command Build()
    {
        var root = new Command("library","Manage registered photo-library members and recoverable copies.");
        var check = new Command("check","Verify and safely deduplicate registered members.");
        var destination = new Option<string>("--dest","-d") { Required = true };
        check.Add(destination);
        check.SetAction(async (parse,ct) =>
        {
            try
            {
                var result=await new LibraryMaintenanceService().CheckAsync(new LibraryCatalog(parse.GetValue(destination)!),ct);
                Console.WriteLine($"Checked: {result.Checked}; recycled: {result.Recycled}; purged: {result.Purged}");
                foreach(string error in result.Errors) Console.Error.WriteLine(error);
                return result.Errors.Count==0 ? 0 : 1;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(ex.Message);return 2; }
        });
        root.Add(check);
        var include=new Command("include","Explicitly include another archive; external directories are not scanned.");
        var parent=new Option<string>("--dest","-d") {Required=true};
        var member=new Option<string>("--member") {Required=true};include.Add(parent);include.Add(member);
        include.SetAction(parse=>
        {
            try { new LibraryCatalog(parse.GetValue(parent)!).Include(parse.GetValue(member)!);return 0; }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {Console.Error.WriteLine(ex.Message);return 2;}
        });root.Add(include);
        var restore=new Command("restore","Restore a duplicate from its 30-day recycle area.");
        var restoreRoot=new Option<string>("--dest","-d") {Required=true};
        var operation=new Option<string>("--operation") {Required=true};restore.Add(restoreRoot);restore.Add(operation);
        restore.SetAction(parse=>
        {
            try {new LibraryMaintenanceService().Restore(parse.GetValue(restoreRoot)!,parse.GetValue(operation)!);return 0;}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException) {Console.Error.WriteLine(ex.Message);return 2;}
        });root.Add(restore);
        var list=new Command("recycled","List recoverable copies and their operation IDs.");
        var listRoot=new Option<string>("--dest","-d") {Required=true};list.Add(listRoot);
        list.SetAction(parse=>
        {
            foreach(var copy in new LibraryMaintenanceService().ListRecycled(new LibraryCatalog(parse.GetValue(listRoot)!)))
                Console.WriteLine($"{copy.OperationId}\t{copy.Root}\t{copy.RelativePath}\t{copy.RecycledAt:O}");
        });root.Add(list);
        var reclaim=new Command("reclaim-writer","Reclaim a writer interrupted on another computer; requires every member disk online.");
        var reclaimRoot=new Option<string>("--dest","-d") {Required=true};reclaim.Add(reclaimRoot);
        reclaim.SetAction(parse=>
        {
            try {LibraryWriteCoordinator.ReclaimLibraryWriter(new LibraryCatalog(parse.GetValue(reclaimRoot)!));return 0;}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {Console.Error.WriteLine(ex.Message);return 2;}
        });root.Add(reclaim);
        return root;
    }
}
