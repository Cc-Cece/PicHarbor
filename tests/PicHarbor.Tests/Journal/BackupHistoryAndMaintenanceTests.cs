using System.IO;
using Microsoft.Data.Sqlite;
using PicHarbor.Core.Device;
using PicHarbor.Core.Journal;
using PicHarbor.Core.Organize;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Journal;

public sealed class BackupHistoryAndMaintenanceTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public void BackupSessions_and_history_items_are_recorded_and_retrieved()
    {
        using var journal = TransferJournal.Open(temp.Path);
        string deviceUid = "TEST-DEVICE-001";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // 1. Begin session
        long sessionId = journal.BeginBackupSession(deviceUid, "My Pixel 8", "Pixel 8", now);
        sessionId.ShouldBeGreaterThan(0);

        // 2. Record items
        journal.RecordHistoryItem(sessionId, "/DCIM/Camera/IMG_01.jpg", "2026/10/IMG_01.jpg", 1024 * 1024, now, "jpg");
        journal.RecordHistoryItem(sessionId, "/DCIM/Camera/VID_02.mp4", "2026/10/VID_02.mp4", 5 * 1024 * 1024, now, "mp4");

        // 3. Complete session
        journal.CompleteBackupSession(sessionId, 2, 6 * 1024 * 1024, "Completed");

        // 4. Retrieve sessions
        var sessions = journal.ReadBackupSessions(deviceUid);
        sessions.Count.ShouldBe(1);
        sessions[0].Id.ShouldBe(sessionId);
        sessions[0].DeviceName.ShouldBe("My Pixel 8");
        sessions[0].FilesCount.ShouldBe(2);
        sessions[0].TotalSizeBytes.ShouldBe(6 * 1024 * 1024);
        sessions[0].Status.ShouldBe("Completed");

        // 5. Retrieve items
        var items = journal.ReadBackupHistoryItems(sessionId);
        items.Count.ShouldBe(2);
        items[0].DeviceSourcePath.ShouldBe("/DCIM/Camera/IMG_01.jpg");
        items[0].DestPath.ShouldBe("2026/10/IMG_01.jpg");
        items[1].DeviceSourcePath.ShouldBe("/DCIM/Camera/VID_02.mp4");

        // 6. Summary
        var summary = journal.ReadDeviceBackupSummary(deviceUid);
        summary.TotalSessions.ShouldBe(1);
        summary.TotalFiles.ShouldBe(2);
        summary.TotalBytes.ShouldBe(6 * 1024 * 1024);
    }

    [Fact]
    public void RegisterOrUpdateDevice_applies_forced_sync_and_disambiguation()
    {
        using var journal = TransferJournal.Open(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Register first device
        string name1 = journal.RegisterOrUpdateDevice("UID-A", "Xiaomi 14", "Xiaomi 14", "SN-A", "Android", now);
        name1.ShouldBe("Xiaomi 14");

        // Register second device with the same name -> forced disambiguation
        string name2 = journal.RegisterOrUpdateDevice("UID-B", "Xiaomi 14", "Xiaomi 14", "SN-B", "Android", now);
        name2.ShouldBe("Xiaomi 14 (2)");

        // Rename first device -> forced update
        string name1Updated = journal.RegisterOrUpdateDevice("UID-A", "Xiaomi 14 Pro", "Xiaomi 14", "SN-A", "Android", now);
        name1Updated.ShouldBe("Xiaomi 14 Pro");

        var devices = journal.ReadDevices();
        devices.Count.ShouldBe(2);
        devices.First(d => d.Udid == "UID-A").Name.ShouldBe("Xiaomi 14 Pro");
        devices.First(d => d.Udid == "UID-B").Name.ShouldBe("Xiaomi 14 (2)");
    }

    [Fact]
    public void PruneRedundantHistory_safely_removes_old_audit_data_without_touching_manifest()
    {
        using var journal = TransferJournal.Open(temp.Path);
        DateTimeOffset past = DateTimeOffset.UtcNow.AddDays(-100);
        DateTimeOffset recent = DateTimeOffset.UtcNow.AddDays(-5);

        // Record a core file in manifest
        journal.EnsurePending(new RemoteFile("/DCIM/IMG_KEEP.jpg", 100, null));
        journal.MarkDone("/DCIM/IMG_KEEP.jpg", 100, "2026/10/IMG_KEEP.jpg", MediaMetadata.Empty, DateTimeOffset.UtcNow);

        // Past session
        long oldSession = journal.BeginBackupSession("UID-1", "Phone", "Phone", past);
        journal.RecordHistoryItem(oldSession, "/DCIM/old.jpg", "old.jpg", 200, past, "jpg");
        journal.CompleteBackupSession(oldSession, 1, 200, "Completed");

        // Recent session
        long newSession = journal.BeginBackupSession("UID-1", "Phone", "Phone", recent);
        journal.RecordHistoryItem(newSession, "/DCIM/new.jpg", "new.jpg", 300, recent, "jpg");
        journal.CompleteBackupSession(newSession, 1, 300, "Completed");

        // Prune older than 30 days
        var (prunedSessions, prunedRecords) = journal.PruneRedundantHistory(DateTimeOffset.UtcNow.AddDays(-30));
        prunedSessions.ShouldBe(1);
        prunedRecords.ShouldBe(1);

        // Old session should be gone, new session should remain
        var remainingSessions = journal.ReadBackupSessions("UID-1");
        remainingSessions.Count.ShouldBe(1);
        remainingSessions[0].Id.ShouldBe(newSession);

        // Core manifest file must still be intact!
        journal.ReadManifest().Count.ShouldBe(1);
        journal.ReadManifest()[0].DestPath.ShouldBe("2026/10/IMG_KEEP.jpg");
    }

    [Fact]
    public void RepairDatabaseConsistency_removes_ghost_records_for_deleted_disk_files()
    {
        using var journal = TransferJournal.Open(temp.Path);

        // File 1: actually exists on disk
        string dest1 = "photo_exists.jpg";
        File.WriteAllBytes(Path.Combine(temp.Path, dest1), new byte[10]);
        journal.EnsurePending(new RemoteFile("/DCIM/1.jpg", 10, null));
        journal.MarkDone("/DCIM/1.jpg", 10, dest1, MediaMetadata.Empty, DateTimeOffset.UtcNow);

        // File 2: deleted on disk by user (ghost record)
        string dest2 = "photo_deleted_by_user.jpg";
        journal.EnsurePending(new RemoteFile("/DCIM/2.jpg", 20, null));
        journal.MarkDone("/DCIM/2.jpg", 20, dest2, MediaMetadata.Empty, DateTimeOffset.UtcNow);

        journal.ReadManifest().Count.ShouldBe(2);

        // Repair consistency
        var (scanned, removed) = journal.RepairDatabaseConsistency(temp.Path);
        scanned.ShouldBe(2);
        removed.ShouldBe(1); // One ghost removed

        var manifestAfter = journal.ReadManifest();
        manifestAfter.Count.ShouldBe(1);
        manifestAfter[0].DestPath.ShouldBe(dest1);
    }
}
