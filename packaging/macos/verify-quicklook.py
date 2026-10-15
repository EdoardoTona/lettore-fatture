#!/usr/bin/env python3
"""Exercise the actual Quick Look extension, with a bundled AppKit host.

qlmanage is unbundled and can crash in EXConcreteExtension on macOS 27 before
requesting a preview. A proper host gives the system a valid bundle identity.
"""
import plistlib
import ctypes
import json
import os
import pty
import queue
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time

root = Path(__file__).resolve().parents[2]
app = root / "artifacts/macos/Lettore Fatture.app"
extension = app / "Contents/PlugIns/FatturePreview.appex"
subprocess.run(["pluginkit", "-a", str(extension)], check=True)
subprocess.run(["pluginkit", "-e", "use", "-i", "it.edoardotona.lettorefatture.preview"], check=True)
messages = queue.Queue()
with tempfile.TemporaryDirectory(prefix="ql-check-", dir=root / "artifacts") as temporary:
    directory = Path(temporary)
    # Also exercise the published NativeAOT library on every sample, including
    # EML. Finder can prefer Apple's Mail provider for that shared content type.
    core = ctypes.CDLL(str(extension / "Contents/Frameworks/FatturePreviewCore.dylib"))
    core.fatture_preview.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
    core.fatture_preview.restype = ctypes.c_int32
    sample_directory = root / "esempi" if (root / "esempi").is_dir() else root / "tests/fixtures"
    samples = sorted(p for p in sample_directory.iterdir() if p.suffix in {".xml", ".p7m", ".eml"})
    assert samples, "Mancano i documenti di esempio"
    for index, source in enumerate(samples):
        output = directory / "native" / str(index)
        assert core.fatture_preview(os.fsencode(source), os.fsencode(output)) == 0, source.name
        manifest = json.loads((output / "manifest.json").read_text())
        assert manifest["documents"] and not manifest["warnings"]
        for document in manifest["documents"]:
            assert Path(document["pdfPath"]).read_bytes().startswith(b"%PDF-")
    print(f"Motore NativeAOT pubblicato: {len(samples)} esempi XML/P7M/EML convertiti")
    contents = directory / "QuickLook Check.app/Contents"
    executable = contents / "MacOS/QuickLookHost"
    executable.parent.mkdir(parents=True)
    with (contents / "Info.plist").open("wb") as file:
        plistlib.dump({"CFBundleIdentifier": "it.edoardotona.lettorefatture.quicklookcheck",
                      "CFBundleName": "QuickLook Check", "CFBundleExecutable": "QuickLookHost",
                      "CFBundlePackageType": "APPL", "CFBundleVersion": "1", "NSPrincipalClass": "NSApplication"}, file)
    subprocess.run(["xcrun", "swiftc", "-parse-as-library", "-O", "-module-cache-path", "/tmp/lettore-fatture-swift-cache",
                    str(root / "packaging/macos/quicklook/verify-host.swift"), "-framework", "AppKit", "-framework", "QuickLookUI",
                    "-o", str(executable)], check=True)
    subprocess.run(["codesign", "--force", "--sign", "-", str(contents.parent)], check=True, capture_output=True)
    master, slave = pty.openpty()
    log = subprocess.Popen(["/usr/bin/log", "stream", "--style", "compact", "--level", "info", "--predicate",
                            'subsystem == "it.edoardotona.lettorefatture.preview"'], stdout=slave, stderr=slave)
    os.close(slave)
    def collect():
        try:
            with os.fdopen(master, encoding="utf-8", errors="replace") as output:
                for line in output:
                    messages.put(line)
        except OSError:
            pass
    reader = threading.Thread(target=collect, daemon=True)
    reader.start()
    try:
        time.sleep(0.5)
        ordinary = directory / "ordinary.xml"
        ordinary.write_text("<metadata><message>Non è una fattura</message></metadata>")
        cases = [(root / "tests/fixtures/invoice.xml", "PDF")]
        signed = next((sample for sample in samples if sample.suffix == ".p7m"), None)
        if signed: cases.append((signed, "PDF"))
        cases.append((ordinary, "XML text"))
        for index, (source, expected) in enumerate(cases):
            selected = directory / (str(index) + "-" + source.name)
            shutil.copy2(source, selected)
            while not messages.empty():
                messages.get_nowait()
            subprocess.run([str(executable), str(selected)], check=True, capture_output=True, text=True, timeout=10)
            observed = []
            deadline = time.monotonic() + 3
            while time.monotonic() < deadline:
                try:
                    observed.append(messages.get(timeout=0.1))
                except queue.Empty:
                    pass
                if any("Quick Look " + expected + " ready" in line for line in observed):
                    break
            assert any("Quick Look " + expected + " ready" in line for line in observed), "".join(observed)
            assert not any("generation failed" in line for line in observed), "".join(observed)
            print(f"Quick Look di sistema: {source.suffix}, {expected} generato")
    finally:
        log.terminate()
        log.wait(timeout=3)
