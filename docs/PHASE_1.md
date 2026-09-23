# Phase 1 Acceptance Contract

Phase 1 proves that the Windows computer owns its access policy independently of ChatGPT or any remote provider.

Required invariants:

1. Missing configuration defaults to Always Off.
2. Invalid configuration fails closed to Always Off.
3. Only the local owner may change persistent policy.
4. Remote providers cannot change persistent policy.
5. Connect Now is temporary and never persists across process restart.
6. Local Only blocks remote AI while preserving local AI.
7. Emergency Block blocks both local and remote AI.
8. Always On persists across process restart.
9. Always Off persists across process restart.
10. Policy persistence is atomic.

Verification command:

```powershell
dotnet run --project src/ErGe.Policy.SelfTest/ErGe.Policy.SelfTest.csproj --configuration Release
```

Success marker:

```text
ERGE_POLICY_SELFTEST_OK
```
