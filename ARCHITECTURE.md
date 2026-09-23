# ErGe Desktop Architecture

## Product invariant

The computer owns the policy. ErGe owns the control path. Providers request capabilities. No external AI provider owns the computer.

## Phase 1 scope

The first build phase proves local policy ownership only.

It implements:

1. Persistent owner mode: `AlwaysOff` or `AlwaysOn`.
2. Temporary session override: `None`, `ConnectNow`, `LocalOnly`, or `EmergencyBlock`.
3. Safe default: missing or invalid persistent configuration fails closed to `AlwaysOff`.
4. Only a local owner control source may change persistent policy.
5. Temporary session overrides are never written to persistent configuration.
6. A process restart clears temporary overrides.
7. Effective access state is computed deterministically.

## Policy precedence

Highest precedence first:

1. `EmergencyBlock`: reject all AI control.
2. `LocalOnly`: allow local AI control but reject remote AI control.
3. `ConnectNow`: temporarily allow remote AI control even when the persistent default is `AlwaysOff`.
4. Persistent `AlwaysOn`: allow remote AI control.
5. Persistent `AlwaysOff`: reject remote AI control.

A remote provider cannot set or clear any owner policy or session override.

## Why this repository is separate

The existing Yusen PC Bridge remains the working production and migration path.

ErGe Desktop is built independently so the replacement architecture can be tested without destabilizing the bridge.

The current bridge will later become a legacy provider adapter into ErGe Core only after the new local architecture passes its acceptance gates.
