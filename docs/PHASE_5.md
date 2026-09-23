# Phase 5 Acceptance Contract

Phase 5 adds the first real provider ingress into ErGe Core.

## Fundamental question

Can a locally authenticated owner process submit a provider-neutral action to Core, have Core assign the trust origin itself, enforce policy through the Capability Broker, cross the authenticated Session Agent boundary, and receive a correlated result without opening a network port?

## Scope

The provider pipe supports only the already registered screen.info capability.

No remote relay, public ChatGPT app, legacy bridge adapter, mouse, keyboard, shell, clipboard, or filesystem mutation is added here.

## Architecture

ErGe Local Provider CLI -> owner authenticated Windows named pipe -> LocalProviderPipeWorker -> Core assigns ActionOrigin.LocalProvider -> Capability Broker -> ScreenInfoCapabilityHandler -> SessionAgentActionQueue -> authenticated Session Agent IPC -> Windows interactive desktop.

## Trust rule

The local provider client never supplies ActionOrigin.

Core derives the origin from the authenticated transport and creates ActionOrigin.LocalProvider itself.

This is a permanent design rule for provider adapters: transport identity determines trust class. Payloads do not self declare authority.

## Security invariants

1. Provider ingress is Windows named pipe IPC only.
2. Windows Network identity is denied.
3. Real provider PID is read from the pipe by Windows.
4. Real provider session ID is read from the pipe by Windows.
5. Provider must be in the active console session.
6. Provider SID must match the configured device owner SID.
7. Claimed PID and session must match operating system values.
8. ClientName is descriptive only.
9. Provider payload has no origin or policy authority.
10. Core assigns LocalProvider.
11. Every action still passes through local owner policy.
12. Every action still requires exact registered capability lookup.

## CI gate

Workflow: phase-5-local-provider

The workflow:

1. Builds Core.
2. Builds Core Service.
3. Builds Session Agent.
4. Builds Local Provider CLI.
5. Runs the Action Broker self test.
6. Creates temporary Always On policy and owner SID files.
7. Starts Core as a real process.
8. Starts the real Session Agent.
9. Waits for authenticated Session Agent status.
10. Executes Local Provider CLI --probe-screen.
11. Requires ERGE_LOCAL_PROVIDER_SCREEN_INFO_OK.
12. Requires ERGE_PHASE5_E2E_OK.

## Target machine gate

Phase 5 is not production verified until Jonathan G14 proves the same path against the installed ErGeCore Windows service and Jonathan interactive Session Agent.

The G14 proof must also verify Always Off denial, Local Only allowance, Emergency Block denial, Session Agent loss behavior, Session Agent recovery, and absence of a new listening network port.

## Next phase

After Phase 5 passes, the legacy ErGe bridge can become the first compatibility adapter into the Local Provider Protocol. It must not bypass the Action Protocol, Capability Broker, or local owner policy.
