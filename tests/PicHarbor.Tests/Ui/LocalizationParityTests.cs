using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Ui;

public class LocalizationParityTests
{
    private static readonly string ResourcesDir = Path.Combine(
        FindRepoRoot(), "src", "PicHarbor.Gui", "Resources");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PicHarbor.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Could not find repository root containing PicHarbor.sln");
    }

    [Theory]
    [InlineData("StringResources.zh-CN.xaml")]
    [InlineData("StringResources.en-US.xaml")]
    [InlineData("StringResources.zh-HK.xaml")]
    public void ResourceFile_ExistsAndIsValidXml(string fileName)
    {
        string path = Path.Combine(ResourcesDir, fileName);
        File.Exists(path).ShouldBeTrue($"File should exist at {path}");

        var doc = XDocument.Load(path);
        doc.Root.ShouldNotBeNull();
        doc.Root!.Elements().Any().ShouldBeTrue();
    }

    [Fact]
    public void AllSupportedLanguages_HaveIdenticalKeySetAndNonEmptyValues()
    {
        var cnKeys = LoadKeysAndValues("StringResources.zh-CN.xaml");
        var enKeys = LoadKeysAndValues("StringResources.en-US.xaml");
        var hkKeys = LoadKeysAndValues("StringResources.zh-HK.xaml");

        cnKeys.Count.ShouldBeGreaterThan(300);

        // Verify key sets are identical
        var cnSet = cnKeys.Keys.ToHashSet();
        var enSet = enKeys.Keys.ToHashSet();
        var hkSet = hkKeys.Keys.ToHashSet();

        var missingInEn = cnSet.Except(enSet).ToList();
        var missingInHk = cnSet.Except(hkSet).ToList();
        var extraInEn = enSet.Except(cnSet).ToList();
        var extraInHk = hkSet.Except(cnSet).ToList();

        missingInEn.ShouldBeEmpty("All zh-CN keys must be present in en-US");
        missingInHk.ShouldBeEmpty("All zh-CN keys must be present in zh-HK");
        extraInEn.ShouldBeEmpty("en-US should not have keys that do not exist in zh-CN");
        extraInHk.ShouldBeEmpty("zh-HK should not have keys that do not exist in zh-CN");

        // Verify all values are non-empty
        foreach (var (k, v) in cnKeys)
        {
            string.IsNullOrWhiteSpace(v).ShouldBeFalse($"zh-CN key '{k}' has empty value");
        }
        foreach (var (k, v) in enKeys)
        {
            string.IsNullOrWhiteSpace(v).ShouldBeFalse($"en-US key '{k}' has empty value");
        }
        foreach (var (k, v) in hkKeys)
        {
            string.IsNullOrWhiteSpace(v).ShouldBeFalse($"zh-HK key '{k}' has empty value");
        }
    }

    private static Dictionary<string, string> LoadKeysAndValues(string fileName)
    {
        string path = Path.Combine(ResourcesDir, fileName);
        var doc = XDocument.Load(path);
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        var result = new Dictionary<string, string>();
        foreach (var elem in doc.Root!.Elements())
        {
            var keyAttr = elem.Attribute(xNs + "Key");
            if (keyAttr != null)
            {
                result[keyAttr.Value] = elem.Value;
            }
        }
        return result;
    }
}
