# Phase 2 Verification Record

Date: 2026-09-23
Machine: JONATHAN-G14
Repository: Bubbles88/Desktop-Bridge

## Verified source

Installed and re-verified from commit:

`a095ceb81dfe250615be99802ad9410b2bdd9914`

## CI verification

Both repository workflows completed successfully on the Phase 2 code path:

- `phase-1-policy`
- `phase-2-core`

The Phase 2 workflow builds the Windows Core Service, runs the Phase 1 policy self-test, runs the Core service self-test, and runs the one-shot Core probe.

## Local build verification

Jonathan G14 built the Core Service using .NET SDK 10.0.401.

Result:

- Build succeeded
- 0 warnings
- 0 errors
- `ERGE_POLICY_SELFTEST_OK`
- `ERGE_CORE_SERVICE_SELFTEST_OK`
- `ERGE_CORE_PROBE_OK`

## Installed Windows Service

Service name:

`ErGeCore`

Display name:

`ErGe Core`

Verified state:

- State: Running
- Start mode: Automatic
- Account: `NT AUTHORITY\LOCAL SERVICE`
- Parent process: `services.exe`
- Runtime state: `AlwaysOff`
- Persistent mode: `AlwaysOff`
- Effective access: `AlwaysOff`
- Configuration healthy: true
- Remote AI allowed: false

Runtime heartbeat:

`C:\ProgramData\ErGe\runtime-status.json`

## Service recovery verification

SCM recovery policy:

1. Restart after 5000 ms
2. Restart after 15000 ms
3. Restart after 60000 ms
4. Reset failure count after 86400 seconds
5. Failure actions on non-crash failures enabled

Fault injection:

The active ErGeCore process was forcibly terminated.

Before:

`PID 13856`

After SCM recovery:

`PID 28880`

The service returned to Running, produced a new instance ID, produced a fresh heartbeat, and retained the authoritative `AlwaysOff` policy.

This proves recovery is owned by Windows Service Control Manager rather than a custom supervisor.

## Repeatable installer verification

The committed installer:

`scripts/install-core-service.ps1`

was executed from the exact GitHub source archive for commit `a095ceb81dfe250615be99802ad9410b2bdd9914`.

It successfully republished and reinstalled the service.

After reinstall:

- State: Running
- Start mode: Automatic
- Account: `NT AUTHORITY\LOCAL SERVICE`
- Runtime state: `AlwaysOff`
- Configuration healthy: true
- Fresh heartbeat confirmed

The committed verifier:

`scripts/verify-core-service.ps1`

returned:

`ERGE_CORE_VERIFY_OK`

## Existing bridge isolation

The current ErGe PC / Yusen bridge was not modified by Phase 2.

ErGeCore is parented by Windows `services.exe`, not ChatGPT Desktop, the ErGe Control Center, or the legacy bridge.

The existing bridge remained independently available during Core development and testing.

## Phase 2 exit gate

Phase 2 is complete.

The following have been proven:

1. Local policy loads independently from ChatGPT.
2. A persistent Windows Core runtime exists.
3. The Core survives independently of visible applications.
4. The Core publishes an atomic health heartbeat.
5. Always Off is a healthy dormant state.
6. Core restart preserves persistent policy.
7. Temporary policy is not persisted.
8. Windows SCM automatically recovers a crashed Core.
9. Installation is repeatable from repository source.
10. The legacy bridge remains isolated.

## Next phase

Phase 3 is the Session Agent boundary.

Its first proof is deliberately narrow:

`ErGeCore -> authenticated local IPC -> ErGe Session Agent -> screen.info -> result`

No relay, public ChatGPT app, TimesFM runtime, or broad GUI automation belongs in that proof.
