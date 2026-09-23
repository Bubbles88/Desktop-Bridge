using System.Text.Json;

namespace ErGe.Core.Ipc;

public static class SessionProtocol
{
    public const int Version = 1;
    public const string PipeName = "ErGeDesktop.SessionAgent.v1";

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"Unable to deserialize {typeof(T).Name}.");
}

public sealed record SessionAgentHello(
    string Type,
    int ProtocolVersion,
    int ProcessId,
    int SessionId,
    string UserName);

public sealed record SessionHandshake(
    string Type,
    bool Accepted,
    string? UserName,
    string? Error);

public sealed record SessionRequest(
    string Type,
    string RequestId,
    string Action,
    JsonElement Arguments = default);

public sealed record SessionResponse(
    string Type,
    string RequestId,
    bool Success,
    ScreenInfoSnapshot? ScreenInfo,
    string? Error,
    AvailabilityInfoSnapshot? Availability = null);

public sealed record ScreenInfoSnapshot(
    IReadOnlyList<ScreenMonitorSnapshot> Monitors);

public sealed record ScreenMonitorSnapshot(
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool Primary);

public sealed record AvailabilityInfoSnapshot(
    bool KeepAwake);
