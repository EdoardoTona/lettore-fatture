#!/usr/bin/env python3
"""Validate a release tag against the version declared by the checked-out code."""
from pathlib import Path
import re
import sys

tag = sys.argv[1] if len(sys.argv) == 2 else ''
if not re.fullmatch(r'v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?', tag):
    raise SystemExit('Tag non valido: usa vMAJOR.MINOR.PATCH, eventualmente con suffisso -beta.1.')
version = tag[1:]
declared = (Path(__file__).resolve().parents[1] / 'VERSION').read_text().strip()
if version != declared:
    raise SystemExit(f'Il tag {tag} non corrisponde a VERSION ({declared}). Aggiorna VERSION prima di creare il tag.')
print(version)
