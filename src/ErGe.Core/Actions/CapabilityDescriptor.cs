namespace ErGe.Core.Actions;

public sealed record CapabilityDescriptor(
    string Name,
    bool RequiresInteractiveSession,
    bool ReadOnly);
