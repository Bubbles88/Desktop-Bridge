using System.IO.Pipes;
using System.Security;
using System.Text;
using ErGe.Core.Actions;
using ErGe.Core.Ipc;
using ErGe.Core.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ErGe.Core.Service;

public sealed class LocalProviderPipeWorker : BackgroundService
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly SessionOwnerStore _ownerStore;
    private readonly CapabilityBroker _broker;
    private readonly ILogger<LocalProviderPipeWorker> _logger;

    public LocalProviderPipeWorker(
        SessionOwnerStore ownerStore,
        CapabilityBroker broker,
        ILogger<LocalProviderPipeWorker> logger)
    {
        _ownerStore = ownerStore;
        _broker = broker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = NamedPipeOwnerAuthenticator.CreateOwnerPipe(
                LocalProviderProtocol.PipeName);

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
                _logger.LogWarning(ex, "Local provider connection ended.");

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

        var hello = LocalProviderProtocol.Deserialize<LocalProviderHello>(helloLine);

        if (!string.Equals(hello.Type, "hello", StringComparison.Ordinal))
        {
            throw new SecurityException("Unexpected local provider handshake type.");
        }

        if (hello.ProtocolVersion != LocalProviderProtocol.Version)
        {
            throw new SecurityException(
                $"Unsupported local provider protocol version {hello.ProtocolVersion}.");
        }

        if (hello.ProcessId != authenticated.ProcessId)
        {
            throw new SecurityException("Local provider process identity mismatch.");
        }

        if (hello.SessionId != authenticated.SessionId)
        {
            throw new SecurityException("Local provider Windows session identity mismatch.");
        }

        if (string.IsNullOrWhiteSpace(hello.ClientName)
            || hello.ClientName.Length > 128)
        {
            throw new SecurityException("Local provider client name is invalid.");
        }

        if (string.IsNullOrWhiteSpace(hello.UserName))
        {
            throw new SecurityException("Local provider user name is missing.");
        }

        if (!string.Equals(
                hello.UserName,
                authenticated.UserName,
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Local provider reported user {ReportedUser}; Windows authenticated pipe user is {AuthenticatedUser}. SID authentication remains authoritative.",
                hello.UserName,
                authenticated.UserName);
        }

        await writer.WriteLineAsync(LocalProviderProtocol.Serialize(
            new LocalProviderHandshake(
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

            LocalProviderActionResponse response;

            try
            {
                var providerRequest =
                    LocalProviderProtocol.Deserialize<LocalProviderActionRequest>(line);

                if (!string.Equals(
                        providerRequest.Type,
                        "action",
                        StringComparison.Ordinal))
                {
                    response = new LocalProviderActionResponse(
                        Type: "response",
                        RequestId: providerRequest.RequestId ?? string.Empty,
                        Success: false,
                        ErrorCode: "request_invalid",
                        Error: "Unexpected local provider request type.",
                        Data: null);
                }
                else
                {
                    // Origin is assigned by Core. The local client never supplies
                    // or controls the trust classification.
                    var actionRequest = new ActionRequest(
                        ActionProtocol.Version,
                        providerRequest.RequestId,
                        ActionOrigin.LocalProvider,
                        providerRequest.Action,
                        providerRequest.Arguments);

                    var result = await _broker.ExecuteAsync(
                        actionRequest,
                        cancellationToken);

                    response = new LocalProviderActionResponse(
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
                response = new LocalProviderActionResponse(
                    Type: "response",
                    RequestId: string.Empty,
                    Success: false,
                    ErrorCode: "request_invalid",
                    Error: $"{ex.GetType().Name}: {ex.Message}",
                    Data: null);
            }

            await writer.WriteLineAsync(
                LocalProviderProtocol.Serialize(response));
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
                    "Local provider pipe closed unexpectedly.");
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Local provider handshake exceeded {timeout.TotalSeconds:g} seconds.");
        }
    }
}
