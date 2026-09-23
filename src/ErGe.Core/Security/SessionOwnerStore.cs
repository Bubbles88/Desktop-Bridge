using System.Text.Json;

namespace ErGe.Core.Security;

public sealed record SessionOwnerDocument(
    int SchemaVersion,
    string UserSid,
    string UserName);

public sealed class SessionOwnerStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public SessionOwnerStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public static string GetDefaultPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return System.IO.Path.Combine(programData, "ErGe", "session-owner.json");
    }

    public SessionOwnerDocument LoadRequired()
    {
        if (!File.Exists(Path))
        {
            throw new InvalidOperationException(
                $"Session owner configuration does not exist: {Path}");
        }

        var document = JsonSerializer.Deserialize<SessionOwnerDocument>(
            File.ReadAllText(Path),
            JsonOptions)
            ?? throw new InvalidOperationException("Session owner configuration could not be parsed.");

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported session owner schema version {document.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(document.UserSid))
        {
            throw new InvalidOperationException("Session owner SID is missing.");
        }

        if (string.IsNullOrWhiteSpace(document.UserName))
        {
            throw new InvalidOperationException("Session owner name is missing.");
        }

        return document;
    }
}
