# Phase 7 Acceptance Contract

Phase 7 begins the machine availability subsystem.

## Fundamental question

Can local owner policy keep the Windows machine operational for ErGe without requiring the Control Center to remain open and without giving provider actions authority over Windows power behavior?

## Scope

Phase 7 implements two foundations only:

1. Current-user Session Agent autostart.
2. Policy-driven system keep-awake through SetThreadExecutionState.

It does not yet rewrite screen saver, lid-close, hibernate, ASUS Armoury Crate, or global power-plan configuration.

## Architecture

PolicyEngine -> SessionAgentPipeWorker -> internal availability.set -> Session Agent -> Windows SetThreadExecutionState.

The availability.set command is internal system control. It is not registered in the Capability Broker and cannot be requested through the Local Provider Protocol.

## Policy mapping

Remote AI allowed means keep awake requested.

Always On => keep awake true.

Always Off => keep awake false.

Connect Now => keep awake true while the temporary override is active.

Local Only => keep awake false.

Emergency Block => keep awake false.

## Autostart

The Session Agent installer publishes under LocalAppData\ErGe\SessionAgent and registers ErGeSessionAgent under the current user's Windows Run key.

The Core service remains independent of the Control Center. Closing the Control Center does not remove Core or Session Agent availability.

## CI gate

Phase 7 verifies:

1. Session Agent availability probe can set and clear the Windows execution state.
2. Session Agent autostart can be installed, verified, and removed.
3. Always On produces KeepAwakeApplied=true.
4. Always Off produces KeepAwakeApplied=false.
5. Connect Now temporarily produces KeepAwakeApplied=true.
6. Emergency Block produces KeepAwakeApplied=false.
7. Clear Override returns to the persistent Always Off keep-awake state.

Expected marker: ERGE_PHASE7_AVAILABILITY_E2E_OK.

## Target-machine exit gate

On Jonathan G14, Phase 7 must prove the installed Session Agent starts after logon without opening the Control Center, keep-awake survives normal idle periods while Always On is effective, Always Off clears the execution-state request, and Core continues running independently.

## Next phase

Phase 8 will harden Windows availability around this proven channel: screen saver, sleep/hibernate, lid behavior, ASUS/OLED conflicts, state backup/restoration, and drift correction.
