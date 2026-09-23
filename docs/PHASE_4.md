# Phase 4 Acceptance Contract

Phase 4 introduces the provider-neutral ErGe Action Protocol and Capability Broker without adding any external network provider.

## Fundamental question

Can ErGe Core receive a provider-neutral action request, enforce local owner policy, resolve exactly one registered capability, and route that capability through the authenticated Session Agent boundary without giving the provider direct access to Windows?

## Scope

Only one capability is registered:

`screen.info`

No mouse, keyboard, clipboard, shell, filesystem mutation, relay, public ChatGPT app, or general remote provider ingress belongs in this phase.

## Architecture

```text
Provider-neutral ActionRequest
        |
        v
ErGe Capability Broker
        |
        +-- protocol validation
        +-- local policy authorization
        +-- exact capability lookup
        +-- argument contract validation
        |
        v
ScreenInfoCapabilityHandler
        |
        v
IInteractiveCapabilityExecutor
        |
        v
SessionAgentActionQueue
        |
        v
authenticated Phase 3 named pipe
        |
        v
ErGe Session Agent
        |
        v
Windows screen geometry
```

## Protocol invariants

1. Protocol version is explicit.
2. Every request has a request ID.
3. Every request declares LocalProvider or RemoteProvider origin.
4. Capability names are exact and case-sensitive.
5. Unregistered capabilities fail before Session Agent dispatch.
6. Capability results must preserve the request ID.
7. Capability exceptions become structured failures.
8. Provider protocol does not expose policy mutation methods.

## Policy invariants

1. Unhealthy policy configuration fails closed.
2. Always Off blocks local and remote provider actions.
3. Always On permits registered local and remote provider actions.
4. Local Only permits local provider actions and blocks remote provider actions.
5. Emergency Block blocks all provider actions.
6. Remote providers still cannot change persistent mode or session override.

## Interactive execution invariants

1. Broker does not know about named pipes.
2. Session Agent remains the only process touching the interactive desktop.
3. Core owns a bounded interactive action queue.
4. Requests fail fast when no authenticated Session Agent is connected.
5. The queue has one reader because one Session Agent connection owns the IPC stream.
6. Session pipe request/response correlation remains mandatory.
7. `screen.info` remains the only action the Session Agent accepts in Phase 4.
8. Existing periodic Session Agent health probing remains active.

## CI verification

Build:

```powershell
dotnet build src/ErGe.Core/ErGe.Core.csproj --configuration Release
dotnet build src/ErGe.Core.Service/ErGe.Core.Service.csproj --configuration Release
dotnet build src/ErGe.SessionAgent/ErGe.SessionAgent.csproj --configuration Release
```

Run:

```powershell
dotnet run --project src/ErGe.ActionBroker.SelfTest/ErGe.ActionBroker.SelfTest.csproj --configuration Release
```

Expected marker:

```text
ERGE_ACTION_BROKER_SELFTEST_OK
```

## Target-machine exit gate

Phase 4 source may be developed while Phase 3 target verification remains open, but Phase 4 is not production verified until Jonathan G14 proves:

1. Phase 3 authenticated Session Agent is connected.
2. Always Off causes broker-originated remote screen.info to fail before IPC dispatch.
3. Always On allows broker-originated remote screen.info.
4. Returned monitor geometry matches the interactive desktop.
5. Killing Session Agent causes new interactive actions to fail fast.
6. Restarting Session Agent restores action execution without restarting Core.
7. Core remains parented by services.exe.
8. Session Agent remains in Jonathan's active interactive session.
9. Existing ErGe PC bridge remains independently healthy.

## Next phase

Only after this gate should the product add a real local provider ingress or legacy bridge adapter into the Action Protocol.
