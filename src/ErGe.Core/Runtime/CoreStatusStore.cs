using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErGe.Core.Runtime;

public sealed class CoreStatusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public CoreStatusStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public static string GetDefaultPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return System.IO.Path.Combine(programData, "ErGe", "runtime-status.json");
    }

    public void Save(CoreStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var directory = System.IO.Path.GetDirectoryName(Path)
            ?? throw new InvalidOperationException("Runtime status path has no parent directory.");

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
