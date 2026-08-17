#!/usr/bin/env python3
"""
FILE PURPOSE

Purpose: serve the public dashboard shell as a local NGU Idle client and expose the
bot's confirmed telemetry to that shell through a small read-only HTTP API.

Mechanism: a loopback-only ThreadingHTTPServer serves the repository's docs/ tree
and builds /api/state from runtime/decision.json plus a filtered tail of
runtime/logs/actions.log.  The optional daemon mode detaches from the launcher and
records its own PID for exact lifecycle cleanup.  The public jehlp.net copy may
request the same endpoint; explicit CORS and Private Network Access headers permit
that hand-off when the browser allows it, while the "Open local client" fallback
stays same-origin.

Inputs and outputs: reads generated telemetry and action logs, returns JSON, and
serves static HTML/CSS/JavaScript.  It never imports the injector, calls a game
controller, writes the save, or accepts commands.  Malformed or stale telemetry is
reported honestly instead of reusing a prior frame.

Invariants and safety: bind only to 127.0.0.1; allow only GET/HEAD/OPTIONS; expose no
directory outside the configured docs root; do not return arbitrary files from the
runtime directory; and never turn an intended action into a confirmed event.

Extension points and non-goals: new presentation fields belong in docs/assets/app.js
and new telemetry belongs in the bot's decision schema.  This bridge may add derived
read-only summaries, but it is deliberately not a remote-control API or persistence
layer.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from datetime import datetime, timezone
from functools import partial
from http import HTTPStatus
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit


EVENT_LINE = re.compile(
    r"^(?P<clock>\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s+"
    r"\[(?P<category>[^\]]+)\]\s+"
    r"(?:\((?P<run>[^)]*)\)\s+)?(?P<message>.*)$"
)
EVIDENCE_SUFFIX = re.compile(r"\s+\[confirmed(?:[^\]]*)\]\s*$", re.IGNORECASE)
ALWAYS_INTERESTING = {
    "TITAN",
    "MILESTONE",
    "DISCOVERY",
    "REBIRTH",
    "CHALLENGE",
    "MACGUFFIN",
    "REWARD",
    "DEATH",
}


def iso_age_seconds(value: Any) -> float | None:
    """Return a truthful telemetry age, accepting the producer's ISO-8601 form."""
    if not isinstance(value, str) or not value.strip():
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return max(0.0, (datetime.now(timezone.utc) - parsed).total_seconds())
    except ValueError:
        return None


def is_key_event(category: str, message: str) -> bool:
    """Mirror the monitor's high-signal event policy without inventing state."""
    category = category.upper()
    lowered = message.lower()
    if category in ALWAYS_INTERESTING:
        return True
    if category == "BOSS":
        return "native controller victory" in lowered or "record fight boss" in lowered
    if category == "PURCHASE":
        return any(term in lowered for term in ("exp", "ap", "arbitrary point"))
    if category == "COLLECTION":
        return "maxxed" in lowered or "set complete" in lowered
    if category == "PROGRESSION":
        return any(term in lowered for term in ("confirmed", "completed", "unlocked", "consumed progression"))
    if category == "QUEST":
        return "completed" in lowered or "turned in" in lowered
    if category == "YGG":
        return any(term in lowered for term in ("harvest", "activated", "permanent"))
    if category == "LOOT":
        return any(term in lowered for term in ("ultra rare", "legendary", "macguffin", "heart"))
    return False


def tail_text(path: Path, limit: int = 1_000_000) -> str:
    """Read only the bounded tail needed for recent events, aligned to a full line."""
    if not path.is_file():
        return ""
    with path.open("rb") as stream:
        stream.seek(0, os.SEEK_END)
        size = stream.tell()
        start = max(0, size - limit)
        stream.seek(start)
        data = stream.read()
    if start:
        _, _, data = data.partition(b"\n")
    return data.decode("utf-8", errors="replace")


def recent_key_events(path: Path, maximum: int = 30) -> list[dict[str, str]]:
    events: list[dict[str, str]] = []
    for line in reversed(tail_text(path).splitlines()):
        match = EVENT_LINE.match(line.strip())
        if not match:
            continue
        category = match.group("category").upper()
        message = EVIDENCE_SUFFIX.sub("", match.group("message")).strip()
        if not is_key_event(category, message):
            continue
        events.append(
            {
                "clock": match.group("clock"),
                "run": match.group("run") or "",
                "category": category,
                "message": message,
            }
        )
        if len(events) >= maximum:
            break
    return events


class DashboardHandler(SimpleHTTPRequestHandler):
    """Static-file handler with two fixed JSON endpoints and no mutation verbs."""

    server_version = "NGUDashboard/1"

    def __init__(self, *args: Any, runtime_dir: Path, **kwargs: Any) -> None:
        self.runtime_dir = runtime_dir
        super().__init__(*args, **kwargs)

    def log_message(self, format: str, *args: Any) -> None:
        # Lifecycle scripts own operational logging; routine polling should stay quiet.
        return

    def end_headers(self) -> None:
        origin = self.headers.get("Origin", "")
        if self._allowed_origin(origin):
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Vary", "Origin")
        self.send_header("Access-Control-Allow-Private-Network", "true")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        super().end_headers()

    @staticmethod
    def _allowed_origin(origin: str) -> bool:
        if origin in {"https://jehlp.net", "https://www.jehlp.net"}:
            return True
        try:
            parsed = urlsplit(origin)
            return parsed.scheme == "http" and parsed.hostname in {"127.0.0.1", "localhost"}
        except ValueError:
            return False

    def do_OPTIONS(self) -> None:  # noqa: N802 - stdlib handler API
        self.send_response(HTTPStatus.NO_CONTENT)
        self.send_header("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.send_header("Access-Control-Max-Age", "600")
        self.end_headers()

    def do_GET(self) -> None:  # noqa: N802 - stdlib handler API
        path = urlsplit(self.path).path
        if path == "/api/health":
            self._send_json({"ok": True, "service": "ngu-dashboard", "schemaVersion": 1})
            return
        if path == "/api/state":
            self._send_state()
            return
        super().do_GET()

    def do_POST(self) -> None:  # noqa: N802 - explicit read-only boundary
        self.send_error(HTTPStatus.METHOD_NOT_ALLOWED, "This dashboard is read-only")

    do_PUT = do_POST
    do_PATCH = do_POST
    do_DELETE = do_POST

    def list_directory(self, path: str) -> None:
        self.send_error(HTTPStatus.NOT_FOUND)
        return None

    def _send_state(self) -> None:
        decision_path = self.runtime_dir / "decision.json"
        try:
            with decision_path.open("r", encoding="utf-8") as stream:
                state = json.load(stream)
        except FileNotFoundError:
            self._send_json(
                {"ok": False, "error": "Waiting for the bot's first confirmed snapshot."},
                HTTPStatus.SERVICE_UNAVAILABLE,
            )
            return
        except (OSError, json.JSONDecodeError) as error:
            self._send_json(
                {"ok": False, "error": f"Telemetry is temporarily unreadable: {error.__class__.__name__}."},
                HTTPStatus.SERVICE_UNAVAILABLE,
            )
            return

        timestamp = (
            state.get("snapshotUtc")
            or state.get("timestampUtc")
            or state.get("generatedAt")
            or state.get("timestamp")
            or state.get("time")
        )
        payload = {
            "ok": True,
            "schemaVersion": 1,
            "servedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "stateAgeSeconds": iso_age_seconds(timestamp),
            "state": state,
            "events": recent_key_events(self.runtime_dir / "logs" / "actions.log"),
        }
        self._send_json(payload)

    def _send_json(self, payload: dict[str, Any], status: HTTPStatus = HTTPStatus.OK) -> None:
        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)


def parse_args() -> argparse.Namespace:
    repository = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description="Serve the read-only NGU dashboard on loopback.")
    parser.add_argument("--root", type=Path, default=repository / "docs")
    parser.add_argument("--runtime", type=Path, default=repository / "runtime")
    parser.add_argument("--port", type=int, default=47635)
    parser.add_argument("--daemon", action="store_true", help="Detach and continue in the background.")
    parser.add_argument("--pid-file", type=Path, help="Write the detached server's PID here.")
    parser.add_argument("--log", type=Path, help="Append detached stdout and stderr here.")
    return parser.parse_args()


def daemonize(pid_file: Path, log_path: Path) -> None:
    """Double-fork into a new session and publish only the final server PID."""
    first = os.fork()
    if first > 0:
        os._exit(0)
    os.setsid()
    second = os.fork()
    if second > 0:
        os._exit(0)

    os.umask(0o077)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    null_input = os.open(os.devnull, os.O_RDONLY)
    log_output = os.open(log_path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
    os.dup2(null_input, sys.stdin.fileno())
    os.dup2(log_output, sys.stdout.fileno())
    os.dup2(log_output, sys.stderr.fileno())
    if null_input > 2:
        os.close(null_input)
    if log_output > 2:
        os.close(log_output)
    pid_file.parent.mkdir(parents=True, exist_ok=True)
    pid_file.write_text(f"{os.getpid()}\n", encoding="ascii")


def main() -> None:
    args = parse_args()
    if args.daemon:
        if args.pid_file is None or args.log is None:
            raise SystemExit("--daemon requires --pid-file and --log")
        daemonize(args.pid_file.resolve(), args.log.resolve())
    root = args.root.resolve(strict=True)
    runtime = args.runtime.resolve(strict=True)
    handler = partial(DashboardHandler, directory=str(root), runtime_dir=runtime)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), handler)
    server.daemon_threads = True
    print(f"NGU dashboard: http://127.0.0.1:{args.port}/", flush=True)
    try:
        server.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
