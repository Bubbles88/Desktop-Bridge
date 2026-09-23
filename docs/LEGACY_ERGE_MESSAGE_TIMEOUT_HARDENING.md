# Legacy ErGe PC Message Timeout Hardening

Date: 2026-09-23
Machine: Jonathan-G14

This folder preserves the exact legacy bridge source that was live-tested before Phase 3 of Desktop Bridge.

It is not the permanent Desktop Bridge architecture. It exists so the working `@ErGe-PC` development path cannot regress to the previous blocking behavior during migration.

## Problem

The legacy bridge previously allowed individual synchronous MCP tool calls to occupy the request path for tens or hundreds of seconds.

Historical recovery events included:

- `search_files` stalls around 90 seconds
- `git_snapshot` stalls around 60 seconds
- `run_shell` stalls around 150 seconds, later around 330 seconds, and once around 930 seconds

ChatGPT can give up before those local watchdogs fire and show:

`Message delivery timed out. Please try again.`

The bridge itself could still be alive, but the active MCP call had monopolized the request path.

## First-principles fix

A ChatGPT-facing tool call must be short.

Long local work may continue, but it must not keep the MCP request open.

The hardening therefore changes the execution contract.

### Shell

Short shell work remains synchronous.

Long shell work:

1. starts a bounded local worker
2. returns control to ChatGPT after about 3 seconds
3. returns a job ID
4. returns status/stdout/stderr paths
5. continues locally
6. enforces its own requested timeout
7. terminates its owned process tree if that timeout expires

Background job artifacts live under:

`%USERPROFILE%\.yusen-pc-bridge\jobs\<job_id>\`

Retention is bounded.

### File reads

Interactive text reads are capped to 512 KiB per call.

Larger files must be paged using offsets.

### Directory listing and file search

Recursive scans use a short deadline and return partial results instead of monopolizing the bridge.

### Git snapshot

Independent Git probes use short timeouts and return partial errors.

A hung Git command no longer stalls the whole bridge.

### MCP watchdog budgets

The historically problematic tools now have short lifecycle budgets aligned with their new fast-return behavior.

## Live verification

### Short synchronous shell

Command:

`echo SHORT_SHELL_OK`

Observed:

- bridge tool duration: about 0.04 seconds
- full connector round trip: about 1.95 seconds
- synchronous
- return code 0

### Long shell detach

A command deliberately lasting about 15 seconds was started with a 30 second local timeout.

Observed:

- MCP execution duration: about 3.05 seconds
- full connector round trip: about 6.9 seconds
- returned a job ID
- bridge remained available
- job completed later with `LONG_JOB_DONE`

### Concurrency

A second command lasting about 25 seconds was detached.

While it was still running:

- `bridge_status` returned in about 1 second
- bridge remained fully available
- job later completed successfully with `CONCURRENCY_JOB_DONE`

This proves long local work no longer monopolizes the MCP request path.

### Background timeout

A command designed to run about 19 seconds was started with a 5 second job timeout.

Observed final state:

- `state = timed_out`
- `timed_out = true`
- owned subprocess tree was terminated
- bridge remained available

### File search

Broad search under `C:\Users\Jonat\Documents`:

- returned in about 5.9 seconds
- result was bounded / partial as appropriate
- no bridge restart

### Git snapshot

The existing local Git environment still hangs.

Instead of blocking the bridge:

- Git snapshot returned in about 5 seconds
- each Git subcommand reported `timed out after 3s`
- `partial = true`
- no bridge restart

### Large text read

Recovery log size exceeded 512 KiB.

Observed:

- returned exactly 524288 bytes
- `truncated = true`
- caller can continue with an offset

### Telemetry

Current-generation tool lifecycle telemetry showed:

- long `run_shell`: about 3.06 seconds
- broad `search_files`: about 4.34 seconds
- hung `git_snapshot`: about 3.69 seconds
- ordinary file reads: milliseconds
- bridge status: milliseconds locally

### Recovery log

After the hardened runtime generation loaded, no tool-stall supervisor restart was recorded during the long-job, concurrency, Git, search, or timeout tests.

## Current-session compatibility

A newly exposed shell-job polling tool may not appear in a ChatGPT conversation whose plugin schema was loaded before the server restart.

For backward compatibility, every detached job returns:

- `job_id`
- `status_path`
- `stdout_path`
- `stderr_path`
- `latest_job_path`

The existing file tools can therefore poll a job immediately even if the current ChatGPT conversation has not refreshed the MCP schema.

A fresh plugin/tool schema should expose the dedicated shell-job polling operations.

## Operational rule

For future ErGe development:

1. Use narrow native tools first.
2. Use short synchronous shell calls only for work expected to finish immediately.
3. Let longer shell work detach.
4. Poll status/output rather than holding the request open.
5. Never use one giant diagnostic call when several bounded probes can answer the same question.
6. Treat ChatGPT platform timeout as external and design every normal MCP interaction to return well before it.

## Boundary

This hardening greatly reduces bridge-caused ChatGPT message delivery timeouts.

It cannot change ChatGPT platform-side delivery deadlines or outages.

The engineering objective is therefore:

> Never give the platform a reason to wait on a long local computer operation.
