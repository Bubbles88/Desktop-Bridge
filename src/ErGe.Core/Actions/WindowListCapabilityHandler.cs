using System.Text.Json;

namespace ErGe.Core.Actions;

public sealed class WindowListCapabilityHandler : ICapabilityHandler
{
    private const int MaxTitleFilterLength = 512;

    private readonly IInteractiveCapabilityExecutor _executor;

    public WindowListCapabilityHandler(IInteractiveCapabilityExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        Name: "windows.list",
        RequiresInteractiveSession: true,
        ReadOnly: true);

    public async Task<ActionResult> ExecuteAsync(
        ActionRequest request,
        ActionContext context,
        CancellationToken cancellationToken)
    {
        var arguments = request.Arguments;

        if (arguments.ValueKind == JsonValueKind.Undefined)
        {
            arguments = JsonSerializer.SerializeToElement(new { });
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return ActionResult.Failed(
                request.RequestId,
                "arguments_invalid",
                "windows.list arguments must be a JSON object.");
        }

        string? titleFilter = null;
        var visibleOnly = true;

        foreach (var property in arguments.EnumerateObject())
        {
            switch (property.Name)
            {
                case "titleFilter":
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        titleFilter = null;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        titleFilter = property.Value.GetString();
                        if (titleFilter is not null && titleFilter.Length > MaxTitleFilterLength)
                        {
                            return ActionResult.Failed(
                                request.RequestId,
                                "arguments_invalid",
                                $"windows.list titleFilter exceeds {MaxTitleFilterLength} characters.");
                        }
                    }
                    else
                    {
                        return ActionResult.Failed(
                            request.RequestId,
                            "arguments_invalid",
                            "windows.list titleFilter must be a string or null.");
                    }
                    break;

                case "visibleOnly":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return ActionResult.Failed(
                            request.RequestId,
                            "arguments_invalid",
                            "windows.list visibleOnly must be boolean.");
                    }
                    visibleOnly = property.Value.GetBoolean();
                    break;

                default:
                    return ActionResult.Failed(
                        request.RequestId,
                        "arguments_invalid",
                        $"windows.list does not accept property '{property.Name}'.");
            }
        }

        var normalized = JsonSerializer.SerializeToElement(
            new
            {
                titleFilter,
                visibleOnly
            },
            ActionProtocol.JsonOptions);

        var data = await _executor.ExecuteAsync(
            Descriptor.Name,
            normalized,
            cancellationToken);

        return ActionResult.Succeeded(request.RequestId, data);
    }
}
