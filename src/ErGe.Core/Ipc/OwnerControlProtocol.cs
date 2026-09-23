namespace ErGe.Core.Ipc;

public static class OwnerControlProtocol
{
    public const int Version = 1;
    public const string PipeName = "ErGeDesktop.OwnerControl.v1";

    public static string Serialize<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(
            value,
            ErGe.Core.Actions.ActionProtocol.JsonOptions);

    public static T Deserialize<T>(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(
            json,
            ErGe.Core.Actions.ActionProtocol.JsonOptions)
        ?? throw new InvalidOperationException(
            $"Unable to deserialize {typeof(T).Name}.");
}

public sealed record OwnerControlHello(
    string Type,
    int ProtocolVersion,
    int ProcessId,
    int SessionId,
    string UserName,
    string ClientName);

public sealed record OwnerControlHandshake(
    string Type,
    bool Accepted,
    string? UserName,
    string? Error);

public sealed record OwnerControlRequest(
    string Type,
    string RequestId,
    string Command,
    string? Value);

public sealed record OwnerControlState(
    string PersistentMode,
    string SessionOverride,
    string EffectiveAccess,
    bool RemoteAiAllowed,
    bool LocalAiAllowed,
    bool ConfigurationHealthy,
    bool SessionAgentConnected,
    string MachineName);

public sealed record OwnerControlResponse(
    string Type,
    string RequestId,
    bool Success,
    string? ErrorCode,
    string? Error,
    OwnerControlState? State);
