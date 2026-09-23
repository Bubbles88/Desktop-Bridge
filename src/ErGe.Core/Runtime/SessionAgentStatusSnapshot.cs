using ErGe.Core.Ipc;

namespace ErGe.Core.Runtime;

public sealed record SessionAgentStatusSnapshot(
    int SchemaVersion,
    bool Connected,
    bool Authenticated,
    int? ProcessId,
    int? SessionId,
    string? UserName,
    DateTimeOffset? ConnectedAtUtc,
    DateTimeOffset? LastSeenUtc,
    ScreenInfoSnapshot? ScreenInfo,
    string? LastError);
