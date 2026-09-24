# Phase 8 Acceptance Contract

Phase 8 hardens Windows availability around the Phase 7 owner policy channel.

## Fundamental question

Can ErGe maintain interactive Windows availability under owner authorized remote access, correct configuration drift, and return the machine to its pre Phase 8 settings without giving an AI provider privileged Windows power authority?

## Authority boundary

The provider still cannot write Windows power settings.

Owner policy -> ErGe Core runtime status -> privileged ErGeAvailabilityGuard -> bounded Windows availability settings.

The guard reads Core state. It does not accept provider commands.

## Scope

When effective remote access is allowed, Phase 8 enforces only:

1. AC sleep timeout = Never.
2. AC hibernate timeout = Never.
3. AC display timeout = Never.
4. AC lid close action = Do nothing.
5. Windows user screen saver disabled.
6. ASUS per user OLED screen saver timeout disabled.
7. ASUS per user OLED screen saver image cleared.
8. Drift correction every 60 seconds while the owner is logged in.

Battery power settings are not changed.

ASUS Pixel Shift and Pixel Refresh are explicitly not changed.

## State backup and restoration

Before the first Phase 8 mutation, the installer records the active Windows power scheme, exact AC values for sleep, hibernate, display and lid action, Windows screen saver values, ASUS per user screen saver values, and Pixel Shift and Pixel Refresh values as protected evidence.

If Armoury Crate or another component changes the active power scheme while ErGe is enforcing availability, the guard captures that scheme's original AC values before applying any change.

When owner policy no longer permits remote access, the guard restores captured baseline values. Always Off therefore disables ErGe enforcement rather than inventing a new power policy.

## Core outage behavior

If Core runtime status is fresh, effective owner policy controls availability. If runtime status is stale or missing, the guard falls back to the persisted owner mode.

Persistent AlwaysOn continues availability enforcement. Persistent AlwaysOff restores baseline. Missing or unreadable policy fails closed to baseline restoration.

Temporary ConnectNow, LocalOnly and EmergencyBlock are honored only while Core runtime status is fresh because temporary overrides are intentionally not persistent.

## Scheduled task

The installed task is `ErGeAvailabilityGuard`. It runs in the device owner's interactive Windows identity at highest run level.

It starts at owner logon and has a daily recovery trigger. The task hosts a 60 second correction loop and is configured for Task Scheduler restart on failure. No Windows account password is stored.

## CI gate

Phase 8 CI parses the guard and runs its deterministic policy mapping self test.

Expected markers: `ERGE_PHASE8_GUARD_SELFTEST_OK` and `ERGE_PHASE8_E2E_OK`.

## Jonathan G14 target gate

Phase 8 is target verified only after Jonathan G14 proves:

1. Phase 7 Session Agent remains connected.
2. Guard baseline is captured before Phase 8 mutation.
3. Guard task runs elevated under Jonathan's interactive identity.
4. AlwaysOn yields `EnforceAlwaysOn`.
5. AlwaysOff yields `RestoreBaseline`.
6. AC sleep, hibernate, display and lid settings match the enforced contract while AlwaysOn.
7. Battery settings remain unchanged.
8. Windows and ASUS user screen saver drift is corrected.
9. Pixel Shift remains enabled if it was enabled before installation.
10. Pixel Refresh remains enabled if it was enabled before installation.
11. Uninstall or AlwaysOff can restore captured baseline.
12. Existing ErGe PC Bridge remains reachable throughout.

## Next phase

After Phase 8 target verification, Phase 9 implements the legacy ErGe adapter boundary so the existing ChatGPT bridge can call ErGe Core through the provider neutral Action Protocol without bypassing Core policy.
