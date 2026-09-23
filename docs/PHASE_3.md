# Phase 3 Acceptance Contract

Phase 3 proves the Windows service boundary between ErGe Core and the interactive user desktop.

## Fundamental question

Can a persistent service request one interactive desktop capability through authenticated local IPC without placing desktop automation inside the service?

## Scope

Only one capability exists in this phase:

`screen.info`

No mouse, keyboard, clipboard, shell, file execution, remote Relay, ChatGPT provider, or general capability broker is introduced here.

## Architecture

```text
ErGe Core Service
    |
    | Windows named pipe
    | Windows authenticated client identity
    v
ErGe Session Agent
    |
    v
Windows interactive desktop
```

## Authentication rules

1. The named pipe denies the Windows Network identity.
2. Local built-in users may open the pipe only as a transport-level prerequisite.
3. Core resolves the authenticated Windows account to its real SID.
4. Core requires that SID to equal the locally configured device-owner SID in `%ProgramData%\ErGe\session-owner.json`.
5. Core obtains the real client PID from Windows.
6. Core resolves the real Windows session for that PID.
7. Core requires that session to equal the active console session.
8. Core obtains the authenticated pipe user from Windows.
9. The Session Agent hello PID, session, and user name must match the operating system values.
10. Protocol version must match.

No custom shared secret is introduced in Phase 3.

## Session Agent behavior

1. Runs in the interactive user session.
2. Connects outbound to the Core named pipe.
3. Authenticates through the Windows pipe identity.
4. Waits for Core requests.
5. Supports only `screen.info`.
6. Reconnects if Core restarts.
7. Does not listen on a network port.

## Core behavior

1. Remains a Windows Service running as Local Service.
2. Continues Phase 2 heartbeat behavior.
3. Hosts one local named pipe.
4. Accepts at most one Session Agent.
5. Requests `screen.info` every two seconds as the Phase 3 heartbeat.
6. Writes Session Agent state to:
   `%ProgramData%\ErGe\session-agent-status.json`

## Exit conditions

Phase 3 passes only when:

1. Core builds.
2. Session Agent builds.
3. Session Agent screen probe returns valid monitor geometry.
4. Core remains parented by `services.exe`.
5. Session Agent runs in Jonathan's interactive session.
6. Windows authenticates the Session Agent connection.
7. Core records the real Session Agent PID and Windows session ID.
8. Core receives a valid `screen.info` result.
9. Killing Session Agent causes Core to mark it disconnected.
10. Restarting Session Agent reconnects without restarting Core.
11. Restarting Core causes Session Agent to reconnect.
12. Existing ErGe PC bridge remains healthy.

The next phase may generalize this one proven path into the ErGe Action Protocol and Capability Broker.
