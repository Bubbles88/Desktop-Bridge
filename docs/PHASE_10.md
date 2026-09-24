# Phase 10 Acceptance Contract

Phase 10 expands the Desktop Bridge capability boundary from monitor geometry to read only top level window observation.

## Fundamental question

Can a remote or local provider discover the real interactive desktop window state through ErGe Core without bypassing owner policy, without granting any window mutation authority, and without changing the existing ErGe PC Bridge tool contract?

## Capability

```text
windows.list
```

Arguments:

```json
{
  "titleFilter": "optional substring",
  "visibleOnly": true
}
```

Defaults:

```text
titleFilter = null
visibleOnly = true
```

Unknown properties and invalid argument types fail with `arguments_invalid`.

## Returned data

Core returns bounded top level window metadata from the authenticated interactive Session Agent:

```text
hwnd
title
className
pid
visible
minimized
maximized
rect.left
rect.top
rect.right
rect.bottom
rect.width
rect.height
```

The Session Agent performs the Win32 enumeration because a background Windows service is not the authoritative interactive desktop.

## Authority boundary

```text
Provider
  -> LocalProvider or RemoteProvider pipe
  -> Core assigned origin
  -> owner policy
  -> Capability Broker
  -> windows.list handler
  -> authenticated Session Agent
  -> Win32 read only enumeration
```

The provider cannot choose its origin.

Phase 10 adds no activate, close, mouse, keyboard, clipboard, file, process, shell, or screenshot mutation authority.

## Policy matrix

AlwaysOn and ConnectNow permit both provider origins.

LocalOnly denies RemoteProvider and permits LocalProvider.

EmergencyBlock denies both.

AlwaysOff denies both.

## Bounds

Window enumeration is limited to 512 returned rows per request.

Title filter length is limited to 512 characters.

Window titles are limited by the Win32 title length exposed by the operating system.

Malformed window records are skipped rather than taking down the Session Agent.

## Compatibility migration

The production ErGe PC Bridge keeps the existing `list_windows(title_filter, visible_only)` tool contract.

Migration follows the same per capability states used by `screen.info`:

```text
off
shadow
enforce
```

In shadow mode, direct and Core window inventories are compared using stable geometry and identity fields without writing window titles to telemetry.

In enforce mode, `list_windows` returns only the Core brokered result. Core failure or owner policy denial is terminal. There is no direct fallback.

## CI gate

Phase 10 must prove:

1. Core registers `windows.list` explicitly.
2. Invalid arguments fail before Session Agent execution.
3. Session Agent returns top level window metadata.
4. RemoteProvider succeeds under AlwaysOn.
5. RemoteProvider fails under LocalOnly.
6. LocalProvider succeeds under LocalOnly.
7. Both origins fail under EmergencyBlock.
8. Both origins fail under AlwaysOff.
9. Existing `screen.info` behavior remains green.

Expected marker:

```text
ERGE_PHASE10_WINDOWS_LIST_E2E_OK
```

## Jonathan G14 target gate

Phase 10 is target verified only after:

1. Core and Session Agent upgrade cleanly.
2. Existing Phase 8 availability state remains healthy.
3. Direct and Core `list_windows` shadow inventories agree on the real G14 desktop.
4. The same ChatGPT bridge session remains reachable.
5. LocalOnly denies remote `list_windows` while local provider still succeeds.
6. EmergencyBlock and AlwaysOff deny remote `list_windows`.
7. AlwaysOn permits remote `list_windows`.
8. Only then may `list_windows` move to enforce mode.

## Next phase

After read only window observation is verified, the next capability group should continue observation before mutation. The preferred next candidates are foreground window state and bounded screenshot transport. Mutating window focus, mouse, keyboard, close, clipboard write, files, processes, and shell remain later gates.
