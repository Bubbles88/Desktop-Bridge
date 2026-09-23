# Desktop Bridge

Native Windows remote AI runtime with owner-controlled Always On / Always Off access.

## Current phase

Phase 5 source development: owner-authenticated local provider ingress on top of the Phase 4 Action Protocol and Capability Broker.

Phase 1 and Phase 2 are complete. Phase 3 source and CI are complete, while its final Jonathan G14 target-machine verification record remains open.

Implemented:

- Provider-neutral Action Protocol v1
- Capability Broker with local policy authorization
- Registered `screen.info` capability only
- Bounded Core-to-Session-Agent action queue
- Authenticated Session Agent IPC remains the only interactive desktop boundary
- Persistent `AlwaysOn` / `AlwaysOff` policy
- Temporary `ConnectNow`, `LocalOnly`, and `EmergencyBlock` overrides
- Local-owner-only policy mutation
- Fail-closed behavior for invalid configuration
- Atomic policy persistence
- Dependency-free self-test

The existing Yusen / ErGe PC bridge remains the working production and migration path. This repository is the replacement architecture and must not destabilize the existing bridge.

## Phase 1 self-test

```powershell
dotnet run --project src/ErGe.Policy.SelfTest/ErGe.Policy.SelfTest.csproj
```

Expected final line:

```text
ERGE_POLICY_SELFTEST_OK
```


## Phase 4 self-test

```powershell
dotnet run --project src/ErGe.ActionBroker.SelfTest/ErGe.ActionBroker.SelfTest.csproj --configuration Release
```

Expected final line:

```text
ERGE_ACTION_BROKER_SELFTEST_OK
```

Phase 4 intentionally adds no Relay and no external ChatGPT provider ingress yet.

## Phase 5 provider probe

The first provider client is local-only and owner-authenticated:

dotnet run --project src/ErGe.LocalProvider.Cli/ErGe.LocalProvider.Cli.csproj --configuration Release -- --probe-screen

A successful end-to-end provider path prints ERGE_LOCAL_PROVIDER_SCREEN_INFO_OK.
