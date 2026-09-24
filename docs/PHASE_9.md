# Phase 9 Acceptance Contract

Phase 9 introduces the remote provider boundary used to migrate the existing ErGe PC Bridge behind Desktop Bridge Core policy.

## Fundamental question

Can a remote ChatGPT request enter ErGe Core as a Core assigned RemoteProvider action, remain distinguishable from local AI, and reach the interactive Session Agent only when local owner policy permits remote access?

## Required architecture

```text
ChatGPT
  -> OpenAI Secure MCP Tunnel
  -> existing ErGe PC Bridge
  -> compatibility adapter
  -> ErGe.RemoteProvider.Cli
  -> authenticated local named pipe
  -> ErGe Core
  -> Capability Broker
  -> Session Agent
  -> Windows
```

The existing tunnel remains the remote transport during migration.

## Origin integrity

The compatibility client cannot choose its trust origin.

The Remote Provider pipe is separate from the Local Provider pipe.

Core converts every valid request arriving through the remote pipe into:

```text
ActionOrigin.RemoteProvider
```

The Local Provider pipe continues to assign:

```text
ActionOrigin.LocalProvider
```

This distinction is mandatory. Reusing the Local Provider pipe for remote ChatGPT traffic would make LocalOnly unsafe.

## Policy matrix

AlwaysOn:

Remote Provider allowed.
Local Provider allowed.

ConnectNow:

Remote Provider allowed.
Local Provider allowed.

LocalOnly:

Remote Provider denied with policy_denied.
Local Provider allowed.

EmergencyBlock:

Remote Provider denied.
Local Provider denied.

AlwaysOff:

Remote Provider denied.
Local Provider denied.

## Authentication

The remote provider named pipe:

1. is local Windows IPC only;
2. denies the Windows Network identity;
3. requires the configured device owner SID;
4. requires the active console session;
5. verifies the real Windows client PID and session;
6. verifies protocol version;
7. never accepts a caller supplied action origin.

No shared secret is added.

## Phase 9 first capability

Only the already registered `screen.info` capability is migrated through the remote provider boundary in this phase.

Phase 9 does not yet register mouse, keyboard, screenshot image transfer, files, shell, processes, clipboard, or window mutation in Desktop Bridge Core.

Those capabilities remain on the legacy bridge until separately brokered and verified.

## Remote Provider CLI

The compatibility executable is:

```text
%LOCALAPPDATA%\ErGe\RemoteProvider\ErGe.RemoteProvider.Cli.exe
```

It supports a generic action request and a `--probe-screen` verification path.

The CLI is a migration adapter, not a policy authority.

## Concurrency

The Core remote provider accept loop allows multiple authenticated local adapter clients so concurrent MCP requests do not serialize behind one long lived provider connection.

Capability execution remains bounded by the registered Core handlers and Session Agent execution channel.

## CI gate

Phase 9 must prove:

1. Core Service builds with the Remote Provider worker.
2. Remote Provider CLI builds.
3. Remote Provider CLI can be installed and verified.
4. AlwaysOn permits remote screen.info.
5. LocalOnly denies remote screen.info.
6. LocalOnly still permits the Local Provider screen.info path.
7. EmergencyBlock denies remote screen.info.
8. Clearing LocalOnly back to persistent AlwaysOn permits remote screen.info again.
9. AlwaysOff denies remote screen.info.

Expected marker:

```text
ERGE_PHASE9_REMOTE_PROVIDER_E2E_OK
```

## Legacy ErGe Bridge migration gate

The ErGe PC Bridge repository must then add a compatibility adapter with three modes:

```text
off
shadow
enforce
```

Off preserves current production behavior.

Shadow performs the existing direct call and independently proves the Core remote path without changing the tool result.

Enforce returns the Core brokered result and must be enabled only for capabilities already registered and target verified in Desktop Bridge.

The default remains off until live target verification.

## Jonathan G14 target gate

Phase 9 is complete only when:

1. the current Core service is upgraded without losing Phase 8 state;
2. the Remote Provider CLI is installed for Jonathan;
3. the existing Session Agent reconnects after Core restart;
4. Remote Provider screen.info succeeds under AlwaysOn;
5. Remote Provider screen.info fails closed under LocalOnly, EmergencyBlock, and AlwaysOff;
6. Local Provider screen.info still succeeds under LocalOnly;
7. the current ErGe PC Bridge remains reachable through the same ChatGPT session;
8. the legacy bridge compatibility adapter passes its own repository CI;
9. the compatibility adapter is deployed without restarting tunnel-client;
10. shadow mode proves the same primary screen geometry as the existing direct bridge;
11. only then may screen.info move to enforce mode.

## Next phase

After the first remote capability is brokered end to end, the next phase expands Core capability coverage in risk ordered groups before retiring corresponding direct legacy execution paths.
