using System.Text.Json;

namespace ErGe.Core.Runtime;

public sealed class SessionAgentStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public SessionAgentStatusStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public static string GetDefaultPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return System.IO.Path.Combine(programData, "ErGe", "session-agent-status.json");
    }

    public void Save(SessionAgentStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Session Agent status path has no parent directory.");

        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var tempPath = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
