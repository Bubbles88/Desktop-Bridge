"""Filesystem operations exposed through MCP.

Read and write paths are validated against the local bridge configuration before
any operation occurs. Deletion defaults to the recycle bin for recoverability.
"""

from __future__ import annotations

import hashlib
import os
import shutil
import stat as stat_module
import time
from pathlib import Path
from typing import Any

from send2trash import send2trash

from .config import load_config, require_capability
from .security import ensure_path_allowed, matches_any

_TEXT_ENCODINGS = ("utf-8-sig", "utf-8", "gb18030", "utf-16")
_MAX_INTERACTIVE_READ_BYTES = 512 * 1024
_FILE_SCAN_DEADLINE_SECONDS = 5.0


def _decode_text(data: bytes, encoding: str = "auto") -> tuple[str, str]:
    """Decode common Windows/Chinese text files with explicit fallback reporting."""

    if encoding != "auto":
        return data.decode(encoding), encoding
    for candidate in _TEXT_ENCODINGS:
        try:
            return data.decode(candidate), candidate
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", errors="replace"), "utf-8-replace"


def stat_path(path: str) -> dict[str, Any]:
    """Return metadata for one file or directory without changing it."""

    require_capability("files_read")
    target = ensure_path_allowed(path)
    st = target.stat()
    return {
        "path": str(target),
        "exists": True,
        "is_file": target.is_file(),
        "is_dir": target.is_dir(),
        "size": st.st_size,
        "modified_ns": st.st_mtime_ns,
        "created_ns": getattr(st, "st_ctime_ns", None),
        "readonly": not bool(st.st_mode & stat_module.S_IWRITE),
    }


def list_dir(path: str, recursive: bool = False, limit: int = 300) -> dict[str, Any]:
    """List directory entries with optional bounded recursion."""

    require_capability("files_read")
    root = ensure_path_allowed(path)
    if not root.is_dir():
        raise NotADirectoryError(str(root))

    config = load_config()
    limit = max(1, min(limit, config.max_search_results))
    entries: list[dict[str, Any]] = []

    iterator = root.rglob("*") if recursive else root.iterdir()
    truncated = False
    deadline_exceeded = False
    deadline = time.monotonic() + _FILE_SCAN_DEADLINE_SECONDS

    for item in iterator:
        if time.monotonic() >= deadline:
            truncated = True
            deadline_exceeded = True
            break

        ensure_path_allowed(item)
        try:
            st = item.stat()
            entries.append(
                {
                    "name": item.name,
                    "path": str(item),
                    "is_dir": item.is_dir(),
                    "size": st.st_size if item.is_file() else None,
                    "modified_ns": st.st_mtime_ns,
                }
            )
        except (PermissionError, FileNotFoundError, OSError) as exc:
            entries.append({"name": item.name, "path": str(item), "error": str(exc)})

        if len(entries) >= limit:
            truncated = True
            break

    return {
        "root": str(root),
        "entries": entries,
        "truncated": truncated,
        "deadline_exceeded": deadline_exceeded,
    }


def read_text(
    path: str,
    offset_bytes: int = 0,
    max_bytes: int | None = None,
    encoding: str = "auto",
) -> dict[str, Any]:
    """Read a bounded byte range from a text file and decode it."""

    require_capability("files_read")
    target = ensure_path_allowed(path)
    if not target.is_file():
        raise FileNotFoundError(str(target))

    config = load_config()
    requested_max_bytes = _MAX_INTERACTIVE_READ_BYTES if max_bytes is None else max(1, max_bytes)
    max_bytes = min(
        requested_max_bytes,
        config.max_text_read_bytes,
        _MAX_INTERACTIVE_READ_BYTES,
    )
    size = target.stat().st_size
    offset_bytes = max(0, offset_bytes)

    with target.open("rb") as handle:
        handle.seek(offset_bytes)
        data = handle.read(max_bytes + 1)

    truncated = len(data) > max_bytes
    if truncated:
        data = data[:max_bytes]
    text, used_encoding = _decode_text(data, encoding)
    return {
        "path": str(target),
        "size": size,
        "offset_bytes": offset_bytes,
        "bytes_returned": len(data),
        "encoding": used_encoding,
        "truncated": truncated or offset_bytes + len(data) < size,
        "content": text,
    }


def file_hash(path: str, algorithm: str = "sha256") -> dict[str, str]:
    """Calculate a cryptographic hash for one file."""

    require_capability("files_read")
    target = ensure_path_allowed(path)
    if not target.is_file():
        raise FileNotFoundError(str(target))
    try:
        digest = hashlib.new(algorithm)
    except ValueError as exc:
        raise ValueError(f"Unsupported hash algorithm: {algorithm}") from exc
    with target.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return {"path": str(target), "algorithm": algorithm, "digest": digest.hexdigest()}


def search_files(
    root: str,
    name_patterns: list[str] | None = None,
    content_query: str | None = None,
    limit: int = 100,
    max_file_bytes: int = 2 * 1024 * 1024,
) -> dict[str, Any]:
    """Search file names and optional text contents under one root."""

    require_capability("files_read")
    base = ensure_path_allowed(root)
    if not base.is_dir():
        raise NotADirectoryError(str(base))

    config = load_config()
    limit = max(1, min(limit, config.max_search_results))
    patterns = name_patterns or ["*"]
    query_lower = content_query.lower() if content_query else None
    matches: list[dict[str, Any]] = []
    skipped_errors = 0
    deadline = time.monotonic() + _FILE_SCAN_DEADLINE_SECONDS

    def partial() -> dict[str, Any]:
        return {
            "root": str(base),
            "matches": matches,
            "truncated": True,
            "skipped_errors": skipped_errors,
            "deadline_exceeded": True,
        }

    for current_root, dirs, files in os.walk(base):
        if time.monotonic() >= deadline:
            return partial()

        current = ensure_path_allowed(current_root)
        dirs[:] = [name for name in dirs if not name.startswith(".git")]

        for name in files:
            if time.monotonic() >= deadline:
                return partial()
            if not matches_any(name, patterns):
                continue

            path = ensure_path_allowed(current / name)
            record: dict[str, Any] = {"path": str(path), "name": name}
            try:
                size = path.stat().st_size
                record["size"] = size
                if query_lower is not None:
                    if size > max_file_bytes:
                        continue
                    data = path.read_bytes()
                    decoded, used_encoding = _decode_text(data)
                    idx = decoded.lower().find(query_lower)
                    if idx < 0:
                        continue
                    start = max(0, idx - 180)
                    end = min(len(decoded), idx + len(content_query or "") + 360)
                    record["encoding"] = used_encoding
                    record["snippet"] = decoded[start:end]
                matches.append(record)
            except (PermissionError, FileNotFoundError, OSError):
                skipped_errors += 1
                continue

            if len(matches) >= limit:
                return {
                    "root": str(base),
                    "matches": matches,
                    "truncated": True,
                    "skipped_errors": skipped_errors,
                    "deadline_exceeded": False,
                }

    return {
        "root": str(base),
        "matches": matches,
        "truncated": False,
        "skipped_errors": skipped_errors,
        "deadline_exceeded": False,
    }


def write_text(
    path: str,
    content: str,
    mode: str = "overwrite",
    encoding: str = "utf-8",
    create_parents: bool = True,
) -> dict[str, Any]:
    """Write or append text to a local file."""

    require_capability("files_write")
    target = ensure_path_allowed(path)
    if create_parents:
        parent = ensure_path_allowed(target.parent)
        parent.mkdir(parents=True, exist_ok=True)
    if mode not in {"overwrite", "append"}:
        raise ValueError("mode must be 'overwrite' or 'append'")
    file_mode = "w" if mode == "overwrite" else "a"
    with target.open(file_mode, encoding=encoding, newline="") as handle:
        handle.write(content)
    return {"path": str(target), "mode": mode, "size": target.stat().st_size}


def make_dir(path: str, parents: bool = True) -> dict[str, Any]:
    """Create a directory tree."""

    require_capability("files_write")
    target = ensure_path_allowed(path)
    target.mkdir(parents=parents, exist_ok=True)
    return {"path": str(target), "created": True}


def copy_path(source: str, destination: str, overwrite: bool = False) -> dict[str, Any]:
    """Copy a file or directory to a new local path."""

    require_capability("files_write")
    src = ensure_path_allowed(source)
    dst = ensure_path_allowed(destination)
    ensure_path_allowed(dst.parent).mkdir(parents=True, exist_ok=True)
    if dst.exists() and not overwrite:
        raise FileExistsError(str(dst))
    if src.is_dir():
        shutil.copytree(src, dst, dirs_exist_ok=overwrite)
    else:
        shutil.copy2(src, dst)
    return {"source": str(src), "destination": str(dst), "copied": True}


def move_path(source: str, destination: str, overwrite: bool = False) -> dict[str, Any]:
    """Move or rename a file/directory."""

    require_capability("files_write")
    src = ensure_path_allowed(source)
    dst = ensure_path_allowed(destination)
    ensure_path_allowed(dst.parent).mkdir(parents=True, exist_ok=True)
    if dst.exists():
        if not overwrite:
            raise FileExistsError(str(dst))
        if dst.is_dir():
            shutil.rmtree(dst)
        else:
            dst.unlink()
    shutil.move(str(src), str(dst))
    return {"source": str(src), "destination": str(dst), "moved": True}


def delete_path(path: str, permanent: bool = False) -> dict[str, Any]:
    """Delete a path, using the recycle bin unless permanent=True."""

    require_capability("files_write")
    target = ensure_path_allowed(path)
    if not target.exists():
        return {"path": str(target), "deleted": False, "reason": "not_found"}
    if permanent:
        if target.is_dir():
            shutil.rmtree(target)
        else:
            target.unlink()
        method = "permanent"
    else:
        send2trash(str(target))
        method = "recycle_bin"
    return {"path": str(target), "deleted": True, "method": method}
