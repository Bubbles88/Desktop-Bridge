using ErGe.Core.Policy;

namespace ErGe.Core.Actions;

public sealed class CapabilityBroker
{
    private readonly PolicyEngine _policyEngine;
    private readonly IReadOnlyDictionary<string, ICapabilityHandler> _handlers;

    public CapabilityBroker(
        PolicyEngine policyEngine,
        IEnumerable<ICapabilityHandler> handlers)
    {
        _policyEngine = policyEngine ?? throw new ArgumentNullException(nameof(policyEngine));

        var materialized = handlers?.ToArray()
            ?? throw new ArgumentNullException(nameof(handlers));

        _handlers = materialized.ToDictionary(
            handler => handler.Descriptor.Name,
            StringComparer.Ordinal);
    }

    public IReadOnlyCollection<CapabilityDescriptor> Capabilities =>
        _handlers.Values
            .Select(handler => handler.Descriptor)
            .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal)
            .ToArray();

    public async Task<ActionResult> ExecuteAsync(
        ActionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ValidateRequest(request);
        if (validation is not null)
        {
            return validation;
        }

        var policy = _policyEngine.Snapshot;
        if (!policy.ConfigurationHealthy)
        {
            return ActionResult.Failed(
                request.RequestId,
                "policy_unhealthy",
                "Local policy configuration is unhealthy; action execution fails closed.");
        }

        if (!IsOriginAllowed(request.Origin, policy))
        {
            return ActionResult.Failed(
                request.RequestId,
                "policy_denied",
                request.Origin == ActionOrigin.RemoteProvider
                    ? "Remote AI access is not allowed by local owner policy."
                    : "Local AI access is not allowed by local owner policy.");
        }

        if (!_handlers.TryGetValue(request.Action, out var handler))
        {
            return ActionResult.Failed(
                request.RequestId,
                "capability_not_found",
                $"Capability '{request.Action}' is not registered.");
        }

        var context = new ActionContext(request.Origin, policy);

        try
        {
            var result = await handler.ExecuteAsync(
                request,
                context,
                cancellationToken);

            if (!string.Equals(result.RequestId, request.RequestId, StringComparison.Ordinal))
            {
                return ActionResult.Failed(
                    request.RequestId,
                    "capability_correlation_error",
                    "Capability result request ID did not match the request.");
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ActionResult.Failed(
                request.RequestId,
                "capability_failed",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ActionResult? ValidateRequest(ActionRequest request)
    {
        if (request.ProtocolVersion != ActionProtocol.Version)
        {
            return ActionResult.Failed(
                request.RequestId ?? string.Empty,
                "protocol_version_unsupported",
                $"Unsupported action protocol version {request.ProtocolVersion}.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return ActionResult.Failed(
                string.Empty,
                "request_id_invalid",
                "Request ID is required.");
        }

        if (request.RequestId.Length > 128)
        {
            return ActionResult.Failed(
                request.RequestId[..128],
                "request_id_invalid",
                "Request ID exceeds 128 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Action))
        {
            return ActionResult.Failed(
                request.RequestId,
                "action_invalid",
                "Action name is required.");
        }

        return null;
    }

    private static bool IsOriginAllowed(
        ActionOrigin origin,
        PolicySnapshot policy) =>
        origin switch
        {
            ActionOrigin.LocalProvider => policy.LocalAiAllowed,
            ActionOrigin.RemoteProvider => policy.RemoteAiAllowed,
            _ => false
        };
}
