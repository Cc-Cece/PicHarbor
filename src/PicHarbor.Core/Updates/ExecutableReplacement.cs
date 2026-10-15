namespace PicHarbor.Core.Updates;

/// <summary>A same-volume executable replacement that retains its backup until startup validation.</summary>
public sealed class ExecutableReplacement
{
    private readonly string target;
    private bool applied;

    /// <summary>Constructs a transaction without changing the target.</summary>
    public ExecutableReplacement(string target)
    {
        this.target = Path.GetFullPath(target);
        Backup = this.target + ".picharbor-old-" + Guid.NewGuid().ToString("N");
    }

    /// <summary>Recovery image retained after successful replacement.</summary>
    public string Backup { get; }

    /// <summary>Copies to the target volume, verifies that exact copy, then replaces while retaining the old file.</summary>
    public async Task ApplyAsync(string source, Func<string, Task> verify)
    {
        if (applied) throw new InvalidOperationException("Replacement is already applied.");
        string local = target + ".picharbor-new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, local);
            await verify(local);
            File.Replace(local, target, Backup);
            applied = true;
        }
        finally { if (File.Exists(local)) File.Delete(local); }
    }

    /// <summary>Restores the old executable. A failure is deliberately propagated for explicit recovery reporting.</summary>
    public void Rollback()
    {
        if (!applied) return;
        File.Replace(Backup, target, null);
        applied = false;
    }
}
