# Desktop Bridge

Native Windows remote AI runtime with owner controlled Always On and Always Off access.

## Current phase

Phase 9: remote provider boundary and legacy ErGe bridge migration.

Phases 1 through 7 are implemented. Phase 7 is target verified on Jonathan G14 with the installed Core service and authenticated Session Agent.

Implemented:

* Provider neutral Action Protocol v1
* Capability Broker with local policy authorization
* Registered `screen.info` capability
* Bounded Core to Session Agent action queue
* Authenticated Session Agent IPC as the interactive desktop boundary
* Persistent `AlwaysOn` and `AlwaysOff` policy
* Temporary `ConnectNow`, `LocalOnly` and `EmergencyBlock` overrides
* Local owner only policy mutation
* Native WPF Owner Control Center
* Current user Session Agent autostart
* Policy driven keep awake
* Phase 8 privileged availability guard with baseline restoration and drift correction
* Dedicated Remote Provider named pipe with Core assigned remote origin
* Generic Remote Provider CLI migration adapter
* Fail closed behavior for invalid configuration
* Atomic policy and state persistence

The existing ErGe PC Bridge remains the working production and construction path. Desktop Bridge is the replacement architecture and must not destabilize the existing bridge.

## Phase 7 live target result

On Jonathan G14, the canonical Session Agent is installed and authenticated to the installed ErGeCore service.

Verified owner policy behavior:

```text
AlwaysOn       -> KeepAwakeApplied=true
AlwaysOff      -> KeepAwakeApplied=false
ConnectNow     -> KeepAwakeApplied=true
EmergencyBlock -> KeepAwakeApplied=false
ClearOverride  -> KeepAwakeApplied=false
```

See `docs/PHASE_7.md`.

## Phase 8

Phase 8 adds a narrow privileged Windows availability guard.

It changes only AC sleep, AC hibernate, AC display timeout, AC lid action, and the Windows user screen saver when owner policy allows remote access.

It does not alter battery settings or any ASUS OLED Care setting. The ASUS screen saver, Pixel Shift, and Pixel Refresh remain OEM managed panel protection.

Before changing a setting it captures a restorable baseline. See `docs/PHASE_8.md`.

## Self tests

Phase 1:

```powershell
dotnet run --project src/ErGe.Policy.SelfTest/ErGe.Policy.SelfTest.csproj
```

Phase 4:

```powershell
dotnet run --project src/ErGe.ActionBroker.SelfTest/ErGe.ActionBroker.SelfTest.csproj --configuration Release
```

Phase 8:

```powershell
./scripts/phase8-availability-hardening-e2e.ps1
```

## Phase 9

Phase 9 adds a dedicated remote provider boundary so the existing ErGe PC Bridge can migrate behind Core policy without being misclassified as local AI.

Remote ChatGPT traffic must use the Remote Provider pipe. LocalOnly therefore denies remote traffic while still allowing the Local Provider path.

The first migrated capability is `screen.info`. See `docs/PHASE_9.md`.
