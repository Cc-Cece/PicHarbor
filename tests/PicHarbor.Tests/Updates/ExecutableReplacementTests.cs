using PicHarbor.Core.Updates;
using Shouldly;
using Xunit;

namespace PicHarbor.Tests.Updates;

public sealed class ExecutableReplacementTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PicHarborUpdateTest-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(directory, "用户重命名.exe");
    private string Source => Path.Combine(directory, "download.exe");

    public ExecutableReplacementTests()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Target, "old");
        File.WriteAllText(Source, "new");
    }

    [Fact]
    public async Task VerifiedReplacementRetainsBackupAndSupportsServiceFailureRollback()
    {
        var transaction = new ExecutableReplacement(Target);
        await transaction.ApplyAsync(Source, staged =>
        {
            Path.GetDirectoryName(staged).ShouldBe(directory);
            File.ReadAllText(staged).ShouldBe("new");
            File.ReadAllText(Target).ShouldBe("old");
            return Task.CompletedTask;
        });
        File.ReadAllText(Target).ShouldBe("new");
        File.ReadAllText(transaction.Backup).ShouldBe("old");
        transaction.Rollback();
        File.ReadAllText(Target).ShouldBe("old");
        Directory.GetFiles(directory, "*.picharbor-new-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task FailedVerificationNeverReplacesCurrentExecutable()
    {
        var transaction = new ExecutableReplacement(Target);
        await Should.ThrowAsync<InvalidDataException>(() => transaction.ApplyAsync(Source, _ => throw new InvalidDataException("hash mismatch")));
        File.ReadAllText(Target).ShouldBe("old");
        File.Exists(transaction.Backup).ShouldBeFalse();
        Directory.GetFiles(directory, "*.picharbor-new-*").ShouldBeEmpty();
    }

    [Fact]
    public async Task OccupiedTargetIsLeftIntact()
    {
        var transaction = new ExecutableReplacement(Target);
        using var held = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Should.ThrowAsync<IOException>(() => transaction.ApplyAsync(Source, _ => Task.CompletedTask));
        File.ReadAllText(Target).ShouldBe("old");
        File.Exists(transaction.Backup).ShouldBeFalse();
    }

    public void Dispose() => Directory.Delete(directory, true);
}
