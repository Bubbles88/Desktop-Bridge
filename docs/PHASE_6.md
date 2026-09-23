# Phase 6 Acceptance Contract

Phase 6 adds the native Windows Owner Control Center.

## Fundamental question

Can the hardware owner control persistent and temporary ErGe policy through a dedicated authenticated authority path while provider actions remain unable to mutate policy?

## Authority separation

Provider path: provider -> Capability Broker -> capability.

Owner path: Control Center -> Owner Control pipe -> PolicyEngine.

These paths are intentionally separate.

## Owner commands

1. get.status
2. set.persistent-mode AlwaysOn
3. set.persistent-mode AlwaysOff
4. set.session-override ConnectNow
5. set.session-override LocalOnly
6. set.session-override EmergencyBlock
7. clear.session-override

## Control Center UI

The WPF Control Center presents:

1. Always On / Off persistent toggle.
2. Core connection status.
3. Session Agent connected state.
4. Remote/local AI access status.
5. Connect Now.
6. Local Only.
7. Emergency Block.
8. Clear Override.
9. Periodic status refresh.

The UI never edits policy files directly.

## CI gate

Phase 6 builds the WPF app and then runs Core plus the Owner Control CLI against temporary machine-local paths.

The gate proves:

1. First run is Always Off.
2. Always On persists to policy storage.
3. Local Only is temporary.
4. Clear Override restores no temporary override.
5. Always Off persists again.
6. Every command travels through authenticated Owner Control IPC.

Expected marker: ERGE_PHASE6_OWNER_CONTROL_E2E_OK.

## Target-machine exit gate

On Jonathan G14, Phase 6 must additionally prove the installed ErGeCore service accepts the Control Center, the UI can change policy without elevation, Core survives when the UI closes, and no provider pipe can invoke owner policy commands.

## Next phase

Phase 7 will implement the startup and availability controller behind the Always On policy: Session Agent startup, keep-awake policy ownership, recovery state, and machine availability without requiring the Control Center to remain open.
