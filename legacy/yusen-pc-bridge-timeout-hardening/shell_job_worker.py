"""Detached shell job worker used by the MCP bridge.

The MCP request path must remain responsive. This worker owns long-running shell
commands outside the synchronous tool call, enforces the requested timeout, and
writes bounded output plus an atomic status record for later polling.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, BinaryIO


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _write_status(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + f".{os.getpid()}.tmp")
    temp.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(temp, path)


def _terminate_tree(proc: subprocess.Popen[bytes]) -> None:
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


def _pump(
    source: BinaryIO,
    destination: Path,
    max_bytes: int,
    state: dict[str, bool],
    key: str,
) -> None:
    written = 0
    destination.parent.mkdir(parents=True, exist_ok=True)

    with destination.open("wb") as handle:
        while True:
            chunk = source.read(64 * 1024)
            if not chunk:
                break

            remaining = max(0, max_bytes - written)
            if remaining:
                part = chunk[:remaining]
                handle.write(part)
                handle.flush()
                written += len(part)

            if len(chunk) > remaining:
                state[key] = True


def main() -> int:
    if len(sys.argv) != 2:
        return 2

    spec_path = Path(sys.argv[1])
    spec = json.loads(spec_path.read_text(encoding="utf-8"))

    job_id = str(spec["job_id"])
    status_path = Path(spec["status_path"])
    stdout_path = Path(spec["stdout_path"])
    stderr_path = Path(spec["stderr_path"])
    argv = [str(value) for value in spec["argv"]]
    cwd = spec.get("cwd") or None
    timeout_seconds = max(1.0, float(spec["timeout_seconds"]))
    max_output_bytes = max(1024, int(spec["max_output_bytes"]))

    try:
        spec_path.unlink(missing_ok=True)
    except OSError:
        pass

    status: dict[str, Any] = {
        "job_id": job_id,
        "state": "starting",
        "worker_pid": os.getpid(),
        "child_pid": None,
        "started_at_utc": _utc_now(),
        "completed_at_utc": None,
        "return_code": None,
        "timed_out": False,
        "stdout_path": str(stdout_path),
        "stderr_path": str(stderr_path),
        "stdout_truncated": False,
        "stderr_truncated": False,
        "requested_timeout_seconds": timeout_seconds,
    }
    _write_status(status_path, status)

    creationflags = 0
    if os.name == "nt":
        creationflags |= getattr(subprocess, "CREATE_NO_WINDOW", 0)

    proc: subprocess.Popen[bytes] | None = None
    stream_state = {
        "stdout_truncated": False,
        "stderr_truncated": False,
    }

    try:
        proc = subprocess.Popen(
            argv,
            cwd=cwd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
            creationflags=creationflags,
        )

        if proc.stdout is None or proc.stderr is None:
            raise RuntimeError("Unable to capture shell output.")

        status["state"] = "running"
        status["child_pid"] = proc.pid
        _write_status(status_path, status)

        stdout_thread = threading.Thread(
            target=_pump,
            args=(proc.stdout, stdout_path, max_output_bytes, stream_state, "stdout_truncated"),
            daemon=True,
        )
        stderr_thread = threading.Thread(
            target=_pump,
            args=(proc.stderr, stderr_path, max_output_bytes, stream_state, "stderr_truncated"),
            daemon=True,
        )
        stdout_thread.start()
        stderr_thread.start()

        try:
            proc.wait(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            status["timed_out"] = True
            _terminate_tree(proc)

        stdout_thread.join(timeout=5)
        stderr_thread.join(timeout=5)

        status["return_code"] = proc.returncode
        status["state"] = (
            "timed_out"
            if status["timed_out"]
            else ("completed" if proc.returncode == 0 else "failed")
        )
        status["stdout_truncated"] = stream_state["stdout_truncated"]
        status["stderr_truncated"] = stream_state["stderr_truncated"]
        status["completed_at_utc"] = _utc_now()
        _write_status(status_path, status)
        return 0
    except BaseException as exc:
        if proc is not None:
            _terminate_tree(proc)

        status["state"] = "failed"
        status["completed_at_utc"] = _utc_now()
        status["error_type"] = type(exc).__name__
        status["error_message"] = str(exc)[:1000]
        status["stdout_truncated"] = stream_state["stdout_truncated"]
        status["stderr_truncated"] = stream_state["stderr_truncated"]
        _write_status(status_path, status)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
