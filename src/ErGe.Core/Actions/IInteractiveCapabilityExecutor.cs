using System.Text.Json;

namespace ErGe.Core.Actions;

public interface IInteractiveCapabilityExecutor
{
    Task<JsonElement> ExecuteAsync(
        string action,
        JsonElement arguments,
        CancellationToken cancellationToken);
}
