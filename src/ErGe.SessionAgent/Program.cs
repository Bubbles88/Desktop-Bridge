using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ErGe.Core.Ipc;
using System.Windows.Forms;

namespace ErGe.SessionAgent;

internal static class Program
{
    private const int MaxWindowRows = 512;
    private const int MaxTitleFilterLength = 512;

    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000
    }

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            TrySetKeepAwake(false);
        };

        if (args.Contains("--probe-screen", StringComparer.OrdinalIgnoreCase))
        {
            var screenInfo = GetScreenInfo();
            Console.WriteLine(SessionProtocol.Serialize(screenInfo));
            Console.WriteLine("ERGE_SESSION_SCREEN_PROBE_OK");
            return 0;
        }

        if (args.Contains("--probe-windows", StringComparer.OrdinalIgnoreCase))
        {
            var windowList = GetWindowList(titleFilter: null, visibleOnly: true);
            Console.WriteLine(SessionProtocol.Serialize(windowList));
            Console.WriteLine("ERGE_SESSION_WINDOWS_PROBE_OK");
            return 0;
        }

        if (args.Contains("--probe-availability", StringComparer.OrdinalIgnoreCase))
        {
            SetKeepAwake(true);
            Console.WriteLine(SessionProtocol.Serialize(
                new AvailabilityInfoSnapshot(KeepAwake: true)));

            SetKeepAwake(false);
            Console.WriteLine("ERGE_SESSION_AVAILABILITY_PROBE_OK");
            return 0;
        }

        var oneRequest = args.Contains("--one-request", StringComparer.OrdinalIgnoreCase);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
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
        finally
        {
            TrySetKeepAwake(false);
        }
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

            var response = HandleRequest(request);

            await writer.WriteLineAsync(SessionProtocol.Serialize(response));

            if (oneRequest)
            {
                return response.Success ? 0 : 1;
            }
        }

        return 0;
    }

    private static SessionResponse HandleRequest(SessionRequest request)
    {
        if (string.Equals(request.Action, "screen.info", StringComparison.Ordinal))
        {
            try
            {
                return new SessionResponse(
                    Type: "response",
                    RequestId: request.RequestId,
                    Success: true,
                    ScreenInfo: GetScreenInfo(),
                    Error: null);
            }
            catch (Exception ex)
            {
                return new SessionResponse(
                    Type: "response",
                    RequestId: request.RequestId,
                    Success: false,
                    ScreenInfo: null,
                    Error: $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (string.Equals(request.Action, "windows.list", StringComparison.Ordinal))
        {
            try
            {
                var (titleFilter, visibleOnly) = ReadWindowListArguments(request.Arguments);
                var windowList = GetWindowList(titleFilter, visibleOnly);

                return new SessionResponse(
                    Type: "response",
                    RequestId: request.RequestId,
                    Success: true,
                    ScreenInfo: null,
                    Error: null,
                    WindowList: windowList);
            }
            catch (Exception ex)
            {
                return new SessionResponse(
                    Type: "response",
                    RequestId: request.RequestId,
                    Success: false,
                    ScreenInfo: null,
                    Error: $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (string.Equals(request.Action, "availability.set", StringComparison.Ordinal))
        {
            try
            {
                var keepAwake = ReadKeepAwakeArgument(request.Arguments);
                SetKeepAwake(keepAwake);

                return new SessionResponse(
                    Type: "response",
                    RequestId: request.RequestId,
                    Success: true,
                    ScreenInfo: null,
                    Error: null,
                    Availability: new AvailabilityInfoSnapshot(keepAwake));
            }
            catch (Exception ex)
            {
                return new SessionResponse(
                    Type: "response",
                    RequestId: request.RequestId,
                    Success: false,
                    ScreenInfo: null,
                    Error: $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        return new SessionResponse(
            Type: "response",
            RequestId: request.RequestId,
            Success: false,
            ScreenInfo: null,
            Error: $"Unsupported action: {request.Action}");
    }

    private static (string? TitleFilter, bool VisibleOnly) ReadWindowListArguments(
        JsonElement? arguments)
    {
        if (arguments is null || arguments.Value.ValueKind == JsonValueKind.Undefined)
        {
            return (null, true);
        }

        if (arguments.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "windows.list arguments must be a JSON object.");
        }

        string? titleFilter = null;
        var visibleOnly = true;

        foreach (var property in arguments.Value.EnumerateObject())
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
                        if (titleFilter is not null
                            && titleFilter.Length > MaxTitleFilterLength)
                        {
                            throw new InvalidDataException(
                                $"windows.list titleFilter exceeds {MaxTitleFilterLength} characters.");
                        }
                    }
                    else
                    {
                        throw new InvalidDataException(
                            "windows.list titleFilter must be a string or null.");
                    }
                    break;

                case "visibleOnly":
                    if (property.Value.ValueKind is not
                        (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw new InvalidDataException(
                            "windows.list visibleOnly must be boolean.");
                    }

                    visibleOnly = property.Value.GetBoolean();
                    break;

                default:
                    throw new InvalidDataException(
                        $"windows.list does not accept property '{property.Name}'.");
            }
        }

        return (titleFilter, visibleOnly);
    }

    private static WindowListSnapshot GetWindowList(
        string? titleFilter,
        bool visibleOnly)
    {
        var rows = new List<WindowSnapshot>();
        var filter = string.IsNullOrEmpty(titleFilter) ? null : titleFilter;
        var hitLimit = false;

        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            try
            {
                var visible = NativeMethods.IsWindowVisible(hwnd);
                if (visibleOnly && !visible)
                {
                    return true;
                }

                var title = GetWindowText(hwnd);
                if (visibleOnly && string.IsNullOrEmpty(title))
                {
                    return true;
                }

                if (filter is not null
                    && title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return true;
                }

                if (rows.Count >= MaxWindowRows)
                {
                    hitLimit = true;
                    return false;
                }

                if (!NativeMethods.GetWindowRect(hwnd, out var rect))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);

                rows.Add(new WindowSnapshot(
                    Hwnd: hwnd.ToInt64(),
                    Title: title,
                    ClassName: GetWindowClassName(hwnd),
                    Pid: checked((int)pid),
                    Visible: visible,
                    Minimized: NativeMethods.IsIconic(hwnd),
                    Maximized: NativeMethods.IsZoomed(hwnd),
                    Rect: new WindowRectSnapshot(
                        Left: rect.Left,
                        Top: rect.Top,
                        Right: rect.Right,
                        Bottom: rect.Bottom,
                        Width: rect.Right - rect.Left,
                        Height: rect.Bottom - rect.Top)));
            }
            catch
            {
                // A window can disappear during enumeration. Skip malformed or
                // transient records rather than taking down the Session Agent.
            }

            return true;
        };

        var completed = NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        if (!completed && !hitLimit)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "EnumWindows failed.");
        }

        var ordered = rows
            .OrderBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(window => window.Hwnd)
            .ToArray();

        return new WindowListSnapshot(ordered, ordered.Length);
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        _ = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static bool ReadKeepAwakeArgument(JsonElement? arguments)
    {
        if (arguments is null
            || arguments.Value.ValueKind != JsonValueKind.Object
            || !arguments.Value.TryGetProperty("keepAwake", out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                "availability.set requires boolean keepAwake.");
        }

        return property.GetBoolean();
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

    private static void SetKeepAwake(bool keepAwake)
    {
        var state = ExecutionState.Continuous;

        if (keepAwake)
        {
            state |= ExecutionState.SystemRequired;
        }

        var previous = SetThreadExecutionState(state);
        if (previous == 0)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "SetThreadExecutionState failed.");
        }
    }

    private static void TrySetKeepAwake(bool keepAwake)
    {
        try
        {
            SetKeepAwake(keepAwake);
        }
        catch
        {
            // Process shutdown cleanup is best effort. Windows clears the
            // execution-state request automatically when the process exits.
        }
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(
        ExecutionState esFlags);

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(
            EnumWindowsProc callback,
            IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLengthW(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextW(
            IntPtr hwnd,
            StringBuilder text,
            int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassNameW(
            IntPtr hwnd,
            StringBuilder className,
            int maxCount);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(
            IntPtr hwnd,
            out Rect rect);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            IntPtr hwnd,
            out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsZoomed(IntPtr hwnd);
    }
}
