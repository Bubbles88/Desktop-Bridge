using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErGe.Core.Policy;

public sealed class FilePolicyStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public FilePolicyStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public static string GetDefaultPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return System.IO.Path.Combine(programData, "ErGe", "policy.json");
    }

    public (PersistentMode Mode, bool Healthy) Load()
    {
        if (!File.Exists(Path))
        {
            return (PersistentMode.AlwaysOff, true);
        }

        try
        {
            var json = File.ReadAllText(Path);
            var document = JsonSerializer.Deserialize<PersistentPolicyDocument>(json, JsonOptions);

            if (document is null || document.SchemaVersion != CurrentSchemaVersion)
            {
                return (PersistentMode.AlwaysOff, false);
            }

            return (document.PersistentMode, true);
        }
        catch (JsonException)
        {
            return (PersistentMode.AlwaysOff, false);
        }
        catch (IOException)
        {
            return (PersistentMode.AlwaysOff, false);
        }
        catch (UnauthorizedAccessException)
        {
            return (PersistentMode.AlwaysOff, false);
        }
    }

    public void Save(PersistentMode mode)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Policy path has no parent directory.");

        Directory.CreateDirectory(directory);

        var document = new PersistentPolicyDocument(CurrentSchemaVersion, mode);
        var json = JsonSerializer.Serialize(document, JsonOptions);
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
