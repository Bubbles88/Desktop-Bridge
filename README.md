# Desktop Bridge

Native Windows remote AI runtime with owner-controlled Always On / Always Off access.

## Current phase

Phase 1: local policy ownership.

Implemented:

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
