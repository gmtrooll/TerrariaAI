"""CPU profile of a running instance with dotnet-trace (sampling), summarized per method.

    python -m terraria_mcp.profile --mode server --bots 16 --seconds 8 --root "Terraria.Player.Update"

Launches an instance, runs the `bench` command long enough to cover the trace, samples the game thread, and prints
the methods with the most inclusive and exclusive time, optionally only inside the subtree of a root method.
"""

import argparse
import json
import subprocess
import threading
from collections import Counter
from datetime import datetime
from pathlib import Path

from . import config
from .game import GameManager

DOTNET_TRACE = Path(config.PERSONAL_SAVE_DIR.parent / "tml-reference" / "tools" / "dotnet-trace.exe")
PROFILES_DIR = config.MOD_SOURCE / "results" / "profiles"


def collect(pid: int, seconds: int, out: Path) -> Path:
    """Sample a process for N seconds; returns the speedscope JSON path."""
    out.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run([str(DOTNET_TRACE), "collect", "-p", str(pid), "--profile", "cpu-sampling",
                    "--duration", f"00:00:{seconds:02d}", "--format", "Speedscope", "-o", str(out)],
                   check=True, capture_output=True, text=True)
    candidates = sorted(out.parent.glob(out.stem + "*.speedscope.json"), key=lambda p: p.stat().st_mtime)
    if not candidates:
        raise FileNotFoundError(f"no speedscope output next to {out}")
    return candidates[-1]


def summarize(speedscope_path: Path, root: str = None, top: int = 40) -> dict:
    """Inclusive/exclusive time per method on the busiest thread, from dotnet-trace's evented speedscope output
    (open/close events with timestamps). With root, only time spent while a frame whose name contains root is on
    the stack is counted, and shares are relative to that subtree."""
    data = json.loads(speedscope_path.read_text(encoding="utf-8"))
    names = [_short(f["name"]) for f in data["shared"]["frames"]]
    profile = max(data["profiles"], key=lambda p: len(p.get("events", [])))  # the game thread

    inclusive, exclusive, callers = Counter(), Counter(), Counter()
    on_stack = Counter()  # frame -> times currently on the stack (recursion counted once)
    stack = []  # (frame, opened_at)
    root_depth = 0  # how many root frames are on the stack
    total = thread_total = 0.0
    last = None
    for event in profile["events"]:
        at = event["at"]
        if stack and last is not None:
            elapsed = at - last
            thread_total += elapsed
            if root is None or root_depth:
                exclusive[stack[-1][0]] += elapsed
                total += elapsed
        last = at
        frame = event["frame"]
        if event["type"] == "O":
            stack.append((frame, at))
            on_stack[frame] += 1
            if root is not None and root in names[frame]:
                root_depth += 1
        else:
            while stack:
                top_frame, opened = stack.pop()
                on_stack[top_frame] -= 1
                if (root is None or root_depth) and on_stack[top_frame] == 0:
                    inclusive[top_frame] += at - opened
                if root is not None and root in names[top_frame]:
                    root_depth -= 1
                    if root_depth == 0 and stack:
                        callers[stack[-1][0]] += at - opened
                if top_frame == frame:
                    break

    def table(counter):
        merged = Counter()
        for frame, ms in counter.items():
            merged[names[frame]] += ms
        return [{"pct": round(100 * ms / total, 1), "ms": round(ms, 1), "method": name}
                for name, ms in merged.most_common(top)]

    return {"thread": profile.get("name"), "root": root, "seconds_counted": round(total / 1000, 2),
            "share_of_thread_pct": round(100 * total / max(1e-9, thread_total), 1),
            "inclusive": table(inclusive), "exclusive": table(exclusive), "callers": table(callers)}


def _short(name: str) -> str:
    """Drop argument lists and module prefixes so methods fit on one line."""
    name = name.split("!", 1)[-1]
    return name.split("(", 1)[0]


def profile_bench(mode="server", bots=16, seconds=8, root=None, world=config.DEFAULT_WORLD, top=40, extra_args=None,
                  games=None):
    games = games or GameManager()
    name = f"profile_{mode}"
    games.launch(mode, world=world, bots=bots, name=name, extra_args=extra_args)
    try:
        instance = games.get(name)
        status = games.call(name, "execute", code="System.Environment.ProcessId")
        pid = status["result"]
        # Enough ticks to outlast the trace even at high speed. The reply never comes: the instance is stopped
        # mid-run, so the connection error is expected and ignored.
        def run_bench():
            try:
                games.call(name, "bench", timeout=600, ticks=2_000_000, warmup=60)
            except (ConnectionError, OSError):
                pass

        bench = threading.Thread(target=run_bench, daemon=True)
        bench.start()
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        out = collect(pid, seconds, PROFILES_DIR / f"{mode}_{bots}bots_{stamp}.nettrace")
        report = summarize(out, root=root, top=top)
        report["file"] = str(out)
        report["instance_pid"] = pid
        return report
    finally:
        games.stop(name)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--mode", default="server", choices=["server", "client"])
    parser.add_argument("--bots", type=int, default=16)
    parser.add_argument("--seconds", type=int, default=8)
    parser.add_argument("--root", default=None, help="only count stacks through this method (substring match)")
    parser.add_argument("--top", type=int, default=40)
    parser.add_argument("--extra-arg", action="append", default=[], help="game launch argument (--extra-arg=-botlean)")
    parser.add_argument("--file", default=None, help="summarize an existing .speedscope.json instead of collecting")
    args = parser.parse_args()
    if args.file:
        report = summarize(Path(args.file), root=args.root, top=args.top)
    else:
        report = profile_bench(args.mode, args.bots, args.seconds, args.root, top=args.top, extra_args=args.extra_arg)
    print(f"thread {report['thread']}, root {report['root']}: {report['share_of_thread_pct']}% of samples")
    for kind in ("inclusive", "exclusive", "callers"):
        if not report[kind]:
            continue
        print(f"\n--- {kind} ---")
        for row in report[kind]:
            print(f"{row['pct']:6.1f}%  {row['method']}")


if __name__ == "__main__":
    main()
