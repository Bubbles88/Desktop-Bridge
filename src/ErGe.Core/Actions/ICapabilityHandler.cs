namespace ErGe.Core.Actions;

public interface ICapabilityHandler
{
    CapabilityDescriptor Descriptor { get; }

    Task<ActionResult> ExecuteAsync(
        ActionRequest request,
        ActionContext context,
        CancellationToken cancellationToken);
}
