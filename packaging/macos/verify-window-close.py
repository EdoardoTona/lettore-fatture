#!/usr/bin/env python3
"""Close a real app window and check that its C# worker exits with the app."""
import os
from pathlib import Path
import subprocess
import time

root = Path(__file__).resolve().parents[2]
app = root / "artifacts/macos/Lettore Fatture.app"
executable = app / "Contents/MacOS/LettoreFatture"
worker = app / "Contents/Resources/bridge/fatture"


def processes():
    output = subprocess.check_output(["ps", "-axo", "pid=,ppid=,comm="], text=True)
    return [line.strip().split(None, 2) for line in output.splitlines() if line.strip()]


for mode in ("ready", "loading"):
    baseline = {int(pid) for pid, _, command in processes() if command == str(worker)}
    environment = dict(os.environ, FATTURE_SMOKE_INPUT=str(root / "esempi/IT04270090261_0002I.xml"),
                       FATTURE_SMOKE_WINDOW_CLOSE=mode)
    process = subprocess.Popen([str(executable), "--smoke-test"], env=environment,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    children = set()
    try:
        deadline = time.monotonic() + 12
        while process.poll() is None and time.monotonic() < deadline:
            children.update(int(pid) for pid, parent, command in processes()
                            if int(parent) == process.pid and command == str(worker))
            time.sleep(0.02)
        stdout, stderr = process.communicate(timeout=1)
        assert process.returncode == 0, stdout + stderr
        remaining = processes()
        worker_pids = {int(pid) for pid, _, command in remaining if command == str(worker)}
        assert not (worker_pids - baseline), "Un processo C# è rimasto attivo dopo la chiusura"
        assert not (children & {int(pid) for pid, _, _ in remaining}), "Worker non terminato"
        if mode == "ready":
            assert children, "Il test non ha osservato il core attivo prima della chiusura"
        print(f"Chiusura finestra ({mode}): app e core terminati")
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
