using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using ErGe.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace ErGe.Core.Service;

public sealed record AuthenticatedPipeClient(
    int ProcessId,
    int SessionId,
    string UserName,
    string UserSid);

public static class NamedPipeOwnerAuthenticator
{
    private const uint InvalidSessionId = 0xFFFFFFFF;

    public static NamedPipeServerStream CreateOwnerPipe(
        string pipeName,
        int maxInstances = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        var security = new PipeSecurity();

        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var localServiceSid = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        var localSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        security.AddAccessRule(new PipeAccessRule(
            networkSid,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        security.AddAccessRule(new PipeAccessRule(
            localServiceSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            localSystemSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            administratorsSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            usersSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }

    public static AuthenticatedPipeClient AuthenticateOwner(
        NamedPipeServerStream pipe,
        SessionOwnerStore ownerStore,
        bool requireActiveConsoleSession = true)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(ownerStore);

        var processId = GetClientProcessId(pipe.SafePipeHandle);
        var sessionId = GetClientSessionId(pipe.SafePipeHandle);

        if (requireActiveConsoleSession)
        {
            var activeSessionId = WTSGetActiveConsoleSessionId();

            if (activeSessionId == InvalidSessionId)
            {
                throw new SecurityException("No active console session exists.");
            }

            if (sessionId != activeSessionId)
            {
                throw new SecurityException(
                    $"Pipe client is in session {sessionId}; active console session is {activeSessionId}.");
            }
        }

        var authenticatedUser = pipe.GetImpersonationUserName();
        if (string.IsNullOrWhiteSpace(authenticatedUser))
        {
            throw new SecurityException(
                "Windows did not provide an authenticated pipe client identity.");
        }

        var authenticatedSid = ((NTAccount)new NTAccount(authenticatedUser))
            .Translate(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new SecurityException(
                "Unable to resolve authenticated pipe client SID.");

        var owner = ownerStore.LoadRequired();

        if (!string.Equals(
                authenticatedSid.Value,
                owner.UserSid,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                $"Pipe client SID {authenticatedSid.Value} is not the configured device owner SID.");
        }

        return new AuthenticatedPipeClient(
            processId,
            sessionId,
            authenticatedUser,
            authenticatedSid.Value);
    }

    private static int GetClientProcessId(SafePipeHandle handle)
    {
        if (!GetNamedPipeClientProcessId(handle, out var processId))
        {
            throw new InvalidOperationException(
                $"GetNamedPipeClientProcessId failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return checked((int)processId);
    }

    private static int GetClientSessionId(SafePipeHandle handle)
    {
        if (!GetNamedPipeClientSessionId(handle, out var sessionId))
        {
            throw new InvalidOperationException(
                $"GetNamedPipeClientSessionId failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return checked((int)sessionId);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle Pipe,
        out uint ClientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(
        SafePipeHandle Pipe,
        out uint ClientSessionId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
