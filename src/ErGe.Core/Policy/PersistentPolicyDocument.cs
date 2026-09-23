namespace ErGe.Core.Policy;

internal sealed record PersistentPolicyDocument(
    int SchemaVersion,
    PersistentMode PersistentMode);
