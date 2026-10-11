// 登记根持久化测试
// RootStore 读写 VaultStore.Directory 下的 vault_roots.json。
// 通过 VaultTestScope 重定向到独立临时目录，不污染真实服务数据。

using PicHarbor.Vault;

namespace PicHarbor.VaultTests;

public class RootStoreTests : IDisposable
{
    private readonly VaultTestScope _scope = new();
    private string FilePath => VaultStore.RootsPath;

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void Load_returns_empty_when_file_missing()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
        RootStore.Load().ShouldBeEmpty();
    }

    [Fact]
    public void Save_then_Load_roundtrips()
    {
        var roots = new List<string>
        {
            @"E:\Pictures\PicHarbor",
            @"D:\Archive\iPhone"
        };

        RootStore.Save(roots);
        var loaded = RootStore.Load();

        loaded.Count.ShouldBe(2);
        loaded.ShouldContain(@"E:\Pictures\PicHarbor");
        loaded.ShouldContain(@"D:\Archive\iPhone");
    }

    [Fact]
    public void Save_overwrites_previous_contents()
    {
        RootStore.Save([@"E:\Old\Path"]);
        RootStore.Save([@"F:\New\Path"]);

        var loaded = RootStore.Load();
        loaded.Count.ShouldBe(1);
        loaded[0].ShouldBe(@"F:\New\Path");
    }

    [Fact]
    public void Load_ignores_blank_entries()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "[\"E:\\\\Pictures\\\\PicHarbor\", \"\", \"  \"]");

        var loaded = RootStore.Load();
        loaded.Count.ShouldBe(1);
        loaded[0].ShouldBe(@"E:\Pictures\PicHarbor");
    }

    [Fact]
    public void Load_returns_empty_on_corrupt_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ this is not valid json");

        RootStore.Load().ShouldBeEmpty();
    }
}
