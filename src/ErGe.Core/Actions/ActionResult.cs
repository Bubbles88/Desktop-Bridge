using System.Text.Json;

namespace ErGe.Core.Actions;

public sealed record ActionResult(
    int ProtocolVersion,
    string RequestId,
    bool Success,
    string? ErrorCode,
    string? Error,
    JsonElement? Data)
{
    public static ActionResult Succeeded(
        string requestId,
        JsonElement? data = null) =>
        new(
            ActionProtocol.Version,
            requestId,
            Success: true,
            ErrorCode: null,
            Error: null,
            Data: data);

    public static ActionResult Failed(
        string requestId,
        string errorCode,
        string error) =>
        new(
            ActionProtocol.Version,
            requestId,
            Success: false,
            ErrorCode: errorCode,
            Error: error,
            Data: null);
}
