"""Build the mod, launch and stop tModLoader instances, generate worlds. Shared by the MCP server and the CLI."""

import json
import re
import shutil
import socket
import subprocess
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path

from . import config
from .bridge import BridgeClient, BridgeError

WORLD_SIZES = {"small": 1, "medium": 2, "large": 3}


@dataclass
class Instance:
    name: str
    mode: str  # "client" or "server"
    bridge_port: int
    world: str
    process: subprocess.Popen  # None when attached to a game this process did not launch
    log_path: Path
    args: list
    started: float = field(default_factory=time.time)
    bridge: BridgeClient = None
    launch_kwargs: dict = field(default_factory=dict)

    @property
    def running(self) -> bool:
        if self.process is not None:
            return self.process.poll() is None
        try:
            self.bridge.call("ping", timeout=2)
            return True
        except (OSError, ConnectionError, ValueError, BridgeError):
            return False


class GameManager:
    def __init__(self):
        self.instances: dict[str, Instance] = {}

    # ---------- build and install ----------

    def build(self) -> dict:
        """dotnet build into the staging save directory (works while games are running), then install into the
        training save directory if no running instance holds the old .tmod."""
        started = time.time()
        config.BUILD_STAGE_DIR.mkdir(parents=True, exist_ok=True)
        result = subprocess.run(
            # Release: a Debug assembly tells the JIT not to optimize, which made the mod's own loops ~10x slower.
            ["dotnet", "build", "-c", "Release", "-nologo", "-v", "q", "-clp:NoSummary",
             f'-p:ExtraBuildModFlags=-tmlsavedirectory "{config.BUILD_STAGE_DIR}"'],
            cwd=config.MOD_SOURCE, capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
        output = result.stdout + result.stderr
        lines = output.splitlines()
        # C# errors look like "file(line,col): error CS0103: ..."; tModLoader's own like "tModLoader : Mod Build error TML003: ...".
        errors = sorted({line.strip() for line in lines if ": error " in line or "Build error" in line})
        errors = [e for e in errors if "MSB3073" not in e] or errors  # MSB3073 only repeats the command line
        warnings = sorted({line.strip() for line in lines if ": warning " in line and "Common" in line})
        report = {
            "ok": result.returncode == 0,
            "seconds": round(time.time() - started, 1),
            "errors": [_short_path(e) for e in errors],
            "warnings": [_short_path(w) for w in warnings][:20],
        }
        if not report["ok"] and not errors:
            report["output"] = output[-4000:]
        if report["ok"]:
            try:
                report["installed"] = str(self.install())
            except PermissionError:
                report["installed"] = "not yet: a running instance holds the old build; relaunch to install"
        return report

    def install(self) -> Path:
        """Copy the staged .tmod into the training save directory (skipped when unchanged, since a running instance
        keeps its .tmod open) and make sure it is enabled: tModLoader drops it from enabled.json after a load error."""
        config.MODS_DIR.mkdir(parents=True, exist_ok=True)
        target = config.MODS_DIR / config.BUILD_OUTPUT.name
        if not target.exists() or target.read_bytes() != config.BUILD_OUTPUT.read_bytes():
            try:
                shutil.copy2(config.BUILD_OUTPUT, target)
            except PermissionError as e:
                running = [inst.name for inst in self.instances.values() if inst.running]
                raise PermissionError(f"{target} is in use (running instances: {running}); stop them to install "
                                      "a new build") from e
        (config.MODS_DIR / "enabled.json").write_text(json.dumps([config.MOD_NAME], indent=2))
        return target

    def prepare_client_config(self) -> None:
        """Settings an unattended client needs. Without a Language entry the game stops at the language picker;
        with ThrottleWhenInactive it slows down whenever another window has focus."""
        path = config.SAVE_DIR / "config.json"
        settings = json.loads(path.read_text(encoding="utf-8-sig")) if path.exists() else {}
        settings.update({
            "Language": "en-US",
            "ThrottleWhenInactive": False,
            "AutoPause": False,
            "SeenFirstLaunchModderWelcomeMessage": True,
            "ShowModMenuNotifications": False,
            "ShowNewUpdatedModsInfo": False,
        })
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(settings, indent=2), encoding="utf-8")

    # ---------- worlds ----------

    def world_path(self, world: str) -> Path:
        return config.WORLDS_DIR / f"{world}.wld"

    def list_worlds(self) -> list[str]:
        return sorted(p.stem for p in config.WORLDS_DIR.glob("*.wld"))

    def create_world(self, world: str, size: str = "small", seed: str = "42", timeout: float = 600) -> dict:
        """Generate a world with a temporary server (-autocreate), then shut it down."""
        if self.world_path(world).exists():
            return {"world": world, "created": False, "path": str(self.world_path(world))}
        started = time.time()
        name = f"worldgen_{world}"
        self.launch("server", world=world, name=name, create={"size": size, "seed": seed}, timeout=timeout)
        self.stop(name)
        return {"world": world, "created": True, "seconds": round(time.time() - started, 1), "path": str(self.world_path(world))}

    # ---------- instances ----------

    def free_bridge_port(self) -> int:
        used = {inst.bridge_port for inst in self.instances.values() if inst.running}
        port = config.FIRST_BRIDGE_PORT
        # Other processes (training runs) launch their own games on the same range, so also probe the OS.
        while port in used or not _port_free(port) or not _port_free(port + 10000):
            port += 1
        return port

    def launch(self, mode: str = "client", world: str = config.DEFAULT_WORLD, bots: int = 0, name: str = None,
               bridge_port: int = None, create: dict = None, extra_args: list = None, wait: bool = True,
               timeout: float = 240) -> dict:
        if mode not in ("client", "server"):
            raise ValueError("mode must be 'client' or 'server'")
        if create is None and not self.world_path(world).exists():
            raise FileNotFoundError(f"world '{world}' does not exist; known: {self.list_worlds()} (create_world makes one)")
        bridge_port = bridge_port or self.free_bridge_port()
        name = name or f"{mode}{bridge_port}"
        if name in self.instances and self.instances[name].running:
            raise RuntimeError(f"instance '{name}' is already running")
        # Always reinstall: tModLoader rewrites enabled.json without the mod after a load error.
        self.install()

        args = [str(config.DOTNET), "tModLoader.dll", "-nosteam", "-tmlsavedirectory", str(config.SAVE_DIR),
                "-bridgeport", str(bridge_port)]
        if bots:
            args += ["-autobots", str(bots)]
        if mode == "server":
            args += ["-server", "-modpath", str(config.MODS_DIR), "-world", str(self.world_path(world)),
                     "-port", str(bridge_port + 10000), "-maxplayers", "16"]
            if create:
                args += ["-autocreate", str(WORLD_SIZES[create.get("size", "small")]), "-worldname", world,
                         "-seed", str(create.get("seed", "42"))]
        else:
            self.prepare_client_config()
            args += ["-skipselect", f"{config.AUTOMATION_PLAYER}:{world}"]
        args += list(extra_args or [])

        config.LOGS_DIR.mkdir(parents=True, exist_ok=True)
        log_path = config.LOGS_DIR / f"{name}.log"
        process = subprocess.Popen(
            args, cwd=config.TML_DIR, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", bufsize=1,
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
        )
        threading.Thread(target=_copy_output, args=(process, log_path), daemon=True).start()
        instance = Instance(name, mode, bridge_port, world, process, log_path, args, bridge=BridgeClient(bridge_port),
                            launch_kwargs={"mode": mode, "world": world, "bots": bots, "name": name,
                                           "bridge_port": bridge_port, "extra_args": extra_args})
        self.instances[name] = instance

        info = {"name": name, "mode": mode, "world": world, "bridge_port": bridge_port, "pid": process.pid,
                "log": str(log_path)}
        if wait:
            info["ready"] = self.wait_ready(name, timeout=timeout)
        return info

    def attach_running(self, ports=range(config.FIRST_BRIDGE_PORT, config.FIRST_BRIDGE_PORT + 32)) -> list:
        """Register games that are already running (launched by an earlier session) by probing bridge ports."""
        known = {inst.bridge_port for inst in self.instances.values() if inst.running}
        attached = []
        for port in ports:
            if port in known:
                continue
            bridge = BridgeClient(port, connect_timeout=0.2)
            try:
                status = bridge.call("status", timeout=2)
            except (OSError, ConnectionError, ValueError, BridgeError):
                continue
            name = f"{status['side']}{port}"
            log_path = config.LOGS_DIR / f"{name}.log"
            self.instances[name] = Instance(name, status["side"], port, status.get("world") or "", None, log_path, [],
                                            bridge=bridge)
            attached.append(name)
        return attached

    def relaunch(self, name: str = None, timeout: float = 240) -> dict:
        """Stop an instance and start it again with the same settings (e.g. after build)."""
        instance = self.get(name)
        if not instance.launch_kwargs:
            raise RuntimeError(f"'{instance.name}' was attached, not launched here; stop it and launch a new one")
        self.stop(instance.name)
        return self.launch(**instance.launch_kwargs, timeout=timeout)

    def wait_ready(self, name: str, timeout: float = 240) -> dict:
        """Wait until the bridge answers and the instance is in its world."""
        instance = self.get(name)
        deadline = time.time() + timeout
        last_error = None
        while time.time() < deadline:
            if not instance.running:
                raise RuntimeError(f"'{name}' exited with code {instance.process.returncode}; last log lines:\n"
                                   + self.logs(name, tail=30))
            try:
                status = instance.bridge.call("status", timeout=5)
                if status["in_world"]:
                    status["seconds_to_ready"] = round(time.time() - instance.started, 1)
                    return status
            except (OSError, ConnectionError, ValueError, BridgeError) as e:
                last_error = e
            time.sleep(0.5)
        raise TimeoutError(f"'{name}' not in a world after {timeout:.0f} s (last error: {last_error}); last log lines:\n"
                           + self.logs(name, tail=30))

    def get(self, name: str = None) -> Instance:
        if name is None:
            running = [inst for inst in self.instances.values() if inst.running]
            if len(running) != 1:
                raise ValueError(f"name an instance; running: {[inst.name for inst in running]}")
            return running[0]
        if name not in self.instances:
            raise KeyError(f"no instance '{name}'; known: {list(self.instances)}")
        return self.instances[name]

    def call(self, name: str, cmd: str, timeout: float = 30, **args):
        return self.get(name).bridge.call(cmd, timeout=timeout, **args)

    def stop(self, name: str = None, save: bool = False, timeout: float = 30) -> dict:
        instance = self.get(name)
        how = "already exited"
        if instance.process is None:
            if instance.running:
                instance.bridge.call("quit", timeout=5, save=save)
                how = "quit (attached; exit not confirmed)"
            instance.bridge.close()
            return {"name": instance.name, "stopped": how}
        if instance.running:
            try:
                instance.bridge.call("quit", timeout=5, save=save)
                instance.process.wait(timeout=timeout)
                how = "quit"
            except (OSError, ConnectionError, ValueError, BridgeError, subprocess.TimeoutExpired):
                instance.process.kill()
                instance.process.wait(timeout=10)
                how = "killed"
        instance.bridge.close()
        return {"name": instance.name, "stopped": how, "exit_code": instance.process.returncode}

    def stop_all(self) -> list:
        return [self.stop(name) for name, inst in list(self.instances.items()) if inst.running]

    def status(self) -> list:
        rows = []
        for inst in self.instances.values():
            row = {"name": inst.name, "mode": inst.mode, "world": inst.world, "bridge_port": inst.bridge_port,
                   "running": inst.running, "pid": inst.process.pid if inst.process else None,
                   "uptime_s": round(time.time() - inst.started)}
            if inst.running:
                try:
                    row["game"] = inst.bridge.call("status", timeout=3)
                except (OSError, ConnectionError, ValueError, BridgeError) as e:
                    row["game"] = f"no answer: {e}"
            rows.append(row)
        return rows

    def logs(self, name: str = None, tail: int = 50, grep: str = None) -> str:
        path = self.get(name).log_path
        if not path.exists():
            return ""
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        lines = [line for line in lines if not _is_progress_line(line)]
        if grep:
            pattern = re.compile(grep, re.IGNORECASE)
            lines = [line for line in lines if pattern.search(line)]
        return "\n".join(lines[-tail:])


def _port_free(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        try:
            s.bind(("127.0.0.1", port))
            return True
        except OSError:
            return False


def _copy_output(process: subprocess.Popen, log_path: Path) -> None:
    started = time.time()
    with open(log_path, "w", encoding="utf-8") as log:
        for line in process.stdout:
            log.write(f"[{time.time() - started:7.1f}] {line}")
            log.flush()


_PROGRESS = re.compile(r"^\[\s*[\d.]+\]\s*(\d+(\.\d+)?% - .*|.*: \d+%|)\s*$")


def _is_progress_line(line: str) -> bool:
    """World generation prints thousands of progress lines; logs() hides them."""
    return bool(_PROGRESS.match(line))


def _short_path(line: str) -> str:
    return line.replace(str(config.MOD_SOURCE) + "\\", "")
