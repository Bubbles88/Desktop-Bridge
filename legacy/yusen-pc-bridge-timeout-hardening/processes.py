"""Process and shell execution for the Windows bridge.

The shell tool is intentionally powerful. It is separately gated by local
configuration and marked as a mutating/destructive MCP action by server.py.

Long-running shell commands must not monopolize the MCP request path. Commands
that exceed the short interactive budget are detached into a bounded local job
worker and can be polled through the returned status/output file paths.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import time
import uuid
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any, Literal

import psutil

from .config import load_config, require_capability
from .security import ensure_path_allowed, limit_text

_MAX_INTERACTIVE_SHELL_SECONDS = 20
_MAX_INTERACTIVE_OUTPUT_CHARS = 30000

_INTERACTIVE_SHELL_WAIT_SECONDS = 3.0
_GIT_COMMAND_TIMEOUT_SECONDS = 3.0
_JOB_RETENTION_SECONDS = 7 * 24 * 60 * 60
_MAX_JOB_DIRS = 200


def list_processes(name_filter: str | None = None, limit: int = 300) -> dict[str, Any]:
    """List running processes with lightweight metadata."""

    require_capability("process_read")
    needle = name_filter.lower() if name_filter else None
    rows: list[dict[str, Any]] = []
    for proc in psutil.process_iter(["pid", "ppid", "name", "username", "status", "create_time"]):
        try:
            info = proc.info
            if needle and needle not in (info.get("name") or "").lower():
                continue
            rows.append(info)
        except (psutil.AccessDenied, psutil.NoSuchProcess):
            continue
        if len(rows) >= max(1, min(limit, 1000)):
            break
    rows.sort(key=lambda row: ((row.get("name") or "").lower(), int(row.get("pid") or 0)))
    return {"processes": rows, "count": len(rows)}


def start_process(
    executable: str,
    args: list[str] | None = None,
    cwd: str | None = None,
    hidden: bool = False,
) -> dict[str, Any]:
    """Start one executable without passing through a command shell."""

    require_capability("process_control")
    working_dir = str(ensure_path_allowed(cwd)) if cwd else None
    command = [os.path.expandvars(os.path.expanduser(executable)), *(args or [])]
    startupinfo = None
    creationflags = 0
    if os.name == "nt" and hidden:
        startupinfo = subprocess.STARTUPINFO()
        startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startupinfo.wShowWindow = 0
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    proc = subprocess.Popen(
        command,
        cwd=working_dir,
        shell=False,
        startupinfo=startupinfo,
        creationflags=creationflags,
    )
    return {"pid": proc.pid, "command": command, "cwd": working_dir}


def terminate_process(pid: int, tree: bool = True, force: bool = False, timeout: float = 5.0) -> dict[str, Any]:
    """Terminate one process and optionally its children."""

    require_capability("process_control")
    root = psutil.Process(pid)
    targets = root.children(recursive=True) if tree else []
    targets.append(root)
    acted: list[int] = []
    for proc in reversed(targets):
        try:
            proc.kill() if force else proc.terminate()
            acted.append(proc.pid)
        except psutil.NoSuchProcess:
            continue
    gone, alive = psutil.wait_procs(targets, timeout=max(0.1, timeout))
    if alive and not force:
        for proc in alive:
            try:
                proc.kill()
            except psutil.NoSuchProcess:
                pass
        psutil.wait_procs(alive, timeout=2.0)
    return {"requested_pid": pid, "affected_pids": acted, "force": force}


def _powershell_executable() -> str:
    """Prefer the Windows PowerShell path already proven on Jonathan-G14."""

    return shutil.which("powershell.exe") or shutil.which("powershell") or shutil.which("pwsh") or "powershell.exe"


def _terminate_subprocess_tree(proc: subprocess.Popen[str]) -> None:
    """Terminate a timed-out child and all descendants so inherited pipes close promptly."""

    if proc.poll() is not None:
        return

    if os.name == "nt":
        try:
            subprocess.run(
                ["taskkill.exe", "/PID", str(proc.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=10,
                shell=False,
            )
        except (OSError, subprocess.TimeoutExpired):
            try:
                proc.kill()
            except OSError:
                pass
    else:
        try:
            proc.kill()
        except OSError:
            pass

    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        try:
            proc.kill()
        except OSError:
            pass


def _run_capture(
    argv: list[str],
    *,
    cwd: str | None = None,
    timeout_seconds: float,
) -> tuple[int | None, str, str, bool]:
    """Run one process with bounded capture and reliable process-tree cleanup."""

    proc = subprocess.Popen(
        argv,
        cwd=cwd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        errors="replace",
        shell=False,
    )
    try:
        stdout, stderr = proc.communicate(timeout=timeout_seconds)
        return proc.returncode, stdout, stderr, False
    except subprocess.TimeoutExpired:
        _terminate_subprocess_tree(proc)
        try:
            stdout, stderr = proc.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            try:
                proc.kill()
            except OSError:
                pass
            stdout, stderr = proc.communicate()
        return None, stdout or "", stderr or "", True


def _app_home() -> Path:
    configured = os.environ.get("YUSEN_PC_BRIDGE_HOME", "").strip()
    if configured:
        return Path(configured).expanduser()
    return Path.home() / ".yusen-pc-bridge"


def _jobs_root() -> Path:
    root = _app_home() / "jobs"
    root.mkdir(parents=True, exist_ok=True)
    return root


def _prune_jobs(root: Path) -> None:
    """Best-effort bounded retention for detached shell job artifacts."""

    try:
        now = time.time()
        directories = [entry for entry in root.iterdir() if entry.is_dir()]
        directories.sort(key=lambda entry: entry.stat().st_mtime, reverse=True)

        for index, entry in enumerate(directories):
            try:
                age = now - entry.stat().st_mtime
                if index >= _MAX_JOB_DIRS or age > _JOB_RETENTION_SECONDS:
                    shutil.rmtree(entry, ignore_errors=True)
            except OSError:
                continue
    except OSError:
        pass


def _read_job_status(path: Path) -> dict[str, Any] | None:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def _read_job_output(path: Path, max_chars: int) -> tuple[str, bool]:
    try:
        data = path.read_bytes()
    except OSError:
        return "", False
    text = data.decode("utf-8", errors="replace")
    return limit_text(text, max_chars)


def _start_shell_job(
    argv: list[str],
    *,
    cwd: str | None,
    timeout_seconds: int,
    max_output_chars: int,
    sync_wait_seconds: float,
) -> dict[str, Any]:
    """Start one bounded detached shell job and wait only for the interactive budget."""

    root = _jobs_root()
    _prune_jobs(root)

    job_id = uuid.uuid4().hex
    job_dir = root / job_id
    job_dir.mkdir(parents=True, exist_ok=False)

    spec_path = job_dir / "spec.json"
    status_path = job_dir / "status.json"
    stdout_path = job_dir / "stdout.txt"
    stderr_path = job_dir / "stderr.txt"

    spec = {
        "job_id": job_id,
        "argv": argv,
        "cwd": cwd,
        "timeout_seconds": timeout_seconds,
        "max_output_bytes": max(4096, max_output_chars * 4),
        "status_path": str(status_path),
        "stdout_path": str(stdout_path),
        "stderr_path": str(stderr_path),
    }
    spec_path.write_text(json.dumps(spec, ensure_ascii=False), encoding="utf-8")

    latest_path = root / "latest.json"
    latest_temp = root / (".latest." + uuid.uuid4().hex + ".tmp")
    latest_payload = {
        "job_id": job_id,
        "status_path": str(status_path),
        "stdout_path": str(stdout_path),
        "stderr_path": str(stderr_path),
        "created_unix": time.time(),
    }
    try:
        latest_temp.write_text(
            json.dumps(latest_payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        os.replace(latest_temp, latest_path)
    finally:
        try:
            latest_temp.unlink(missing_ok=True)
        except OSError:
            pass

    creationflags = 0
    if os.name == "nt":
        creationflags |= getattr(subprocess, "CREATE_NO_WINDOW", 0)
        creationflags |= getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)

    worker = subprocess.Popen(
        [sys.executable, "-m", "yusen_pc_bridge.shell_job_worker", str(spec_path)],
        cwd=cwd,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        shell=False,
        creationflags=creationflags,
    )

    deadline = time.monotonic() + max(0.1, sync_wait_seconds)
    status: dict[str, Any] | None = None

    while time.monotonic() < deadline:
        status = _read_job_status(status_path)
        if status and status.get("state") in {"completed", "timed_out", "failed"}:
            break
        if worker.poll() is not None and status is None:
            break
        time.sleep(0.05)

    status = _read_job_status(status_path) or {
        "job_id": job_id,
        "state": "running" if worker.poll() is None else "failed",
        "worker_pid": worker.pid,
        "child_pid": None,
        "return_code": None,
        "timed_out": False,
        "stdout_truncated": False,
        "stderr_truncated": False,
        "requested_timeout_seconds": timeout_seconds,
    }

    stdout, stdout_truncated = _read_job_output(stdout_path, max_output_chars)
    stderr, stderr_truncated = _read_job_output(stderr_path, max_output_chars)

    state = str(status.get("state") or "running")
    terminal = state in {"completed", "timed_out", "failed"}

    return {
        "job_id": job_id,
        "job_state": state,
        "detached": not terminal,
        "worker_pid": status.get("worker_pid", worker.pid),
        "child_pid": status.get("child_pid"),
        "return_code": status.get("return_code"),
        "timed_out": bool(status.get("timed_out", False)),
        "stdout": stdout,
        "stderr": stderr,
        "stdout_truncated": bool(status.get("stdout_truncated", False) or stdout_truncated),
        "stderr_truncated": bool(status.get("stderr_truncated", False) or stderr_truncated),
        "status_path": str(status_path),
        "stdout_path": str(stdout_path),
        "stderr_path": str(stderr_path),
        "requested_timeout_seconds": timeout_seconds,
        "latest_job_path": str(latest_path),
        "poll_after_seconds": 2 if not terminal else 0,
    }


def run_shell(
    command: str,
    cwd: str | None = None,
    shell: Literal["powershell", "cmd"] = "powershell",
    timeout_seconds: int = 120,
) -> dict[str, Any]:
    """Run shell work without allowing long commands to monopolize the MCP request path."""

    require_capability("shell")
    config = load_config()
    requested_timeout_seconds = max(1, min(timeout_seconds, config.max_shell_timeout_seconds))
    effective_timeout_seconds = min(requested_timeout_seconds, _MAX_INTERACTIVE_SHELL_SECONDS)
    working_dir = str(ensure_path_allowed(cwd)) if cwd else None

    if shell == "powershell":
        argv = [
            _powershell_executable(),
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            command,
        ]
    elif shell == "cmd":
        argv = ["cmd.exe" if os.name == "nt" else "cmd", "/d", "/s", "/c", command]
    else:
        raise ValueError("shell must be 'powershell' or 'cmd'")

    started = time.monotonic()

    output_limit = min(config.max_shell_output_chars, _MAX_INTERACTIVE_OUTPUT_CHARS)

    if requested_timeout_seconds <= _INTERACTIVE_SHELL_WAIT_SECONDS:
        return_code, stdout, stderr, timed_out = _run_capture(
            argv,
            cwd=working_dir,
            timeout_seconds=effective_timeout_seconds,
        )

        stdout, stdout_truncated = limit_text(stdout, output_limit)
        stderr, stderr_truncated = limit_text(stderr, output_limit)
        return {
            "shell": shell,
            "command": command,
            "cwd": working_dir,
            "return_code": return_code,
            "timed_out": timed_out,
            "detached": False,
            "duration_seconds": round(time.monotonic() - started, 3),
            "stdout": stdout,
            "stderr": stderr,
            "stdout_truncated": stdout_truncated,
            "stderr_truncated": stderr_truncated,
        }

    job = _start_shell_job(
        argv,
        cwd=working_dir,
        timeout_seconds=requested_timeout_seconds,
        max_output_chars=output_limit,
        sync_wait_seconds=_INTERACTIVE_SHELL_WAIT_SECONDS,
    )

    return {
        "shell": shell,
        "command": command,
        "cwd": working_dir,
        "duration_seconds": round(time.monotonic() - started, 3),
        **job,
    }


def _validate_job_id(job_id: str) -> str:
    value = job_id.strip().lower()
    if len(value) != 32 or any(char not in "0123456789abcdef" for char in value):
        raise ValueError("Invalid shell job id.")
    return value


def get_shell_job(job_id: str, max_chars: int = 12000) -> dict[str, Any]:
    """Read one detached shell job without blocking on its completion."""

    require_capability("process_read")
    job_id = _validate_job_id(job_id)
    max_chars = max(1, min(int(max_chars), _MAX_INTERACTIVE_OUTPUT_CHARS))
    job_dir = _jobs_root() / job_id
    status_path = job_dir / "status.json"
    status = _read_job_status(status_path)
    if status is None:
        raise FileNotFoundError(f"Unknown shell job: {job_id}")

    stdout, stdout_truncated = _read_job_output(job_dir / "stdout.txt", max_chars)
    stderr, stderr_truncated = _read_job_output(job_dir / "stderr.txt", max_chars)

    return {
        **status,
        "stdout": stdout,
        "stderr": stderr,
        "stdout_truncated": bool(status.get("stdout_truncated", False) or stdout_truncated),
        "stderr_truncated": bool(status.get("stderr_truncated", False) or stderr_truncated),
        "poll_after_seconds": 2 if status.get("state") not in {"completed", "timed_out", "failed", "cancelled"} else 0,
    }


def list_shell_jobs(limit: int = 20) -> dict[str, Any]:
    """List recent detached shell jobs without exposing command text."""

    require_capability("process_read")
    limit = max(1, min(int(limit), 100))
    root = _jobs_root()
    _prune_jobs(root)

    rows: list[dict[str, Any]] = []
    status_paths = sorted(
        root.glob("*/status.json"),
        key=lambda path: path.stat().st_mtime_ns,
        reverse=True,
    )
    for status_path in status_paths[:limit]:
        status = _read_job_status(status_path)
        if status is not None:
            rows.append(status)

    return {"jobs": rows, "count": len(rows)}


def cancel_shell_job(job_id: str) -> dict[str, Any]:
    """Cancel a detached shell job and its owned process tree."""

    require_capability("process_control")
    job_id = _validate_job_id(job_id)
    status_path = _jobs_root() / job_id / "status.json"
    status = _read_job_status(status_path)
    if status is None:
        raise FileNotFoundError(f"Unknown shell job: {job_id}")

    if status.get("state") in {"completed", "timed_out", "failed", "cancelled"}:
        return {**status, "already_finished": True}

    worker_pid = status.get("worker_pid")
    if worker_pid:
        try:
            subprocess.run(
                ["taskkill.exe", "/PID", str(int(worker_pid)), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=5,
                shell=False,
            )
        except (OSError, subprocess.TimeoutExpired):
            pass

    status["state"] = "cancelled"
    status["completed_at_utc"] = status.get("completed_at_utc") or time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    _write_job_status = status_path.with_name(status_path.name + ".tmp")
    try:
        _write_job_status.write_text(json.dumps(status, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(_write_job_status, status_path)
    finally:
        try:
            _write_job_status.unlink(missing_ok=True)
        except OSError:
            pass
    return status


def open_path(path: str) -> dict[str, Any]:
    """Open a local file or directory with its Windows default application."""

    require_capability("process_control")
    target = ensure_path_allowed(path)
    if os.name != "nt":
        raise RuntimeError("open_path is only available on Windows.")
    os.startfile(str(target))  # type: ignore[attr-defined]
    return {"path": str(target), "opened": True}


def git_snapshot(path: str, recent_commits: int = 5) -> dict[str, Any]:
    """Read Git state with a hard interactive latency budget and partial results on timeout."""

    require_capability("process_read")
    repo = ensure_path_allowed(path)
    if not repo.is_dir():
        raise NotADirectoryError(str(repo))

    commands: dict[str, list[str]] = {
        "branch": ["branch", "--show-current"],
        "head": ["rev-parse", "HEAD"],
        "status_porcelain": ["status", "--porcelain=v1", "--branch"],
        "recent_commits": [
            "log",
            f"-{max(1, min(recent_commits, 20))}",
            "--pretty=format:%H%x09%ad%x09%s",
            "--date=iso-strict",
        ],
    }

    def run_one(name: str, args: list[str]) -> tuple[str, str | None, str | None]:
        return_code, stdout, stderr, timed_out = _run_capture(
            ["git", "-C", str(repo), *args],
            timeout_seconds=_GIT_COMMAND_TIMEOUT_SECONDS,
        )
        if timed_out:
            return name, None, f"timed out after {_GIT_COMMAND_TIMEOUT_SECONDS:g}s"
        if return_code != 0:
            return name, None, stderr.strip() or "git command failed"
        return name, stdout.strip(), None

    values: dict[str, Any] = {}
    errors: dict[str, str] = {}

    with ThreadPoolExecutor(max_workers=len(commands)) as executor:
        futures = [executor.submit(run_one, name, args) for name, args in commands.items()]
        for future in as_completed(futures):
            name, value, error = future.result()
            if error is not None:
                errors[name] = error
            else:
                values[name] = value

    return {
        "path": str(repo),
        "branch": values.get("branch"),
        "head": values.get("head"),
        "status_porcelain": values.get("status_porcelain"),
        "recent_commits": values.get("recent_commits"),
        "partial": bool(errors),
        "errors": errors,
    }
