#!/usr/bin/env python3
"""Set bundle and extension versions from the same release version as .NET."""
import os
from pathlib import Path
import plistlib
import re
import sys

version = os.environ['FATTURE_VERSION']
if not re.fullmatch(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?', version):
    raise SystemExit('FATTURE_VERSION deve essere una versione SemVer.')
bundle_version = version.split('-')[0]
build = os.environ.get('FATTURE_BUILD_NUMBER', bundle_version)
if not re.fullmatch(r'\d+(\.\d+){0,2}', build):
    raise SystemExit('FATTURE_BUILD_NUMBER deve contenere da uno a tre numeri separati da punti.')
for name in sys.argv[1:]:
    path = Path(name)
    values = plistlib.loads(path.read_bytes())
    values['CFBundleShortVersionString'] = bundle_version
    values['CFBundleVersion'] = build
    path.write_bytes(plistlib.dumps(values, sort_keys=False))
