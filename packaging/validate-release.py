#!/usr/bin/env python3
"""Validate all four release payloads before publishing, then record checksums."""
import hashlib
import json
from pathlib import Path
import plistlib
import struct
import sys
from zipfile import ZipFile

directory = Path(sys.argv[1])
version = sys.argv[2]
expected = set()


def require(name):
    expected.add(name)
    path = directory / name
    if not path.is_file() or path.stat().st_size == 0:
        raise SystemExit(f'Asset mancante o vuoto: {name}')
    return path


def checksum(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def entry(archive, name):
    matches = [item for item in archive.namelist() if item.replace('\\', '/').split('/')[-1] == name]
    assert len(matches) == 1, f'{archive.filename}: {name} mancante o ambiguo'
    return archive.read(matches[0])


def check_pe(data, machine):
    assert data[:2] == b'MZ', 'Eseguibile Windows non valido'
    offset = struct.unpack_from('<I', data, 60)[0]
    assert data[offset:offset + 4] == b'PE\0\0'
    assert struct.unpack_from('<H', data, offset + 4)[0] == machine, 'Architettura Windows errata'
    assert struct.unpack_from('<H', data, offset + 24 + 68)[0] == 2, 'Sottosistema Windows non GUI'


for runtime, machine in [('win-x64', 0x8664), ('win-arm64', 0xAA64)]:
    setup = require(f'LettoreFatture-{runtime}-Setup.exe')
    with setup.open('rb') as stream:
        assert stream.read(2) == b'MZ', 'Installer Windows non valido'
    require(f'assets.{runtime}.json')
    require(f'RELEASES-{runtime}')
    feed = json.loads(require(f'releases.{runtime}.json').read_text())
    assert len(feed['Assets']) == 1, 'Atteso un solo pacchetto completo per architettura'
    asset = feed['Assets'][0]
    assert asset['PackageId'] == 'LettoreFatture' and asset['Version'] == version and asset['Type'] == 'Full'
    assert Path(asset['FileName']).name == asset['FileName']
    package = require(asset['FileName'])
    assert package.stat().st_size == asset['Size'] and checksum(package).upper() == asset['SHA256'].upper()
    for path in [package, require(f'LettoreFatture-{runtime}.zip')]:
        with ZipFile(path) as archive:
            assert archive.testzip() is None, f'Archivio danneggiato: {path.name}'
            check_pe(entry(archive, 'LettoreFatture.exe'), machine)
            entry(archive, 'LettoreFatture.dll')  # Unbundled: no runtime extraction at startup.
            entry(archive, 'Velopack.dll')
            config = json.loads(entry(archive, 'LettoreFatture.runtimeconfig.json'))
            frameworks = {item['name']: item['version'] for item in config['runtimeOptions']['includedFrameworks']}
            assert frameworks['Microsoft.NETCore.App'] == '10.0.13'
            assert frameworks['Microsoft.WindowsDesktop.App'] == '10.0.13'

for runtime, cpu in [('osx-arm64', 0x100000C), ('osx-x64', 0x1000007)]:
    with require(f'LettoreFatture-{runtime}.dmg').open('rb') as stream:
        stream.seek(-512, 2)
        assert stream.read(4) == b'koly', 'Immagine DMG non valida'
    with ZipFile(require(f'LettoreFatture-{runtime}.zip')) as archive:
        assert archive.testzip() is None
        root = 'Lettore Fatture.app/Contents/'
        extension = root + 'PlugIns/FatturePreview.appex/Contents/'
        app = plistlib.loads(archive.read(root + 'Info.plist'))
        preview = plistlib.loads(archive.read(extension + 'Info.plist'))
        assert app['CFBundleShortVersionString'] == preview['CFBundleShortVersionString'] == version.split('-')[0]
        assert app['CFBundleVersion'] == preview['CFBundleVersion']
        for executable in [root + 'MacOS/LettoreFatture', root + 'Resources/bridge/fatture',
                           extension + 'MacOS/FatturePreview', extension + 'Frameworks/FatturePreviewCore.dylib']:
            data = archive.read(executable)
            assert data[:4] == b'\xcf\xfa\xed\xfe' and struct.unpack_from('<I', data, 4)[0] == cpu, f'Architettura macOS errata: {executable}'

actual = {path.name for path in directory.iterdir() if path.is_file() and path.name != 'SHA256SUMS.txt'}
assert actual == expected, f'Asset inattesi: {actual - expected}'
(directory / 'SHA256SUMS.txt').write_text(''.join(f'{checksum(directory / name)}  {name}\n' for name in sorted(expected)))
print(f'{len(expected)} asset verificati per quattro architetture; SHA256SUMS.txt generato.')
