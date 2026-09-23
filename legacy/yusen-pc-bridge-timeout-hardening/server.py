"""MCP tool surface for the Windows PC bridge.

The server is designed for stdio transport behind OpenAI Secure MCP Tunnel. Tool
annotations tell ChatGPT which actions are read-only, mutating, or destructive.
Every tool is wrapped with sanitized lifecycle telemetry so a supervisor can
identify a stalled call without recording arguments or return values.
"""

from __future__ import annotations

from collections.abc import Callable
from typing import Any, Literal, TypeVar, cast

from mcp.server.fastmcp import FastMCP, Image
from mcp.types import ToolAnnotations

from . import computer, files, processes, windows
from .config import config_path, load_config, machine_identity
from .telemetry import emit_server_start, trace_tool

_startup_config = load_config()
_startup_identity = machine_identity(_startup_config)

mcp = FastMCP(
    _startup_config.server_name,
    instructions=(
        f"Use this private bridge only for machine_id={_startup_identity['machine_id']} "
        f"hostname={_startup_identity['hostname']}. Read current state before acting when "
        "coordinates, window identity, file paths, or process identity could have changed. "
        "Prefer recycle-bin deletion over permanent deletion unless explicitly needed. "
        "Shell calls are latency-bounded: if run_shell returns detached=true and a job_id, "
        "poll that same job with get_shell_job instead of rerunning the command. Use list_shell_jobs "
        "to recover recent job IDs after a client interruption. If the current client has stale tool "
        "schema and cannot call those helpers, read the bridge jobs\\latest.json pointer and then read "
        "the referenced status/output files rather than rerunning the command."
    ),
)

READ = ToolAnnotations(
    readOnlyHint=True,
    destructiveHint=False,
    idempotentHint=True,
    openWorldHint=True,
)
WRITE = ToolAnnotations(
    readOnlyHint=False,
    destructiveHint=False,
    idempotentHint=False,
    openWorldHint=True,
)
DESTRUCTIVE = ToolAnnotations(
    readOnlyHint=False,
    destructiveHint=True,
    idempotentHint=False,
    openWorldHint=True,
)

F = TypeVar("F", bound=Callable[..., Any])


def tool(
    annotations: ToolAnnotations,
    *,
    stall_after_seconds: float = 45.0,
) -> Callable[[F], F]:
    """Register one MCP tool and add privacy-safe lifecycle telemetry."""

    def decorator(func: F) -> F:
        traced = trace_tool(stall_after_seconds=stall_after_seconds)(func)
        registered = mcp.tool(annotations=annotations)(traced)
        return cast(F, registered)

    return decorator


@tool(READ)
def bridge_status() -> dict[str, Any]:
    """Use this to inspect bridge capabilities and local limits before using PC tools."""

    config = load_config()
    identity = machine_identity(config)
    return {
        "name": config.server_name,
        "machine_id": identity["machine_id"],
        "server_name": identity["server_name"],
        "hostname": identity["hostname"],
        "profile": config.profile,
        "config_path": str(config_path()),
        "capabilities": {
            name: getattr(config.capabilities, name) for name in config.capabilities.__slots__
        },
        "allowed_paths": config.allowed_paths,
        "blocked_paths": config.blocked_paths,
        "allow_unc_paths": config.allow_unc_paths,
        "limits": {
            "max_text_read_bytes": config.max_text_read_bytes,
            "max_shell_output_chars": config.max_shell_output_chars,
            "max_shell_timeout_seconds": config.max_shell_timeout_seconds,
            "max_search_results": config.max_search_results,
            "max_screenshot_width": config.max_screenshot_width,
        },
    }


@tool(READ)
def get_screen_info() -> dict[str, Any]:
    """Use this to inspect monitor coordinates and dimensions before targeted screen control."""

    return computer.screen_info()


@tool(READ)
def get_screen(
    monitor_index: int = 0,
    max_width: int | None = None,
    left: int | None = None,
    top: int | None = None,
    width: int | None = None,
    height: int | None = None,
) -> Image:
    """Use this to visually inspect the current desktop, one monitor, or a coordinate region."""

    data, _metadata = computer.screenshot_png(
        monitor_index=monitor_index,
        max_width=max_width,
        left=left,
        top=top,
        width=width,
        height=height,
    )
    return Image(data=data, format="png")


@tool(READ)
def get_local_image(path: str, max_width: int | None = None) -> Image:
    """Use this to visually inspect an authorized local image file from disk."""

    data, _metadata = computer.local_image_png(path=path, max_width=max_width)
    return Image(data=data, format="png")


@tool(READ)
def get_cursor_position() -> dict[str, int]:
    """Use this to read the current mouse coordinates before or after desktop interaction."""

    return computer.cursor_position()


@tool(READ)
def get_foreground_window() -> dict[str, Any]:
    """Use this to identify the window that currently has keyboard focus."""

    return windows.foreground_window()


@tool(READ)
def list_windows(title_filter: str | None = None, visible_only: bool = True) -> dict[str, Any]:
    """Use this to find desktop windows, handles, owning PIDs, state, and screen rectangles."""

    return windows.list_windows(title_filter=title_filter, visible_only=visible_only)


@tool(WRITE)
def activate_window(hwnd: int) -> dict[str, Any]:
    """Use this to restore and focus a known window handle before keyboard interaction."""

    return windows.activate_window(hwnd)


@tool(DESTRUCTIVE)
def close_window(hwnd: int) -> dict[str, Any]:
    """Use this to request that a known application window close through the normal UI path."""

    return windows.close_window(hwnd)


@tool(WRITE)
def move_mouse(x: int, y: int, duration: float = 0.0) -> dict[str, Any]:
    """Use this to move the Windows mouse pointer to an absolute desktop coordinate."""

    return computer.move_mouse(x, y, duration)


@tool(WRITE)
def click_mouse(
    x: int | None = None,
    y: int | None = None,
    button: Literal["left", "middle", "right"] = "left",
    clicks: int = 1,
    interval: float = 0.1,
) -> dict[str, Any]:
    """Use this to click the current pointer position or an explicit desktop coordinate."""

    return computer.click_mouse(x=x, y=y, button=button, clicks=clicks, interval=interval)


@tool(WRITE)
def drag_mouse(
    x: int,
    y: int,
    duration: float = 0.5,
    button: Literal["left", "middle", "right"] = "left",
) -> dict[str, Any]:
    """Use this to drag the current pointer to an absolute coordinate with a held mouse button."""

    return computer.drag_mouse(x=x, y=y, duration=duration, button=button)


@tool(WRITE)
def scroll_mouse(clicks: int, x: int | None = None, y: int | None = None) -> dict[str, Any]:
    """Use this to scroll vertically in the focused UI or at a specific screen coordinate."""

    return computer.scroll_mouse(clicks=clicks, x=x, y=y)


@tool(WRITE)
def press_key(key: str, presses: int = 1, interval: float = 0.05) -> dict[str, Any]:
    """Use this to press a named keyboard key in the currently focused application."""

    return computer.press_key(key=key, presses=presses, interval=interval)


@tool(WRITE)
def press_hotkey(keys: list[str]) -> dict[str, Any]:
    """Use this to press a keyboard chord such as ctrl+shift+s in the focused application."""

    return computer.hotkey(keys)


@tool(WRITE)
def type_text(text: str, interval: float = 0.0, via_clipboard: bool = True) -> dict[str, Any]:
    """Use this to enter text in the focused application, including Unicode text via clipboard."""

    return computer.type_text(text=text, interval=interval, via_clipboard=via_clipboard)


@tool(DESTRUCTIVE, stall_after_seconds=90.0)
def computer_batch(actions: list[dict[str, Any]]) -> dict[str, Any]:
    """Use this to execute a short ordered sequence of UI actions after current state is known."""

    return computer.computer_batch(actions)


@tool(READ, stall_after_seconds=8.0)
def read_clipboard() -> dict[str, str]:
    """Use this to inspect text currently stored in the local Windows clipboard."""

    return computer.read_clipboard()


@tool(WRITE, stall_after_seconds=8.0)
def write_clipboard(text: str) -> dict[str, Any]:
    """Use this to replace the local Windows clipboard text for a later paste operation."""

    return computer.write_clipboard(text)


@tool(READ)
def stat_path(path: str) -> dict[str, Any]:
    """Use this to inspect metadata for a known local file or directory path."""

    return files.stat_path(path)


@tool(READ, stall_after_seconds=10.0)
def list_files(path: str, recursive: bool = False, limit: int = 300) -> dict[str, Any]:
    """Use this to browse files and directories on the authorized local filesystem."""

    return files.list_dir(path=path, recursive=recursive, limit=limit)


@tool(READ, stall_after_seconds=10.0)
def read_file(
    path: str,
    offset_bytes: int = 0,
    max_bytes: int | None = None,
    encoding: str = "auto",
) -> dict[str, Any]:
    """Use this to read bounded text content from an authorized local file path."""

    return files.read_text(path, offset_bytes=offset_bytes, max_bytes=max_bytes, encoding=encoding)


@tool(READ)
def hash_file(path: str, algorithm: str = "sha256") -> dict[str, str]:
    """Use this to calculate a local file hash for integrity or change comparison."""

    return files.file_hash(path, algorithm=algorithm)


@tool(READ, stall_after_seconds=10.0)
def search_files(
    root: str,
    name_patterns: list[str] | None = None,
    content_query: str | None = None,
    limit: int = 100,
    max_file_bytes: int = 2 * 1024 * 1024,
) -> dict[str, Any]:
    """Use this to find local files by filename glob and optionally by text content."""

    return files.search_files(
        root=root,
        name_patterns=name_patterns,
        content_query=content_query,
        limit=limit,
        max_file_bytes=max_file_bytes,
    )


@tool(DESTRUCTIVE)
def write_file(
    path: str,
    content: str,
    mode: Literal["overwrite", "append"] = "overwrite",
    encoding: str = "utf-8",
    create_parents: bool = True,
) -> dict[str, Any]:
    """Use this to create, overwrite, or append text in an authorized local file."""

    return files.write_text(
        path=path,
        content=content,
        mode=mode,
        encoding=encoding,
        create_parents=create_parents,
    )


@tool(WRITE)
def make_directory(path: str, parents: bool = True) -> dict[str, Any]:
    """Use this to create a local directory or directory tree within authorized paths."""

    return files.make_dir(path, parents=parents)


@tool(DESTRUCTIVE, stall_after_seconds=90.0)
def copy_path(source: str, destination: str, overwrite: bool = False) -> dict[str, Any]:
    """Use this to copy a local file or directory, optionally replacing the destination."""

    return files.copy_path(source=source, destination=destination, overwrite=overwrite)


@tool(DESTRUCTIVE, stall_after_seconds=90.0)
def move_path(source: str, destination: str, overwrite: bool = False) -> dict[str, Any]:
    """Use this to move or rename a local path, optionally replacing the destination."""

    return files.move_path(source=source, destination=destination, overwrite=overwrite)


@tool(DESTRUCTIVE, stall_after_seconds=90.0)
def delete_path(path: str, permanent: bool = False) -> dict[str, Any]:
    """Use this to delete a local path; recycle-bin deletion is the safer default."""

    return files.delete_path(path=path, permanent=permanent)


@tool(READ)
def list_processes(name_filter: str | None = None, limit: int = 300) -> dict[str, Any]:
    """Use this to inspect running local processes and identify executable process IDs."""

    return processes.list_processes(name_filter=name_filter, limit=limit)


@tool(WRITE)
def start_process(
    executable: str,
    args: list[str] | None = None,
    cwd: str | None = None,
    hidden: bool = False,
) -> dict[str, Any]:
    """Use this to start one local executable directly without invoking a command shell."""

    return processes.start_process(executable=executable, args=args, cwd=cwd, hidden=hidden)


@tool(DESTRUCTIVE, stall_after_seconds=30.0)
def terminate_process(
    pid: int,
    tree: bool = True,
    force: bool = False,
    timeout: float = 5.0,
) -> dict[str, Any]:
    """Use this to terminate a known process ID and optionally its child process tree."""

    return processes.terminate_process(pid=pid, tree=tree, force=force, timeout=timeout)


@tool(DESTRUCTIVE, stall_after_seconds=10.0)
def run_shell(
    command: str,
    cwd: str | None = None,
    shell: Literal["powershell", "cmd"] = "powershell",
    timeout_seconds: int = 120,
) -> dict[str, Any]:
    """Run shell work with a fast return; long commands detach and return a job_id for polling."""

    return processes.run_shell(command=command, cwd=cwd, shell=shell, timeout_seconds=timeout_seconds)


@tool(READ, stall_after_seconds=10.0)
def get_shell_job(job_id: str, max_chars: int = 12000) -> dict[str, Any]:
    """Poll one detached shell job and return its current state plus bounded output tails."""

    return processes.get_shell_job(job_id=job_id, max_chars=max_chars)


@tool(READ, stall_after_seconds=10.0)
def list_shell_jobs(limit: int = 20) -> dict[str, Any]:
    """Recover recent detached shell job IDs and states after a client interruption."""

    return processes.list_shell_jobs(limit=limit)


@tool(DESTRUCTIVE, stall_after_seconds=10.0)
def cancel_shell_job(job_id: str) -> dict[str, Any]:
    """Cancel one detached shell job and its owned process tree."""

    return processes.cancel_shell_job(job_id=job_id)


@tool(WRITE)
def open_path(path: str) -> dict[str, Any]:
    """Use this to open a local file or directory in its Windows default application."""

    return processes.open_path(path)


@tool(READ, stall_after_seconds=10.0)
def git_snapshot(path: str, recent_commits: int = 5) -> dict[str, Any]:
    """Use this to inspect branch, HEAD, working tree status, and recent commits of a local Git repo."""

    return processes.git_snapshot(path=path, recent_commits=recent_commits)


def main() -> None:
    """Run the MCP server over stdio for OpenAI Secure MCP Tunnel."""

    computer.enable_dpi_awareness()
    emit_server_start()
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
