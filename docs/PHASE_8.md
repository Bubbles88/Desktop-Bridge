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
8. ASUS machine level OLED screen saver timeout disabled while AlwaysOn.
9. ASUS machine level OLED screen saver image cleared while AlwaysOn.
10. Drift correction every 60 seconds while the owner is logged in.

Battery power settings are not changed.

ASUS Pixel Shift and Pixel Refresh are explicitly not changed.

## State backup and restoration

Before the first Phase 8 mutation, the installer records the active Windows power scheme, exact AC values for sleep, hibernate, display and lid action, Windows screen saver values, ASUS per user screen saver values, ASUS machine level screen saver values, and Pixel Shift and Pixel Refresh values as protected evidence. Existing schema 1 baselines migrate in place to schema 2 without recapturing already preserved user settings.

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
8. Windows, ASUS user, and ASUS machine screen saver drift is corrected.
9. AlwaysOff restores the captured ASUS machine screen saver baseline.
10. Pixel Shift remains enabled if it was enabled before installation.
11. Pixel Refresh remains enabled if it was enabled before installation.
12. Uninstall or AlwaysOff can restore captured baseline.
13. Existing ErGe PC Bridge remains reachable throughout.

## Next phase

After Phase 8 target verification, Phase 9 implements the legacy ErGe adapter boundary so the existing ChatGPT bridge can call ErGe Core through the provider neutral Action Protocol without bypassing Core policy.

## ASUS G14 observation

Live G14 testing showed that Armoury Crate can normalize or remove per user OLEDCare overrides after restoration even though machine level OLED protection remains enabled. Phase 8 therefore treats the machine level ASUS OLEDCare screen saver values as part of the reversible enforcement boundary. Pixel Shift and Pixel Refresh remain evidence only and are never modified by the guard.
