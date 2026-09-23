using System.Text.Json;

namespace ErGe.Core.Actions;

public sealed class ScreenInfoCapabilityHandler : ICapabilityHandler
{
    private static readonly JsonElement EmptyArguments =
        JsonDocument.Parse("{}").RootElement.Clone();

    private readonly IInteractiveCapabilityExecutor _executor;

    public ScreenInfoCapabilityHandler(IInteractiveCapabilityExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Name: "screen.info",
        RequiresInteractiveSession: true,
        ReadOnly: true);

    public async Task<ActionResult> ExecuteAsync(
        ActionRequest request,
        ActionContext context,
        CancellationToken cancellationToken)
    {
        var arguments = request.Arguments.ValueKind == JsonValueKind.Undefined
            ? EmptyArguments
            : request.Arguments;

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return ActionResult.Failed(
                request.RequestId,
                "arguments_invalid",
                "screen.info arguments must be a JSON object.");
        }

        if (arguments.EnumerateObject().Any())
        {
            return ActionResult.Failed(
                request.RequestId,
                "arguments_invalid",
                "screen.info does not accept arguments.");
        }

        var data = await _executor.ExecuteAsync(
            Descriptor.Name,
            arguments,
            cancellationToken);

        return ActionResult.Succeeded(request.RequestId, data);
    }
}
