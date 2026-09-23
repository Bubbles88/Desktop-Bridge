using System.IO.Pipes;
using System.Security;
using System.Text;
using ErGe.Core.Ipc;
using ErGe.Core.Policy;
using ErGe.Core.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ErGe.Core.Service;

public sealed class OwnerControlPipeWorker : BackgroundService
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly SessionOwnerStore _ownerStore;
    private readonly PolicyEngine _policyEngine;
    private readonly SessionAgentActionQueue _sessionAgentQueue;
    private readonly ILogger<OwnerControlPipeWorker> _logger;

    public OwnerControlPipeWorker(
        SessionOwnerStore ownerStore,
        PolicyEngine policyEngine,
        SessionAgentActionQueue sessionAgentQueue,
        ILogger<OwnerControlPipeWorker> logger)
    {
        _ownerStore = ownerStore;
        _policyEngine = policyEngine;
        _sessionAgentQueue = sessionAgentQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = NamedPipeOwnerAuthenticator.CreateOwnerPipe(
                OwnerControlProtocol.PipeName);

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                await HandleConnectionAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Owner Control connection ended.");

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        var authenticated = NamedPipeOwnerAuthenticator.AuthenticateOwner(
            pipe,
            _ownerStore);

        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true
        };

        var helloLine = await ReadLineWithTimeoutAsync(
            reader,
            HandshakeTimeout,
            cancellationToken);

        var hello = OwnerControlProtocol.Deserialize<OwnerControlHello>(helloLine);

        if (!string.Equals(hello.Type, "hello", StringComparison.Ordinal))
        {
            throw new SecurityException("Unexpected Owner Control handshake type.");
        }

        if (hello.ProtocolVersion != OwnerControlProtocol.Version)
        {
            throw new SecurityException(
                $"Unsupported Owner Control protocol version {hello.ProtocolVersion}.");
        }

        if (hello.ProcessId != authenticated.ProcessId)
        {
            throw new SecurityException("Owner Control process identity mismatch.");
        }

        if (hello.SessionId != authenticated.SessionId)
        {
            throw new SecurityException("Owner Control Windows session identity mismatch.");
        }

        if (string.IsNullOrWhiteSpace(hello.ClientName)
            || hello.ClientName.Length > 128)
        {
            throw new SecurityException("Owner Control client name is invalid.");
        }

        if (string.IsNullOrWhiteSpace(hello.UserName))
        {
            throw new SecurityException("Owner Control user name is missing.");
        }

        await writer.WriteLineAsync(OwnerControlProtocol.Serialize(
            new OwnerControlHandshake(
                Type: "handshake",
                Accepted: true,
                UserName: authenticated.UserName,
                Error: null)));

        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            OwnerControlResponse response;

            try
            {
                var request = OwnerControlProtocol.Deserialize<OwnerControlRequest>(line);
                response = ExecuteRequest(request);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response = new OwnerControlResponse(
                    Type: "response",
                    RequestId: string.Empty,
                    Success: false,
                    ErrorCode: "request_invalid",
                    Error: $"{ex.GetType().Name}: {ex.Message}",
                    State: BuildState());
            }

            await writer.WriteLineAsync(
                OwnerControlProtocol.Serialize(response));
        }
    }

    private OwnerControlResponse ExecuteRequest(OwnerControlRequest request)
    {
        if (!string.Equals(request.Type, "command", StringComparison.Ordinal))
        {
            return Failed(
                request.RequestId,
                "request_invalid",
                "Unexpected Owner Control request type.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            return Failed(
                string.Empty,
                "request_id_invalid",
                "Request ID is required.");
        }

        switch (request.Command)
        {
            case "get.status":
                break;

            case "set.persistent-mode":
                if (!Enum.TryParse<PersistentMode>(
                        request.Value,
                        ignoreCase: false,
                        out var persistentMode))
                {
                    return Failed(
                        request.RequestId,
                        "value_invalid",
                        "Persistent mode must be AlwaysOn or AlwaysOff.");
                }

                _policyEngine.SetPersistentMode(
                    persistentMode,
                    ControlSource.LocalOwner);
                break;

            case "set.session-override":
                if (!Enum.TryParse<SessionOverride>(
                        request.Value,
                        ignoreCase: false,
                        out var sessionOverride))
                {
                    return Failed(
                        request.RequestId,
                        "value_invalid",
                        "Session override must be None, ConnectNow, LocalOnly, or EmergencyBlock.");
                }

                _policyEngine.SetSessionOverride(
                    sessionOverride,
                    ControlSource.LocalOwner);
                break;

            case "clear.session-override":
                _policyEngine.ClearSessionOverride(ControlSource.LocalOwner);
                break;

            default:
                return Failed(
                    request.RequestId,
                    "command_not_found",
                    $"Owner Control command '{request.Command}' is not registered.");
        }

        return new OwnerControlResponse(
            Type: "response",
            RequestId: request.RequestId,
            Success: true,
            ErrorCode: null,
            Error: null,
            State: BuildState());
    }

    private OwnerControlResponse Failed(
        string requestId,
        string errorCode,
        string error) =>
        new(
            Type: "response",
            RequestId: requestId,
            Success: false,
            ErrorCode: errorCode,
            Error: error,
            State: BuildState());

    private OwnerControlState BuildState()
    {
        var policy = _policyEngine.Snapshot;

        return new OwnerControlState(
            PersistentMode: policy.PersistentMode.ToString(),
            SessionOverride: policy.SessionOverride.ToString(),
            EffectiveAccess: policy.EffectiveAccess.ToString(),
            RemoteAiAllowed: policy.RemoteAiAllowed,
            LocalAiAllowed: policy.LocalAiAllowed,
            ConfigurationHealthy: policy.ConfigurationHealthy,
            SessionAgentConnected: _sessionAgentQueue.Connected,
            MachineName: Environment.MachineName);
    }

    private static async Task<string> ReadLineWithTimeoutAsync(
        StreamReader reader,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        linked.CancelAfter(timeout);

        try
        {
            return await reader.ReadLineAsync(linked.Token)
                ?? throw new EndOfStreamException(
                    "Owner Control pipe closed unexpectedly.");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Owner Control handshake exceeded {timeout.TotalSeconds:g} seconds.");
        }
    }
}
