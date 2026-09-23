using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using ErGe.Core.Ipc;
using System.Windows.Forms;

namespace ErGe.SessionAgent;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        if (args.Contains("--probe-screen", StringComparer.OrdinalIgnoreCase))
        {
            var screenInfo = GetScreenInfo();
            Console.WriteLine(SessionProtocol.Serialize(screenInfo));
            Console.WriteLine("ERGE_SESSION_SCREEN_PROBE_OK");
            return 0;
        }

        var oneRequest = args.Contains("--one-request", StringComparer.OrdinalIgnoreCase);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        if (oneRequest)
        {
            return await RunConnectionAsync(
                oneRequest: true,
                shutdown.Token);
        }

        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(
                    oneRequest: false,
                    shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Session Agent disconnected: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
        }

        return 0;
    }

    private static async Task<int> RunConnectionAsync(
        bool oneRequest,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            SessionProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);

        await pipe.ConnectAsync(5000, cancellationToken);

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

        using var process = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();

        var hello = new SessionAgentHello(
            Type: "hello",
            ProtocolVersion: SessionProtocol.Version,
            ProcessId: Environment.ProcessId,
            SessionId: process.SessionId,
            UserName: identity.Name ?? Environment.UserName);

        await writer.WriteLineAsync(SessionProtocol.Serialize(hello));

        var handshakeLine = await ReadLineWithTimeoutAsync(
            reader,
            TimeSpan.FromSeconds(5),
            cancellationToken);

        var handshake = SessionProtocol.Deserialize<SessionHandshake>(handshakeLine);
        if (!handshake.Accepted)
        {
            throw new UnauthorizedAccessException(
                handshake.Error ?? "Core rejected Session Agent authentication.");
        }

        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            var requestLine = await ReadLineWithTimeoutAsync(
                reader,
                TimeSpan.FromSeconds(10),
                cancellationToken);

            var request = SessionProtocol.Deserialize<SessionRequest>(requestLine);

            SessionResponse response;

            if (string.Equals(request.Action, "screen.info", StringComparison.Ordinal))
            {
                try
                {
                    response = new SessionResponse(
                        Type: "response",
                        RequestId: request.RequestId,
                        Success: true,
                        ScreenInfo: GetScreenInfo(),
                        Error: null);
                }
                catch (Exception ex)
                {
                    response = new SessionResponse(
                        Type: "response",
                        RequestId: request.RequestId,
                        Success: false,
                        ScreenInfo: null,
                        Error: $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                response = new SessionResponse(
                    Type: "response",
                    RequestId: request.RequestId,
                    Success: false,
                    ScreenInfo: null,
                    Error: $"Unsupported action: {request.Action}");
            }

            await writer.WriteLineAsync(SessionProtocol.Serialize(response));

            if (oneRequest)
            {
                return response.Success ? 0 : 1;
            }
        }

        return 0;
    }

    private static ScreenInfoSnapshot GetScreenInfo()
    {
        var monitors = Screen.AllScreens
            .Select(screen => new ScreenMonitorSnapshot(
                DeviceName: screen.DeviceName,
                X: screen.Bounds.X,
                Y: screen.Bounds.Y,
                Width: screen.Bounds.Width,
                Height: screen.Bounds.Height,
                Primary: screen.Primary))
            .ToArray();

        return new ScreenInfoSnapshot(monitors);
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
            throw new TimeoutException($"Named pipe response exceeded {timeout.TotalSeconds:g} seconds.");
        }
    }
}
