using PicHarbor.Core.Journal;
using PicHarbor.Tests.TestSupport;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Journal;

/// <summary>
/// 覆盖设备标识的人工修正与迁移：修正序列号不得影响其它设备，重新识别必须保留历史批次。
/// </summary>
public sealed class DeviceIdentityMigrationTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public void UpdateDeviceHardwareSerial_only_touches_target_device()
    {
        using var journal = TransferJournal.Open(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        journal.RegisterOrUpdateDevice("android:192.168.1.5", "Pixel 8", "Pixel 8", "android:192.168.1.5", "Android", now);
        journal.RegisterOrUpdateDevice("android:other", "Xiaomi 14", "Xiaomi 14", "android:other", "Android", now);

        bool updated = journal.UpdateDeviceHardwareSerial("android:192.168.1.5", "R58M12345AB");

        updated.ShouldBeTrue();
        var devices = journal.ReadDevices();
        devices.First(d => d.Udid == "android:192.168.1.5").HardwareSerial.ShouldBe("R58M12345AB");
        devices.First(d => d.Udid == "android:other").HardwareSerial.ShouldBe("android:other");
    }

    [Fact]
    public void UpdateDeviceHardwareSerial_reports_miss_for_unknown_device()
    {
        using var journal = TransferJournal.Open(temp.Path);

        journal.UpdateDeviceHardwareSerial("android:does-not-exist", "SN-1").ShouldBeFalse();
    }

    [Fact]
    public void IsHardwareSerialTakenByOther_detects_duplicates_and_excludes_self()
    {
        using var journal = TransferJournal.Open(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        journal.RegisterOrUpdateDevice("android:a", "A", "A", "SN-SHARED", "Android", now);
        journal.RegisterOrUpdateDevice("android:b", "B", "B", "SN-B", "Android", now);

        // 通用串被多台设备上报时，必须能被交叉比对发现。
        journal.IsHardwareSerialTakenByOther("SN-SHARED", "android:b").ShouldBeTrue();
        journal.IsHardwareSerialTakenByOther("SN-SHARED", "android:a").ShouldBeFalse();
        journal.IsHardwareSerialTakenByOther("SN-NEW", "android:a").ShouldBeFalse();
    }

    [Fact]
    public void MigrateDeviceUdid_preserves_backup_history()
    {
        using var journal = TransferJournal.Open(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        journal.RegisterOrUpdateDevice("android:old-host", "Pixel 8", "Pixel 8", null, "Android", now);
        long sessionId = journal.BeginBackupSession("android:old-host", "Pixel 8", "Pixel 8", now);
        journal.RecordHistoryItem(sessionId, "/DCIM/Camera/IMG_01.jpg", "2026/10/IMG_01.jpg", 1024, now, "jpg");

        journal.MigrateDeviceUdid("android:old-host", "android:R58M12345AB").ShouldBeTrue();

        var devices = journal.ReadDevices();
        devices.ShouldContain(d => d.Udid == "android:R58M12345AB");
        devices.ShouldNotContain(d => d.Udid == "android:old-host");
        journal.ReadBackupSessions("android:R58M12345AB").Count.ShouldBe(1);
        journal.ReadBackupSessions("android:old-host").Count.ShouldBe(0);
    }

    [Fact]
    public void Device_archive_folder_is_recorded_with_the_device_identity()
    {
        using var journal = TransferJournal.Open(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        journal.RegisterOrUpdateDevice("android:PH-AAAAAA", "K40", "M2012K11AC", "PH-AAAAAA", "Android", now);

        journal.GetDeviceArchiveFolder("android:PH-AAAAAA").ShouldBeNull();

        journal.SetDeviceArchiveFolder("android:PH-AAAAAA", "K40").ShouldBeTrue();
        journal.GetDeviceArchiveFolder("android:PH-AAAAAA").ShouldBe("K40");
    }

    [Fact]
    public void IsDeviceArchiveFolderTakenByOther_excludes_self_and_falls_back_to_display_name()
    {
        using var journal = TransferJournal.Open(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        journal.RegisterOrUpdateDevice("android:PH-AAAAAA", "K40", "M2012K11AC", "PH-AAAAAA", "Android", now);
        journal.RegisterOrUpdateDevice("android:PH-BBBBBB", "Other", "M2012K11AC", "PH-BBBBBB", "Android", now);
        journal.SetDeviceArchiveFolder("android:PH-AAAAAA", "K40");

        // 自己不算冲突——这正是「填自己的目录名反被拒」的根因。
        journal.IsDeviceArchiveFolderTakenByOther("K40", "android:PH-AAAAAA").ShouldBeFalse();
        // 别人占用则冲突。
        journal.IsDeviceArchiveFolderTakenByOther("K40", "android:PH-BBBBBB").ShouldBeTrue();
        // 老记录没有 archive_folder 时以显示名兜底。
        journal.IsDeviceArchiveFolderTakenByOther("Other", "android:PH-AAAAAA").ShouldBeTrue();
    }

    [Fact]
    public void InferDeviceArchiveFolderFromHistory_takes_first_path_segment()
    {
        using var journal = TransferJournal.Open(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        journal.RegisterOrUpdateDevice("uid-x", "iPhone", "iPhone15,5", null, "iPhone", now);
        long sessionId = journal.BeginBackupSession("uid-x", "iPhone", "iPhone15,5", now);
        journal.RecordHistoryItem(sessionId, "/DCIM/IMG_1.HEIC", @"15Plus\2026-06\IMG_1.HEIC", 100, now, "heic");

        // 显示名与归档目录名不一致（用户改过名）时，靠备份历史推导。
        journal.InferDeviceArchiveFolderFromHistory("uid-x").ShouldBe("15Plus");
        journal.InferDeviceArchiveFolderFromHistory("uid-none").ShouldBeNull();
    }

    [Fact]
    public void MigrateDeviceUdid_reports_false_when_source_does_not_exist()
    {
        using var journal = TransferJournal.Open(temp.Path);

        // 源记录不存在时必须返回 false —— 否则会打出「历史已迁移」的误导日志。
        journal.MigrateDeviceUdid("android:missing", "android:new").ShouldBeFalse();
    }

    [Fact]
    public void MigrateDeviceUdid_rejects_no_op_and_taken_target()
    {
        using var journal = TransferJournal.Open(temp.Path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        journal.RegisterOrUpdateDevice("android:source", "Source", "Source", null, "Android", now);
        journal.RegisterOrUpdateDevice("android:taken", "Taken", "Taken", null, "Android", now);

        journal.MigrateDeviceUdid("android:source", "android:source").ShouldBeFalse();
        journal.MigrateDeviceUdid("android:source", "android:taken").ShouldBeFalse();

        // 拒绝后原记录必须原样保留。
        var devices = journal.ReadDevices();
        devices.ShouldContain(d => d.Udid == "android:source");
        devices.ShouldContain(d => d.Udid == "android:taken");
    }
}
