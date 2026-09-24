using System.IO.Pipes;
using System.Security;
using System.Text;
using ErGe.Core.Actions;
using ErGe.Core.Ipc;
using ErGe.Core.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ErGe.Core.Service;

public sealed class RemoteProviderPipeWorker : BackgroundService
{
    private const int MaxConcurrentClients = 8;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly SessionOwnerStore _ownerStore;
    private readonly CapabilityBroker _broker;
    private readonly ILogger<RemoteProviderPipeWorker> _logger;

    public RemoteProviderPipeWorker(
        SessionOwnerStore ownerStore,
        CapabilityBroker broker,
        ILogger<RemoteProviderPipeWorker> logger)
    {
        _ownerStore = ownerStore;
        _broker = broker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;

            try
            {
                pipe = NamedPipeOwnerAuthenticator.CreateOwnerPipe(
                    RemoteProviderProtocol.PipeName,
                    MaxConcurrentClients);

                await pipe.WaitForConnectionAsync(stoppingToken);

                var connectedPipe = pipe;
                pipe = null;

                _ = HandleConnectionSafeAsync(
                    connectedPipe,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync();
                }

                break;
            }
            catch (Exception ex)
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync();
                }

                _logger.LogWarning(ex, "Remote provider accept loop failed.");

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

    private async Task HandleConnectionSafeAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                await HandleConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Remote provider connection ended.");
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

        var hello = RemoteProviderProtocol.Deserialize<RemoteProviderHello>(helloLine);

        if (!string.Equals(hello.Type, "hello", StringComparison.Ordinal))
        {
            throw new SecurityException("Unexpected remote provider handshake type.");
        }

        if (hello.ProtocolVersion != RemoteProviderProtocol.Version)
        {
            throw new SecurityException(
                $"Unsupported remote provider protocol version {hello.ProtocolVersion}.");
        }

        if (hello.ProcessId != authenticated.ProcessId)
        {
            throw new SecurityException("Remote provider process identity mismatch.");
        }

        if (hello.SessionId != authenticated.SessionId)
        {
            throw new SecurityException("Remote provider Windows session identity mismatch.");
        }

        if (string.IsNullOrWhiteSpace(hello.ClientName)
            || hello.ClientName.Length > 128)
        {
            throw new SecurityException("Remote provider client name is invalid.");
        }

        if (string.IsNullOrWhiteSpace(hello.UserName))
        {
            throw new SecurityException("Remote provider user name is missing.");
        }

        if (!string.Equals(
                hello.UserName,
                authenticated.UserName,
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Remote provider reported user {ReportedUser}; Windows authenticated pipe user is {AuthenticatedUser}. SID authentication remains authoritative.",
                hello.UserName,
                authenticated.UserName);
        }

        await writer.WriteLineAsync(RemoteProviderProtocol.Serialize(
            new RemoteProviderHandshake(
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

            RemoteProviderActionResponse response;

            try
            {
                var providerRequest =
                    RemoteProviderProtocol.Deserialize<RemoteProviderActionRequest>(line);

                if (!string.Equals(
                        providerRequest.Type,
                        "action",
                        StringComparison.Ordinal))
                {
                    response = new RemoteProviderActionResponse(
                        Type: "response",
                        RequestId: providerRequest.RequestId ?? string.Empty,
                        Success: false,
                        ErrorCode: "request_invalid",
                        Error: "Unexpected remote provider request type.",
                        Data: null);
                }
                else
                {
                    var actionRequest = new ActionRequest(
                        ActionProtocol.Version,
                        providerRequest.RequestId,
                        ActionOrigin.RemoteProvider,
                        providerRequest.Action,
                        providerRequest.Arguments);

                    var result = await _broker.ExecuteAsync(
                        actionRequest,
                        cancellationToken);

                    response = new RemoteProviderActionResponse(
                        Type: "response",
                        RequestId: result.RequestId,
                        Success: result.Success,
                        ErrorCode: result.ErrorCode,
                        Error: result.Error,
                        Data: result.Data);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response = new RemoteProviderActionResponse(
                    Type: "response",
                    RequestId: string.Empty,
                    Success: false,
                    ErrorCode: "request_invalid",
                    Error: $"{ex.GetType().Name}: {ex.Message}",
                    Data: null);
            }

            await writer.WriteLineAsync(
                RemoteProviderProtocol.Serialize(response));
        }
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
                    "Remote provider pipe closed unexpectedly.");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Remote provider handshake exceeded {timeout.TotalSeconds:g} seconds.");
        }
    }
}
