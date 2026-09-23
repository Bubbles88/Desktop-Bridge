# Legacy ErGe / Yusen Bridge Message Delivery Hardening

Date: 2026-09-23
Machine: Jonathan-G14

## Problem

ChatGPT could show **message delivery timed out, please try again** while using the private ErGe-PC connector even though the local computer and tunnel were still alive.

Bridge telemetry and the supervisor recovery log identified the main blocking paths:

- `run_shell`
- `git_snapshot`
- `search_files`
- `list_processes`

Historical maximum durations before hardening included approximately:

- run_shell: 338 seconds
- git_snapshot: 61 seconds
- search_files: 55 seconds
- list_processes: 48 seconds

These durations could exceed the ChatGPT message-delivery window before the bridge returned a tool result.

## First-principles fix

The MCP request path must remain responsive.

Long work may continue locally, but the ChatGPT tool call itself must return quickly.

The hardening therefore uses two rules:

1. Interactive MCP work is bounded and returns partial state rather than blocking.
2. Long shell work detaches into a local bounded job that can survive a client retry or message interruption.

## Implemented changes

### Shell execution

Long `run_shell` calls now:

1. Create a local job under `%USERPROFILE%\.yusen-pc-bridge\jobs\<job-id>`.
2. Launch a hidden worker.
3. Wait only about two seconds for an immediate completion.
4. Return `detached=true` and a `job_id` when work continues.
5. Keep stdout, stderr and status in bounded local files.
6. Enforce the original requested command timeout in the worker.
7. Kill the worker-owned process tree when the command timeout expires.
8. Retain recent jobs for resume/recovery.
9. Maintain `jobs\latest.json` as a backward-compatible resume pointer.

New MCP helpers are defined for newly connected clients:

- `get_shell_job`
- `list_shell_jobs`
- `cancel_shell_job`

A ChatGPT conversation whose tool schema was loaded before these tools were added can still poll the returned status/output paths or `jobs\latest.json`.

### Git inspection

Git subcommands now run in parallel with an approximately 1.5 second budget each.

If local Git is unhealthy, `git_snapshot` returns partial results and explicit timeout errors instead of blocking the MCP server.

### File search and listing

Recursive directory and search work has a roughly two-second scan budget.

When the deadline is reached, the tool returns the matches collected so far with:

- `truncated=true`
- `deadline_exceeded=true`

### File reads

One interactive read is capped at 512 KiB.

Larger files are read using `offset_bytes` paging rather than delivering a multi-megabyte tool response in one ChatGPT message.

### Process enumeration

Process listing no longer performs expensive Windows username lookups.

It returns lightweight:

- PID
- parent PID
- process name

and has a two-second enumeration deadline.

### Large filesystem operations

Potentially long operations fail fast and instruct the caller to use detached `run_shell` instead:

- directory copy
- file copy larger than 64 MiB
- file hash larger than 256 MiB
- cross-volume move
- replacement of existing directories
- recursive permanent directory deletion

This deliberately reuses the detached shell mechanism instead of creating a second background-job subsystem.

### Watchdog budgets

The high-risk MCP calls now use short stall budgets.

The supervisor therefore no longer waits minutes before recognizing a genuinely stuck interactive request.

## Verification on Jonathan-G14

The hardened runtime was syntax compiled successfully:

`PY_COMPILE_OK`

Controlled bridge recycles completed successfully.

Measured final behavior:

- instant shell command: underlying execution approximately 0.03 seconds
- long shell request: returned to ChatGPT in approximately 4 seconds and continued locally
- recursive search: approximately 3.6 seconds and returned partial results
- broken local Git inspection: approximately 2.7 seconds and returned partial errors
- process enumeration: approximately 2.9 seconds and returned bounded partial results

A deliberately long background command completed successfully with its output preserved.

A deliberately timed-out background command:

- entered `timed_out`
- terminated its child tree
- left the bridge online

A 30-second local command was launched in the background while ChatGPT immediately performed additional bridge-status and screen-information operations. The bridge remained responsive during the running job.

After the hardened runtime was loaded, the supervisor recovery log showed no new `tool_stall` restart. Recent child exits were controlled bridge recycles performed during deployment.

## Resume behavior after a ChatGPT delivery interruption

If a ChatGPT message itself is interrupted while local work is running:

1. Do not rerun the original long command blindly.
2. Read `%USERPROFILE%\.yusen-pc-bridge\jobs\latest.json` or use `list_shell_jobs`.
3. Inspect the job status.
4. Poll its stdout/stderr or use `get_shell_job`.
5. Continue from the completed or still-running local job.

This is the key property that allows work to continue even if a ChatGPT message must be retried.

## Platform boundary

This hardening removes the known **bridge-side** causes of message delivery timeouts.

It cannot guarantee that the ChatGPT client, local network, or OpenAI platform will never experience an unrelated delivery interruption.

The important architectural change is that a client-side interruption no longer has to cancel or duplicate long work on Jonathan-G14.

## Preserved patch

The exact migration patch is stored at:

`migration/legacy-yusen/message-delivery-hardening.patch`

It includes changes to:

- `src/yusen_pc_bridge/processes.py`
- `src/yusen_pc_bridge/files.py`
- `src/yusen_pc_bridge/server.py`
- new `src/yusen_pc_bridge/shell_job_worker.py`
