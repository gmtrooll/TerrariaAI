"""Client for the mod's automation bridge: line-delimited JSON over local TCP (see Common/Automation/BridgeServer.cs).

Requests can overlap: a long `bench` or `wait_until` does not block a `status` or `quit` sent meanwhile. A reader
thread matches replies to requests by id.
"""

import itertools
import json
import socket
import threading


class BridgeError(RuntimeError):
    """The game answered with ok=false."""


class _Pending:
    __slots__ = ("event", "reply")

    def __init__(self):
        self.event = threading.Event()
        self.reply = None


class BridgeClient:
    def __init__(self, port: int, host: str = "127.0.0.1", connect_timeout: float = 2.0):
        self.port = port
        self.host = host
        self.connect_timeout = connect_timeout
        self._sock = None
        self._ids = itertools.count(1)
        self._lock = threading.Lock()  # guards connect, send and the pending table
        self._pending: dict[int, _Pending] = {}

    def connect(self) -> None:
        self.close()
        sock = socket.create_connection((self.host, self.port), timeout=self.connect_timeout)
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        sock.settimeout(None)
        self._sock = sock
        threading.Thread(target=self._read_loop, args=(sock,), daemon=True).start()

    def close(self) -> None:
        sock, self._sock = self._sock, None
        if sock is not None:
            try:
                sock.close()
            except OSError:
                pass

    @property
    def connected(self) -> bool:
        return self._sock is not None

    def _read_loop(self, sock: socket.socket) -> None:
        try:
            with sock.makefile("rb") as stream:
                for line in stream:
                    try:
                        reply = json.loads(line)
                    except ValueError:
                        continue
                    with self._lock:
                        pending = self._pending.pop(reply.get("id"), None)
                    if pending is not None:
                        pending.reply = reply
                        pending.event.set()
        except OSError:
            pass
        finally:
            # Wake every waiter on this connection; they see no reply and raise.
            with self._lock:
                if self._sock is sock:
                    self._sock = None
                waiting, self._pending = self._pending, {}
            for pending in waiting.values():
                pending.event.set()

    def call(self, cmd: str, timeout: float = 30.0, **args):
        """Send one command and wait for its reply. Commands that wait on the game (step, wait_until, bench) need a
        timeout longer than their own."""
        pending = _Pending()
        with self._lock:
            if self._sock is None:
                self.connect()
            request_id = next(self._ids)
            self._pending[request_id] = pending
            message = (json.dumps({"id": request_id, "cmd": cmd, "args": args}) + "\n").encode("utf-8")
            try:
                self._sock.sendall(message)
            except OSError:
                self._pending.pop(request_id, None)
                self.close()
                raise
        if not pending.event.wait(timeout):
            with self._lock:
                self._pending.pop(request_id, None)
            raise TimeoutError(f"no reply to '{cmd}' within {timeout} s")
        reply = pending.reply
        if reply is None:
            raise ConnectionError("the game closed the bridge connection")
        if not reply.get("ok"):
            raise BridgeError(reply.get("error", "unknown error"))
        return reply.get("result")
