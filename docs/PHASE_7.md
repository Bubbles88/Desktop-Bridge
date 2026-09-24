# Phase 7 Acceptance Contract

Phase 7 establishes the machine availability channel.

## Fundamental question

Can local owner policy keep the Windows machine operational for ErGe without requiring the Control Center to remain open and without giving provider actions authority over Windows power behavior?

## Implemented

1. Current user Session Agent autostart.
2. Policy driven system keep awake through SetThreadExecutionState.
3. Authenticated Session Agent reconnection after Core restart.
4. Owner policy transitions over the dedicated Owner Control pipe.

## Architecture

PolicyEngine -> SessionAgentPipeWorker -> internal availability.set -> Session Agent -> Windows SetThreadExecutionState.

The availability.set command is internal system control. It is not registered in the Capability Broker and cannot be requested through the Local Provider Protocol.

## Policy mapping

Always On => keep awake true.
Always Off => keep awake false.
Connect Now => keep awake true while the temporary override is active.
Local Only => keep awake false.
Emergency Block => keep awake false.

## Jonathan G14 live verification

Target verification completed on 24 September 2026.

The canonical Session Agent was installed under LocalAppData and registered as `ErGeSessionAgent` in the current user Run key.

The installed ErGeCore service was upgraded in place to the canonical build while preserving the service identity, ProgramData state, automatic startup and Local Service account.

After the Core restart, the existing Session Agent automatically reconnected and authenticated in Windows session 1 without restarting the Session Agent.

Live owner policy acceptance results:

1. AlwaysOn -> remote allowed -> KeepAwakeApplied=true.
2. AlwaysOff -> remote denied -> KeepAwakeApplied=false.
3. ConnectNow -> remote allowed -> KeepAwakeApplied=true.
4. EmergencyBlock -> remote denied -> KeepAwakeApplied=false.
5. Clear Override -> persistent AlwaysOff -> KeepAwakeApplied=false.

The Session Agent reported the real primary desktop at 2880 x 1800.

The standalone E2E harness was corrected so temporary paths work both inside GitHub Actions and on ordinary Windows machines.

## Verification boundary

Phase 7 source, CI, installed Core, installed Session Agent, owner controls, authentication, reconnection and live keep awake behavior are verified.

A full Windows reboot remains a later persistence gate for the complete Desktop Bridge stack.

## Next phase

Phase 8 hardens Windows availability around this proven channel: screen saver, AC sleep and hibernate, AC display timeout, AC lid behavior, ASUS OLED conflicts, state backup and restoration, and drift correction.
