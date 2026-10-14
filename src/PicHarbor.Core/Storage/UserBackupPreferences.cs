using System.Text.Json.Nodes;

namespace PicHarbor.Core.Storage;

/// <summary>User-wide deletion and verification preferences shared by GUI and CLI.</summary>
public sealed record RebackupPolicy(bool RebackupDeletedPhotos = true, bool QuestionAsked = false, bool StrictVerification = false, bool FastVerification = false);

/// <summary>Updates only backup preference fields, preserving encrypted credentials and other configuration.</summary>
public sealed class UserBackupPreferences
{
    private readonly string path;
    /// <summary>Uses the current user configuration, or an isolated directory supplied by a test.</summary>
    public UserBackupPreferences(string? directory = null) => path=Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"PicHarbor"),"config.json");
    /// <summary>Reads backup preferences without decrypting or exposing credentials.</summary>
    public RebackupPolicy Load()
    {
        if(!File.Exists(path)) return new();
        var json=JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid user settings.");
        return new(json["RebackupDeletedPhotos"]?.GetValue<bool>() ?? true,json["RebackupQuestionAsked"]?.GetValue<bool>() ?? false,json["StrictBackupVerification"]?.GetValue<bool>() ?? false,json["FastBackupVerification"]?.GetValue<bool>() ?? false);
    }
    /// <summary>Persists the user's first completed deletion decision atomically.</summary>
    public void RecordDecision(bool rebackup)
    {
        var json=File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))! : new JsonObject();
        json["RebackupDeletedPhotos"]=rebackup;json["RebackupQuestionAsked"]=true;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temporary,json.ToJsonString());File.Move(temporary,path,overwrite:true);
    }
}
