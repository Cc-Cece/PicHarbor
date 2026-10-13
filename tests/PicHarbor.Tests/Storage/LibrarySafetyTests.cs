using Microsoft.Data.Sqlite;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Core.Storage;
using PicHarbor.Core.Transfer;
using PicHarbor.Tests.TestSupport;
using Xunit;

namespace PicHarbor.Tests.Storage;

public sealed class LibrarySafetyTests
{
    [Theory]
    [InlineData("../private.jpg")]
    [InlineData("C:\\private.jpg")]
    [InlineData("photos/../../private.jpg")]
    [InlineData("photo.jpg:secret")]
    public void Unsafe_archive_paths_are_rejected(string relative)
    {
        using var temp = new TempDirectory();
        Assert.Throws<InvalidDataException>(() => ArchivePathResolver.Resolve(temp.Path,relative));
    }

    [Fact]
    public void Directory_junction_cannot_escape_the_archive()
    {
        using var archive=new TempDirectory();using var outside=new TempDirectory();
        File.WriteAllText(Path.Combine(outside.Path,"private.jpg"),"outside archive");
        string link=Path.Combine(archive.Path,"junction");
        var start=new System.Diagnostics.ProcessStartInfo("cmd.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in new[]{"/d","/c","mklink","/J",link,outside.Path}) start.ArgumentList.Add(argument);
        using var process=System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();Assert.Equal(0,process.ExitCode);
        try { Assert.Throws<InvalidDataException>(()=>ArchivePathResolver.Resolve(archive.Path,"junction/private.jpg")); }
        finally { Directory.Delete(link); }
        Assert.Equal("outside archive",File.ReadAllText(Path.Combine(outside.Path,"private.jpg")));
    }

    [Fact]
    public async Task Different_devices_with_same_path_and_size_both_copy()
    {
        using var temp = new TempDirectory(); using var journal=TransferJournal.Open(temp.Path);
        var file=new RemoteFile("/DCIM/IMG_1.JPG",4,null);
        using(var copier=Copier(journal,temp.Path,"A",[1,2,3,4])) Assert.Equal(CopyStatus.Copied,(await copier.CopyAsync(file,TestContext.Current.CancellationToken)).Status);
        using(var copier=Copier(journal,temp.Path,"B",[5,6,7,8])) Assert.Equal(CopyStatus.Copied,(await copier.CopyAsync(file,TestContext.Current.CancellationToken)).Status);
        Assert.Equal(2,journal.ReadManifest().Count);
        Assert.Equal(new byte[]{1,2,3,4},File.ReadAllBytes(ArchivePathResolver.Resolve(temp.Path,journal.ReadManifest()[0].DestPath)));
    }

    [Fact]
    public void Future_database_is_not_migrated_or_modified()
    {
        using var temp=new TempDirectory();
        string path;
        using(var journal=TransferJournal.Open(temp.Path)) path=journal.DatabasePath;
        using(var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString()))
        { connection.Open(); connection.CreateFunction("picharbor_writer_version",()=>8L); using var cmd=connection.CreateCommand();cmd.CommandText="PRAGMA user_version=999;";cmd.ExecuteNonQuery(); }
        Assert.Throws<InvalidDataException>(()=>TransferJournal.Open(temp.Path));
        using var check=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString());check.Open();
        using var query=check.CreateCommand();query.CommandText="PRAGMA user_version;";Assert.Equal(999L,query.ExecuteScalar());
    }

    [Fact]
    public async Task Explicit_members_recycle_duplicates_without_losing_sources()
    {
        using var first=new TempDirectory();using var second=new TempDirectory();
        Seed(first.Path,"A","a.jpg",[1,2,3,4]); Seed(second.Path,"B","b.jpg",[1,2,3,4]);
        var catalog=new LibraryCatalog(first.Path);catalog.Include(second.Path);
        var result=await new LibraryMaintenanceService().CheckAsync(catalog,TestContext.Current.CancellationToken);
        Assert.Empty(result.Errors);Assert.Equal(1,result.Recycled);
        Assert.Single(catalog.ReadLogicalMedia());
        using var a=TransferJournal.OpenReadOnly(first.Path);using var b=TransferJournal.OpenReadOnly(second.Path);
        Assert.Single(a.ReadLibraryMedia(first.Path));Assert.Single(b.ReadLibraryMedia(second.Path));
        Assert.Single(Directory.EnumerateFiles(first.Path,"*",SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(second.Path,"*",SearchOption.AllDirectories)) , p=>p.Contains("recycle")&&p.EndsWith(".jpg"));
    }

    [Fact]
    public async Task Separate_archives_are_never_automatically_deduplicated()
    {
        using var first=new TempDirectory();using var second=new TempDirectory();
        Seed(first.Path,"A","a.jpg",[1,2,3,4]);Seed(second.Path,"B","b.jpg",[1,2,3,4]);
        var result=await new LibraryMaintenanceService().CheckAsync(new LibraryCatalog(first.Path),TestContext.Current.CancellationToken);
        Assert.Equal(0,result.Recycled);Assert.True(File.Exists(Path.Combine(second.Path,"b.jpg")));
    }

    [Fact]
    public async Task Same_size_different_contents_are_not_recycled()
    {
        using var first=new TempDirectory();using var second=new TempDirectory();
        Seed(first.Path,"A","a.jpg",[1,2,3,4]);Seed(second.Path,"B","b.jpg",[5,6,7,8]);
        var catalog=new LibraryCatalog(first.Path);catalog.Include(second.Path);
        Assert.Equal(0,(await new LibraryMaintenanceService().CheckAsync(catalog,TestContext.Current.CancellationToken)).Recycled);
        Assert.Equal(2,catalog.ReadLogicalMedia().Count);
    }

    [Fact]
    public void Concurrent_writer_lease_is_rejected()
    {
        using var temp=new TempDirectory();using var first=LibraryWriteCoordinator.Acquire(temp.Path);
        Assert.Throws<IOException>(()=>LibraryWriteCoordinator.Acquire(temp.Path));
    }

    [Fact]
    public async Task Deleted_file_is_rebacked_up_by_default()
    {
        using var temp=new TempDirectory();using var journal=TransferJournal.Open(temp.Path);
        var file=new RemoteFile("/DCIM/IMG.JPG",4,null);
        using var copier=Copier(journal,temp.Path,"A",[1,2,3,4]);
        var first=await copier.CopyAsync(file,TestContext.Current.CancellationToken);File.Delete(ArchivePathResolver.Resolve(temp.Path,first.RelativeDestPath!));
        Assert.Equal(CopyStatus.Copied,(await copier.CopyAsync(file,TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Deleted_file_decision_can_cancel_without_excluding_source()
    {
        using var temp=new TempDirectory();using var journal=TransferJournal.Open(temp.Path);
        var file=new RemoteFile("/DCIM/IMG.JPG",4,null);
        using(var copier=Copier(journal,temp.Path,"A",[1,2,3,4]))
        { var copied=await copier.CopyAsync(file,TestContext.Current.CancellationToken);File.Delete(ArchivePathResolver.Resolve(temp.Path,copied.RelativeDestPath!)); }
        using var cancelled=new FileCopier(new Source("A",[1,2,3,4]),journal,new(),new Metadata(),temp.Path,OrganizeScheme.YearMonth,deletedPhotoDecision:_=>null);
        await Assert.ThrowsAsync<OperationCanceledException>(()=>cancelled.CopyAsync(file,TestContext.Current.CancellationToken));
        Assert.False(journal.IsSourceExcluded(file.Path,file.Size));
    }

    [Fact]
    public void Repair_rejects_wrong_or_unavailable_root_without_removing_records()
    {
        using var temp=new TempDirectory();Seed(temp.Path,"A","a.jpg",[1,2,3,4]);
        using var journal=TransferJournal.Open(temp.Path);
        Assert.Throws<InvalidOperationException>(()=>journal.RepairDatabaseConsistency(Path.Combine(temp.Path,"not-there")));
        Assert.Single(journal.ReadManifest());
    }

    [Fact]
    public void History_records_cannot_reference_nonexistent_sessions()
    {
        using var temp=new TempDirectory();using var journal=TransferJournal.Open(temp.Path);
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=journal.DatabasePath}.ToString());db.Open(); db.CreateFunction("picharbor_writer_version",()=>8L);
        using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO backup_history_records(session_id,device_source_path,dest_path,file_size,transferred_at) VALUES(123,'a','a',1,'2026-01-01');";
        Assert.Throws<SqliteException>(()=>cmd.ExecuteNonQuery());
    }

    [Fact]
    public async Task Strict_verification_recopies_changed_source_with_identical_attributes()
    {
        using var temp=new TempDirectory();using var journal=TransferJournal.Open(temp.Path);
        var file=new RemoteFile("/DCIM/IMG.JPG",4,null);
        using(var first=Copier(journal,temp.Path,"A",[1,2,3,4])) await first.CopyAsync(file,TestContext.Current.CancellationToken);
        using var second=new FileCopier(new Source("A",[5,6,7,8]),journal,new(),new Metadata(),temp.Path,OrganizeScheme.YearMonth,strictVerification:true);
        var result=await second.CopyAsync(file,TestContext.Current.CancellationToken);
        Assert.Equal(CopyStatus.Copied,result.Status);
        Assert.Equal(new byte[]{5,6,7,8},File.ReadAllBytes(ArchivePathResolver.Resolve(temp.Path,result.RelativeDestPath!)));
    }

    [Fact]
    public async Task Recycle_is_restorable_and_expiry_requires_an_online_keeper()
    {
        using var first=new TempDirectory();using var second=new TempDirectory();
        Seed(first.Path,"A","a.jpg",[1,2,3,4]);Seed(second.Path,"B","b.jpg",[1,2,3,4]);
        var catalog=new LibraryCatalog(first.Path);catalog.Include(second.Path);
        var service=new LibraryMaintenanceService();await service.CheckAsync(catalog,TestContext.Current.CancellationToken);
        var copy=Assert.Single(service.ListRecycled(catalog));
        using(var journal=TransferJournal.Open(copy.Root))
        using(var database=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=journal.DatabasePath}.ToString()))
        {
            database.Open();database.CreateFunction("picharbor_writer_version",()=>8L);
            using var command=database.CreateCommand();command.CommandText="UPDATE maintenance_operations SET created_at='2000-01-01T00:00:00Z',keeper_root=$offline WHERE operation_id=$id;";
            command.Parameters.AddWithValue("$offline",Path.Combine(first.Path,"offline"));command.Parameters.AddWithValue("$id",copy.OperationId);command.ExecuteNonQuery();
        }
        var keeper=Assert.Single(catalog.ReadLogicalMedia());
        string hidden=keeper.Root+".offline";
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),Path.GetFullPath(hidden));
        SqliteConnection.ClearAllPools();
        Directory.Move(keeper.Root,hidden);
        try
        {
            var checkedAgain=await service.CheckAsync(catalog,TestContext.Current.CancellationToken);
            Assert.Equal(0,checkedAgain.Purged);
            Assert.Single(service.ListRecycled(catalog));
            service.Restore(copy.Root,copy.OperationId);
            Assert.Equal(new byte[]{1,2,3,4},File.ReadAllBytes(ArchivePathResolver.Resolve(copy.Root,copy.RelativePath)));
        }
        finally { SqliteConnection.ClearAllPools();Directory.Move(hidden,keeper.Root); }
    }

    [Fact]
    public async Task Apple_export_and_unknown_files_are_not_recycled()
    {
        using var temp=new TempDirectory();Seed(temp.Path,"A","a.jpg",[1,2,3,4]);
        Directory.CreateDirectory(Path.Combine(temp.Path,".AppleSync"));
        File.WriteAllBytes(Path.Combine(temp.Path,".AppleSync","export.jpg"),[1,2,3,4]);
        File.WriteAllBytes(Path.Combine(temp.Path,"unknown.jpg"),[1,2,3,4]);
        var result=await new LibraryMaintenanceService().CheckAsync(new LibraryCatalog(temp.Path),TestContext.Current.CancellationToken);
        Assert.Equal(0,result.Recycled);Assert.True(File.Exists(Path.Combine(temp.Path,".AppleSync","export.jpg")));Assert.True(File.Exists(Path.Combine(temp.Path,"unknown.jpg")));
    }

    [Fact]
    public async Task Offline_member_records_do_not_create_a_local_done_row()
    {
        using var first=new TempDirectory();using var second=new TempDirectory();
        Seed(first.Path,"A","a.jpg",[1,2,3,4]);using(var initialize=TransferJournal.Open(second.Path)) { }
        var catalog=new LibraryCatalog(first.Path);catalog.Include(second.Path);catalog.ReadMedia();catalog.Save();
        File.Delete(Path.Combine(first.Path,"a.jpg"));
        using var journal=TransferJournal.Open(second.Path);using var fallback=TransferJournal.OpenReadOnly(first.Path);
        var file=new RemoteFile("/DCIM/a.jpg",4,null);
        using var copier=new FileCopier(new Source("A",[1,2,3,4]),journal,new(),new Metadata(),second.Path,OrganizeScheme.YearMonth,fallbackJournals:[fallback]);
        Assert.Equal(CopyStatus.Copied,(await copier.CopyAsync(file,TestContext.Current.CancellationToken)).Status);
        Assert.Single(journal.ReadManifest());
    }

    [Fact]
    public void New_library_rejects_unversioned_old_writer()
    {
        using var temp=new TempDirectory();using var journal=TransferJournal.Open(temp.Path);
        using var external=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=journal.DatabasePath,Pooling=false}.ToString());external.Open();
        using var command=external.CreateCommand();command.CommandText="INSERT INTO files(source_path,source_size,state) VALUES('old-client',1,'pending');";
        var error=Assert.Throws<SqliteException>(()=>command.ExecuteNonQuery());Assert.Contains("picharbor_writer_version",error.Message);
    }

    private static FileCopier Copier(TransferJournal journal,string root,string id,byte[] content)=>new(new Source(id,content),journal,new(),new Metadata(),root,OrganizeScheme.YearMonth,deviceSubfolder:id);

    [Fact]
    public async Task Edited_content_preserves_original_identity_and_is_never_recycled_as_original()
    {
        using var first=new TempDirectory();using var second=new TempDirectory();
        Seed(first.Path,"A","a.jpg",[1,2,3,4]);Seed(second.Path,"B","b.jpg",[1,2,3,4]);
        var catalog=new LibraryCatalog(first.Path);catalog.Include(second.Path);
        File.WriteAllBytes(Path.Combine(first.Path,"a.jpg"),[5,6,7,8]);
        var result=await new LibraryMaintenanceService().CheckAsync(catalog,TestContext.Current.CancellationToken);
        Assert.Equal(0,result.Recycled);Assert.Contains(result.Errors,e=>e.StartsWith("Content changed:"));
        using var journal=TransferJournal.OpenReadOnly(first.Path);journal.BindDevice("A");
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[]{1,2,3,4})),journal.GetReplicaVerification("/DCIM/a.jpg",4).Hash);
        Assert.Equal(2,catalog.ReadLogicalMedia().Count);
        await new LibraryMaintenanceService().CheckAsync(catalog,TestContext.Current.CancellationToken);
        Assert.Equal(2,catalog.ReadLogicalMedia().Count);
    }

    [Fact]
    public async Task Copied_member_is_browsable_but_not_maintained_before_explicit_inclusion()
    {
        using var original=new TempDirectory();using var clone=new TempDirectory();
        Seed(original.Path,"A","a.jpg",[1,2,3,4]);
        var catalog=new LibraryCatalog(original.Path);catalog.ReadMedia();catalog.Save();
        using(var source=TransferJournal.OpenReadOnly(original.Path))
        using(var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(clone.Path,"get-and-see.db")}.ToString()))
        { target.Open();source.Connection.BackupDatabase(target); }
        Directory.CreateDirectory(Path.Combine(clone.Path,".picharbor"));
        File.Copy(Path.Combine(original.Path,".picharbor","library.json"),Path.Combine(clone.Path,".picharbor","library.json"));
        File.Copy(Path.Combine(original.Path,"a.jpg"),Path.Combine(clone.Path,"a.jpg"));
        var copied=new LibraryCatalog(clone.Path);
        Assert.True(copied.IsUnincludedCopy);Assert.Single(copied.ReadLogicalMedia());
        Assert.NotEmpty((await new LibraryMaintenanceService().CheckAsync(copied,TestContext.Current.CancellationToken)).Errors);
        Assert.True(File.Exists(Path.Combine(clone.Path,"a.jpg")));
        catalog.Include(clone.Path);
        using var a=TransferJournal.OpenReadOnly(original.Path);using var b=TransferJournal.OpenReadOnly(clone.Path);
        Assert.NotEqual(a.GetLibraryIdentity().StorageId,b.GetLibraryIdentity().StorageId);
        Assert.Equal(1,(await new LibraryMaintenanceService().CheckAsync(catalog,TestContext.Current.CancellationToken)).Recycled);
    }

    [Fact]
    public async Task Prepared_recycle_recovers_after_move_without_losing_photo()
    {
        using var temp=new TempDirectory();Seed(temp.Path,"A","a.jpg",[1,2,3,4]);
        string hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[]{1,2,3,4}));
        using(var journal=TransferJournal.Open(temp.Path))
        using(var command=journal.Connection.CreateCommand())
        {
            command.CommandText="INSERT INTO maintenance_operations VALUES('interrupted','Duplicate','Prepared','a.jpg','.picharbor/recycle/interrupted/a.jpg',$root,'keeper.jpg',$hash,4,$at);";
            command.Parameters.AddWithValue("$root",temp.Path);command.Parameters.AddWithValue("$hash",hash);command.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));command.ExecuteNonQuery();
        }
        Directory.CreateDirectory(Path.Combine(temp.Path,".picharbor","recycle","interrupted"));
        File.Move(Path.Combine(temp.Path,"a.jpg"),Path.Combine(temp.Path,".picharbor","recycle","interrupted","a.jpg"));
        var service=new LibraryMaintenanceService();var catalog=new LibraryCatalog(temp.Path);
        Assert.Empty((await service.CheckAsync(catalog,TestContext.Current.CancellationToken)).Errors);
        var copy=Assert.Single(service.ListRecycled(catalog));service.Restore(copy.Root,copy.OperationId);
        Assert.Equal(new byte[]{1,2,3,4},File.ReadAllBytes(Path.Combine(temp.Path,"a.jpg")));
    }

    [Fact]
    public async Task Reorganizing_members_keeps_stable_manual_selection()
    {
        using var first=new TempDirectory();using var second=new TempDirectory();
        Seed(first.Path,"A","a.jpg",[1,2,3,4]);Seed(second.Path,"B","b.jpg",[5,6,7,8]);
        var catalog=new LibraryCatalog(first.Path);catalog.Include(second.Path);
        await new LibraryMaintenanceService().CheckAsync(catalog,TestContext.Current.CancellationToken);
        var before=PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(first.Path).Select(m=>m.SelectionKey).Order().ToArray();
        var report=PicHarbor.Core.Reorganize.LibraryReorganizer.Execute(first.Path,OrganizeScheme.YearMonth,TestContext.Current.CancellationToken);
        Assert.Equal(0,report.Failed);Assert.Equal(0,report.PendingMembers);
        var after=PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(first.Path);
        Assert.Equal(before,after.Select(m=>m.SelectionKey).Order());
        Assert.All(after,m=>Assert.True(File.Exists(ArchivePathResolver.Resolve(m.ArchiveRoot!,m.DestPath))));
    }

    [Fact]
    public void Deletion_exclusion_does_not_hide_a_changed_source_timestamp()
    {
        using var temp=new TempDirectory();using var journal=TransferJournal.Open(temp.Path);journal.BindDevice("A");
        var now=DateTimeOffset.UtcNow;journal.ExcludeSource("/photo.jpg",4,now);
        Assert.True(journal.IsSourceExcluded("/photo.jpg",4,now));
        Assert.False(journal.IsSourceExcluded("/photo.jpg",4,now.AddMinutes(1)));
        journal.BindDevice("B");Assert.False(journal.IsSourceExcluded("/photo.jpg",4,now));
    }

    [Fact]
    public async Task Export_distinct_same_named_photos_preserves_both_contents_and_pair_stems()
    {
        using var first=new TempDirectory();using var second=new TempDirectory();
        Seed(first.Path,"A","IMG_1.HEIC",[1,2,3,4]);Seed(first.Path,"A","IMG_1.MOV",[5,6,7,8]);
        Seed(second.Path,"B","IMG_1.HEIC",[9,10,11,12]);Seed(second.Path,"B","IMG_1.MOV",[13,14,15,16]);
        var catalog=new LibraryCatalog(first.Path);catalog.Include(second.Path);
        var report=await PicHarbor.Core.iPhone.IPhoneSyncEngine.ExportAsync(first.Path,new(){DeviceModel="test",AlbumMode=PicHarbor.Core.iPhone.IPhoneAlbumMode.Flat},cancellationToken:TestContext.Current.CancellationToken);
        Assert.Equal(4,report.CopiedCount);Assert.Equal(0,report.FailedCount);
        var exported=Directory.GetFiles(Path.Combine(first.Path,".AppleSync","test"));Assert.Equal(4,exported.Length);
        Assert.Equal(2,exported.GroupBy(Path.GetFileNameWithoutExtension).Count());
        Assert.All(exported.GroupBy(Path.GetFileNameWithoutExtension),g=>Assert.Equal(2,g.Count()));
        Assert.Equal(new[]{1,5,9,13},exported.Select(p=>(int)File.ReadAllBytes(p)[0]).Order());
        var again=await PicHarbor.Core.iPhone.IPhoneSyncEngine.ExportAsync(first.Path,new(){DeviceModel="test",AlbumMode=PicHarbor.Core.iPhone.IPhoneAlbumMode.Flat},cancellationToken:TestContext.Current.CancellationToken);
        Assert.Equal(4,again.SkippedCount);
    }

    [Fact]
    public async Task Outgoing_transfer_refuses_replaced_same_size_content()
    {
        using var temp=new TempDirectory();Seed(temp.Path,"A","a.jpg",[1,2,3,4]);
        var catalog=new LibraryCatalog(temp.Path);catalog.ReadMedia();catalog.Save();
        File.WriteAllBytes(Path.Combine(temp.Path,"a.jpg"),[5,6,7,8]);
        var report=await PicHarbor.Core.iPhone.IPhoneSyncEngine.ExportAsync(temp.Path,new(){DeviceModel="test"},cancellationToken:TestContext.Current.CancellationToken);
        Assert.Equal(1,report.FailedCount);Assert.Equal(0,report.CopiedCount);
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path,".AppleSync","test"),"*",SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Disconnected_member_does_not_prompt_for_deletion_and_reconnect_deduplicates()
    {
        using var first=new TempDirectory();using var second=new TempDirectory();
        Seed(first.Path,"A","a.jpg",[1,2,3,4]);using(var initialize=TransferJournal.Open(second.Path)) { }
        var catalog=new LibraryCatalog(first.Path);catalog.Include(second.Path);
        string hidden=first.Path+".offline";
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()),Path.GetFullPath(hidden));
        SqliteConnection.ClearAllPools();Directory.Move(first.Path,hidden);
        int questions=0;
        try
        {
            var unavailable=Assert.Single(PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(first.Path));
            Assert.Equal(ReplicaState.Offline,unavailable.Availability);
            using var journal=TransferJournal.Open(second.Path);
            using var copier=new FileCopier(new Source("A",[1,2,3,4]),journal,new(),new Metadata(),second.Path,OrganizeScheme.YearMonth,
                deletedPhotoDecision:_=>{questions++;return null;});
            Assert.Equal(CopyStatus.Copied,(await copier.CopyAsync(new("/DCIM/a.jpg",4,null),TestContext.Current.CancellationToken)).Status);
            Assert.Equal(0,questions);Assert.Single(journal.ReadManifest());
        }
        finally { SqliteConnection.ClearAllPools();Directory.Move(hidden,first.Path); }
        var restored=new LibraryCatalog(second.Path);
        var check=await new LibraryMaintenanceService().CheckAsync(restored,TestContext.Current.CancellationToken);
        Assert.Empty(check.Errors);Assert.Equal(1,check.Recycled);Assert.Single(restored.ReadLogicalMedia());
    }

    [Fact]
    public void Local_cache_uses_current_user_encryption()
    {
        using var temp=new TempDirectory();string file=Path.Combine(temp.Path,"cache.bin");
        PicHarbor.Core.Util.UserProtectedFile.Write(file,"private-photo-name.jpg");
        Assert.Equal("private-photo-name.jpg",PicHarbor.Core.Util.UserProtectedFile.Read(file));
        Assert.DoesNotContain("private-photo-name.jpg",System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file)));
    }

    [Fact]
    public async Task Live_components_remain_related_after_one_moves_to_another_member()
    {
        using var first=new TempDirectory();using var second=new TempDirectory();
        Seed(first.Path,"A","IMG_1.HEIC",[1,2,3,4]);Seed(first.Path,"A","IMG_1.MOV",[5,6,7,8]);
        Seed(second.Path,"A","IMG_1.HEIC",[1,2,3,4]);Seed(second.Path,"A","IMG_1.MOV",[5,6,7,8]);
        using(var firstJournal=TransferJournal.Open(first.Path))
        using(var command=firstJournal.Connection.CreateCommand())
        { command.CommandText="UPDATE files SET copied_at='2000-01-01T00:00:00Z' WHERE dest_path='IMG_1.HEIC';";command.ExecuteNonQuery(); }
        var catalog=new LibraryCatalog(first.Path);catalog.Include(second.Path);
        File.Delete(Path.Combine(first.Path,"IMG_1.MOV"));
        await new LibraryMaintenanceService().CheckAsync(catalog,TestContext.Current.CancellationToken);
        var manifest=PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(first.Path);
        var image=Assert.Single(manifest,m=>m.DestPath.EndsWith(".HEIC"));var video=Assert.Single(manifest,m=>m.DestPath.EndsWith(".MOV"));
        Assert.NotEqual(image.ArchiveRoot,video.ArchiveRoot);
        Assert.Contains(video.MediaId!,image.RelatedMediaIds);
        var suggestion=Assert.Single(MediaPairMatcher.FindMissingPairs([image.SelectionKey],manifest));
        Assert.Equal(video.SelectionKey,suggestion.SelectionKey);
        File.Move(ArchivePathResolver.Resolve(video.ArchiveRoot!,video.DestPath),Path.Combine(video.ArchiveRoot!,"renamed.MOV"));
        await new LibraryMaintenanceService().CheckAsync(new LibraryCatalog(first.Path),TestContext.Current.CancellationToken);
        var rows=PicHarbor.Core.Search.ArchiveRepository.ReadLibrarySearchRows(first.Path);
        Assert.Single(LivePhotoDetector.FindLibraryPairs(rows));
        Assert.Single(MediaPairMatcher.FindMissingPairs([image.SelectionKey],PicHarbor.Core.Search.ArchiveRepository.ReadLibraryManifest(first.Path)));
        var exported=await PicHarbor.Core.iPhone.IPhoneSyncEngine.ExportAsync(first.Path,new(){DeviceModel="paired",AlbumMode=PicHarbor.Core.iPhone.IPhoneAlbumMode.Flat},cancellationToken:TestContext.Current.CancellationToken);
        Assert.Equal(2,exported.CopiedCount);
        Assert.Single(Directory.GetFiles(Path.Combine(first.Path,".AppleSync","paired")).GroupBy(Path.GetFileNameWithoutExtension));
    }

    [Fact]
    public void Same_names_on_independent_members_do_not_create_a_false_live_pair()
    {
        var image=new ManifestSearchRow("IMG_1.HEIC",null,4,null,null,null,null){ArchiveRoot="D:\\Photos",MediaId="image"};
        var video=new ManifestSearchRow("IMG_1.MOV",null,4,null,null,null,null){ArchiveRoot="E:\\Photos",MediaId="video"};
        Assert.Empty(LivePhotoDetector.FindLibraryPairs([image,video]));
        Assert.Empty(MediaPairMatcher.FindMissingPairs(["#media/image"],[
            new("IMG_1.HEIC",4,null,null){ArchiveRoot=image.ArchiveRoot,MediaId=image.MediaId},
            new("IMG_1.MOV",4,null,null){ArchiveRoot=video.ArchiveRoot,MediaId=video.MediaId}]));
    }

    private static void Seed(string root,string device,string path,byte[] bytes)
    {
        File.WriteAllBytes(Path.Combine(root,path),bytes);
        using var journal=TransferJournal.Open(root);journal.BindDevice(device);
        var file=new RemoteFile("/DCIM/"+path,bytes.Length,null);journal.EnsurePending(file);
        journal.MarkDone(file.Path,file.Size,path,MediaMetadata.Empty,DateTimeOffset.UtcNow,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private sealed class Metadata:IMediaMetadataExtractor { public MediaMetadata Extract(string path)=>MediaMetadata.Empty; }
    private sealed class Source(string id,byte[] content):IMediaSourceClient
    {
        public DeviceInfo? Device=>new(id,id,"test"); public void Dispose() { }
        public Task ConnectAsync(CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListDirectoryAsync(string path,CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<string>>([]);
        public Task<RemoteFileInfo> GetFileInfoAsync(string path,CancellationToken cancellationToken=default)=>Task.FromResult(new RemoteFileInfo(content.Length,null,false));
        public Task<Stream> OpenReadAsync(string path,CancellationToken cancellationToken=default)=>Task.FromResult<Stream>(new MemoryStream(content));
    }
}
