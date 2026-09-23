using System.Text.Json;

namespace ErGe.Core.Actions;

public sealed record ActionRequest(
    int ProtocolVersion,
    string RequestId,
    ActionOrigin Origin,
    string Action,
    JsonElement Arguments);
