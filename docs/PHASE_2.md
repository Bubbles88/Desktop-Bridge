# Phase 2 Acceptance Contract

Phase 2 proves that ErGe Core can exist as a persistent Windows runtime independently of ChatGPT, the Control Center, and the existing bridge.

## Build scope

1. A minimal Windows Service host named **ErGe Core**.
2. Phase 1 policy is loaded by the service.
3. The service writes an atomic runtime heartbeat.
4. Always Off remains a healthy dormant state.
5. Always On reaches **LocalReady** before any remote transport or Session Agent exists.
6. Temporary overrides remain process-scoped.
7. Runtime status and persistent policy are separate concepts.
8. The existing Yusen / ErGe PC bridge remains untouched.

## Runtime status

Default path:

`%ProgramData%\ErGe\runtime-status.json`

Test overrides:

`ERGE_POLICY_PATH`

`ERGE_STATUS_PATH`

## Verification

Build:

```powershell
dotnet build src/ErGe.Core.Service/ErGe.Core.Service.csproj --configuration Release
```

Policy self-test:

```powershell
dotnet run --project src/ErGe.Policy.SelfTest/ErGe.Policy.SelfTest.csproj --configuration Release
```

Core service self-test:

```powershell
dotnet run --project src/ErGe.Core.Service.SelfTest/ErGe.Core.Service.SelfTest.csproj --configuration Release
```

One-shot host probe using temporary paths:

```powershell
$env:ERGE_POLICY_PATH = Join-Path $env:TEMP 'erge-policy.json'
$env:ERGE_STATUS_PATH = Join-Path $env:TEMP 'erge-runtime-status.json'
dotnet run --project src/ErGe.Core.Service/ErGe.Core.Service.csproj --configuration Release -- --probe-once
```

Expected markers:

`ERGE_POLICY_SELFTEST_OK`

`ERGE_CORE_SERVICE_SELFTEST_OK`

`ERGE_CORE_PROBE_OK`

## Local G14 service acceptance

On Jonathan G14 only after build tests pass:

1. Publish ErGe Core.
2. Install as Windows service `ErGeCore`.
3. Configure Service Control Manager recovery.
4. Start the service.
5. Verify a fresh heartbeat is written under ProgramData.
6. Close ChatGPT Desktop.
7. Confirm the Core remains running.
8. Restart the Core service.
9. Confirm persistent policy survives while temporary session overrides are cleared.
10. Confirm the existing ErGe PC bridge is still healthy.

Phase 2 is complete only when both CI and the actual G14 service pass.
