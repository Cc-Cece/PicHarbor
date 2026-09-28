using GetAndSee.Core.Device;
using GetAndSee.Core.Scope;
using Xunit;

namespace GetAndSee.Tests.Scope;

public class IPhoneBackupScopeResolverTests
{
    [Fact]
    public void Filter_AllScope_ReturnsAllFiles()
    {
        var files = new List<RemoteFile>
        {
            new("/DCIM/100APPLE/IMG_0001.HEIC", 1000, DateTimeOffset.UtcNow),
            new("/DCIM/100APPLE/IMG_0002.JPG", 2000, DateTimeOffset.UtcNow.AddDays(-5)),
            new("/DCIM/101APPLE/IMG_0003.MOV", 5000, DateTimeOffset.UtcNow.AddDays(-10)),
        };

        var criteria = new IPhoneBackupScopeCriteria { ScopeMode = ScopeMode.All };
        var result = IPhoneBackupScopeResolver.Filter(files, criteria);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Filter_DateScope_FiltersByDateAndPreservesLivePhotoPair()
    {
        var dtIn1 = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);
        var dtIn2 = new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero);
        var dtOut = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // IMG_0001.HEIC is in range; paired IMG_0001.MOV has dtOut timestamp.
        // Live Photo pairing preservation must keep IMG_0001.MOV in the result.
        var fileHeic = new RemoteFile("/DCIM/100APPLE/IMG_0001.HEIC", 1000, dtIn1);
        var fileMov = new RemoteFile("/DCIM/100APPLE/IMG_0001.MOV", 5000, dtOut);
        var fileOtherOut = new RemoteFile("/DCIM/100APPLE/IMG_0002.JPG", 2000, dtOut);
        var fileOtherIn = new RemoteFile("/DCIM/101APPLE/IMG_0003.JPG", 3000, dtIn2);

        var files = new List<RemoteFile> { fileHeic, fileMov, fileOtherOut, fileOtherIn };

        var criteria = new IPhoneBackupScopeCriteria
        {
            ScopeMode = ScopeMode.Date,
            DateFrom = new DateTime(2026, 5, 1),
            DateTo = new DateTime(2026, 5, 31)
        };

        var result = IPhoneBackupScopeResolver.Filter(files, criteria);

        Assert.Equal(3, result.Count);
        Assert.Contains(fileHeic, result);
        Assert.Contains(fileMov, result); // Live photo pair preserved!
        Assert.Contains(fileOtherIn, result);
        Assert.DoesNotContain(fileOtherOut, result);
    }

    [Fact]
    public void Filter_FolderScope_FiltersBySelectedSubfolders()
    {
        var file1 = new RemoteFile("/DCIM/100APPLE/IMG_0001.HEIC", 1000, DateTimeOffset.UtcNow);
        var file2 = new RemoteFile("/DCIM/100APPLE/IMG_0002.JPG", 2000, DateTimeOffset.UtcNow);
        var file3 = new RemoteFile("/DCIM/101APPLE/IMG_0003.MOV", 5000, DateTimeOffset.UtcNow);
        var file4 = new RemoteFile("/DCIM/102APPLE/IMG_0004.JPG", 4000, DateTimeOffset.UtcNow);

        var files = new List<RemoteFile> { file1, file2, file3, file4 };

        var criteria = new IPhoneBackupScopeCriteria
        {
            ScopeMode = ScopeMode.Folder,
            SelectedSubfolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "100APPLE" }
        };

        var result = IPhoneBackupScopeResolver.Filter(files, criteria);

        Assert.Equal(2, result.Count);
        Assert.Contains(file1, result);
        Assert.Contains(file2, result);
        Assert.DoesNotContain(file3, result);
        Assert.DoesNotContain(file4, result);
    }

    [Fact]
    public void GetSubfolderName_ParsesDcimSubfoldersCorrectly()
    {
        Assert.Equal("100APPLE", IPhoneBackupScopeResolver.GetSubfolderName("/DCIM/100APPLE/IMG_0001.JPG"));
        Assert.Equal("101CLOUD", IPhoneBackupScopeResolver.GetSubfolderName("/DCIM/101CLOUD/sub/IMG_0002.HEIC"));
        Assert.Equal("100APPLE", IPhoneBackupScopeResolver.GetSubfolderName("DCIM/100APPLE/IMG_0001.JPG"));
    }

    [Fact]
    public void GetMediaStemKey_ComputesStemKeyCorrectly()
    {
        string key1 = IPhoneBackupScopeResolver.GetMediaStemKey("/DCIM/100APPLE/IMG_0001.HEIC");
        string key2 = IPhoneBackupScopeResolver.GetMediaStemKey("/DCIM/100APPLE/IMG_0001.MOV");
        Assert.Equal(key1, key2);
        Assert.Equal("/DCIM/100APPLE/IMG_0001", key1);
    }
}
