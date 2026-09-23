using ErGe.Core.Policy;

namespace ErGe.Core.Actions;

public sealed record ActionContext(
    ActionOrigin Origin,
    PolicySnapshot Policy);
