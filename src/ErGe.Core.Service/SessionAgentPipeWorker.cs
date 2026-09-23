using System.IO.Pipes;
using System.Security;
using System.Text;
using System.Text.Json;
using ErGe.Core.Actions;
using ErGe.Core.Ipc;
using ErGe.Core.Runtime;
using ErGe.Core.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ErGe.Core.Service;

public sealed class SessionAgentPipeWorker : BackgroundService
{
    private const int StatusSchemaVersion = 1;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

    private readonly SessionAgentStatusStore _statusStore;
    private readonly SessionOwnerStore _ownerStore;
    private readonly SessionAgentActionQueue _actionQueue;
    private readonly ILogger<SessionAgentPipeWorker> _logger;

    public SessionAgentPipeWorker(
        SessionAgentStatusStore statusStore,
        SessionOwnerStore ownerStore,
        SessionAgentActionQueue actionQueue,
        ILogger<SessionAgentPipeWorker> logger)
    {
        _statusStore = statusStore;
        _ownerStore = ownerStore;
        _actionQueue = actionQueue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _actionQueue.SetConnected(false);
        WriteDisconnectedStatus(null);

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = NamedPipeOwnerAuthenticator.CreateOwnerPipe(
                SessionProtocol.PipeName);

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
                _actionQueue.SetConnected(false);

                var message = $"{ex.GetType().Name}: {ex.Message}";
                _logger.LogWarning(ex, "Session Agent connection ended.");
                WriteDisconnectedStatus(message.Length <= 1000 ? message : message[..1000]);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _actionQueue.SetConnected(false);
        WriteDisconnectedStatus("Core stopping.");
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken stoppingToken)
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
            stoppingToken);

        var hello = SessionProtocol.Deserialize<SessionAgentHello>(helloLine);

        if (!string.Equals(hello.Type, "hello", StringComparison.Ordinal))
        {
            throw new SecurityException("Unexpected Session Agent handshake type.");
        }

        if (hello.ProtocolVersion != SessionProtocol.Version)
        {
            throw new SecurityException(
                $"Unsupported Session Agent protocol version {hello.ProtocolVersion}.");
        }

        if (hello.ProcessId != authenticated.ProcessId)
        {
            throw new SecurityException("Session Agent process identity mismatch.");
        }

        if (hello.SessionId != authenticated.SessionId)
        {
            throw new SecurityException("Session Agent Windows session identity mismatch.");
        }

        if (string.IsNullOrWhiteSpace(hello.UserName))
        {
            throw new SecurityException("Session Agent user name is missing.");
        }

        if (!string.Equals(
                hello.UserName,
                authenticated.UserName,
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Session Agent reported user {ReportedUser}; Windows authenticated pipe user is {AuthenticatedUser}. SID authentication remains authoritative.",
                hello.UserName,
                authenticated.UserName);
        }

        var handshake = new SessionHandshake(
            Type: "handshake",
            Accepted: true,
            UserName: authenticated.UserName,
            Error: null);

        await writer.WriteLineAsync(SessionProtocol.Serialize(handshake));

        var connectedAtUtc = DateTimeOffset.UtcNow;
        ScreenInfoSnapshot? lastScreenInfo = null;

        _actionQueue.SetConnected(true);

        try
        {
            var nextProbeAt = DateTimeOffset.UtcNow;

            while (!stoppingToken.IsCancellationRequested && pipe.IsConnected)
            {
                if (_actionQueue.TryRead(out var pending) && pending is not null)
                {
                    lastScreenInfo = await ExecutePendingActionAsync(
                        pending,
                        reader,
                        writer,
                        lastScreenInfo,
                        stoppingToken);

                    WriteConnectedStatus(
                        authenticated,
                        connectedAtUtc,
                        lastScreenInfo);

                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                if (now >= nextProbeAt)
                {
                    lastScreenInfo = await ExecuteScreenInfoRequestAsync(
                        requestId: Guid.NewGuid().ToString("N"),
                        reader,
                        writer,
                        stoppingToken);

                    WriteConnectedStatus(
                        authenticated,
                        connectedAtUtc,
                        lastScreenInfo);

                    nextProbeAt = DateTimeOffset.UtcNow.Add(ProbeInterval);
                    continue;
                }

                var waitForAction = _actionQueue
                    .WaitToReadAsync(stoppingToken)
                    .AsTask();

                var waitForProbe = Task.Delay(
                    nextProbeAt - now,
                    stoppingToken);

                await Task.WhenAny(waitForAction, waitForProbe);
            }
        }
        finally
        {
            _actionQueue.SetConnected(false);
        }

        _statusStore.Save(new SessionAgentStatusSnapshot(
            StatusSchemaVersion,
            Connected: false,
            Authenticated: true,
            ProcessId: authenticated.ProcessId,
            SessionId: authenticated.SessionId,
            UserName: authenticated.UserName,
            ConnectedAtUtc: connectedAtUtc,
            LastSeenUtc: DateTimeOffset.UtcNow,
            ScreenInfo: lastScreenInfo,
            LastError: "Session Agent disconnected."));
    }

    private async Task<ScreenInfoSnapshot?> ExecutePendingActionAsync(
        PendingSessionAction pending,
        StreamReader reader,
        StreamWriter writer,
        ScreenInfoSnapshot? lastScreenInfo,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(pending.Action, "screen.info", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Session Agent transport does not support action '{pending.Action}'.");
            }

            var screenInfo = await ExecuteScreenInfoRequestAsync(
                pending.QueueId,
                reader,
                writer,
                cancellationToken);

            var data = JsonSerializer.SerializeToElement(
                screenInfo,
                ActionProtocol.JsonOptions);

            _actionQueue.Complete(pending, data);
            return screenInfo;
        }
        catch (Exception ex)
        {
            _actionQueue.Fail(pending, ex);
            return lastScreenInfo;
        }
    }

    private static async Task<ScreenInfoSnapshot> ExecuteScreenInfoRequestAsync(
        string requestId,
        StreamReader reader,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        var request = new SessionRequest(
            Type: "request",
            RequestId: requestId,
            Action: "screen.info");

        await writer.WriteLineAsync(SessionProtocol.Serialize(request));

        var responseLine = await ReadLineWithTimeoutAsync(
            reader,
            RequestTimeout,
            cancellationToken);

        var response = SessionProtocol.Deserialize<SessionResponse>(responseLine);

        if (!string.Equals(response.Type, "response", StringComparison.Ordinal)
            || !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Session Agent response correlation failed.");
        }

        if (!response.Success || response.ScreenInfo is null)
        {
            throw new InvalidOperationException(
                response.Error ?? "Session Agent screen.info request failed.");
        }

        if (response.ScreenInfo.Monitors.Count == 0
            || response.ScreenInfo.Monitors.Any(monitor => monitor.Width <= 0 || monitor.Height <= 0))
        {
            throw new InvalidDataException("Session Agent returned invalid monitor geometry.");
        }

        return response.ScreenInfo;
    }

    private void WriteConnectedStatus(
        AuthenticatedPipeClient authenticated,
        DateTimeOffset connectedAtUtc,
        ScreenInfoSnapshot? screenInfo)
    {
        _statusStore.Save(new SessionAgentStatusSnapshot(
            StatusSchemaVersion,
            Connected: true,
            Authenticated: true,
            ProcessId: authenticated.ProcessId,
            SessionId: authenticated.SessionId,
            UserName: authenticated.UserName,
            ConnectedAtUtc: connectedAtUtc,
            LastSeenUtc: DateTimeOffset.UtcNow,
            ScreenInfo: screenInfo,
            LastError: null));
    }

    private static async Task<string> ReadLineWithTimeoutAsync(
        StreamReader reader,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);

        try
        {
            return await reader.ReadLineAsync(linked.Token)
                ?? throw new EndOfStreamException("Named pipe closed unexpectedly.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Named pipe response exceeded {timeout.TotalSeconds:g} seconds.");
        }
    }

    private void WriteDisconnectedStatus(string? error)
    {
        _statusStore.Save(new SessionAgentStatusSnapshot(
            StatusSchemaVersion,
            Connected: false,
            Authenticated: false,
            ProcessId: null,
            SessionId: null,
            UserName: null,
            ConnectedAtUtc: null,
            LastSeenUtc: null,
            ScreenInfo: null,
            LastError: error));
    }
}
