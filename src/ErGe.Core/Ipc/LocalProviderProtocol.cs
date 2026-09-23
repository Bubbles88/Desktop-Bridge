using System.Text.Json;
using ErGe.Core.Actions;

namespace ErGe.Core.Ipc;

public static class LocalProviderProtocol
{
    public const int Version = 1;
    public const string PipeName = "ErGeDesktop.LocalProvider.v1";

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, ActionProtocol.JsonOptions);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ActionProtocol.JsonOptions)
        ?? throw new InvalidOperationException(
            $"Unable to deserialize {typeof(T).Name}.");
}

public sealed record LocalProviderHello(
    string Type,
    int ProtocolVersion,
    int ProcessId,
    int SessionId,
    string UserName,
    string ClientName);

public sealed record LocalProviderHandshake(
    string Type,
    bool Accepted,
    string? UserName,
    string? Error);

public sealed record LocalProviderActionRequest(
    string Type,
    string RequestId,
    string Action,
    JsonElement Arguments);

public sealed record LocalProviderActionResponse(
    string Type,
    string RequestId,
    bool Success,
    string? ErrorCode,
    string? Error,
    JsonElement? Data);
