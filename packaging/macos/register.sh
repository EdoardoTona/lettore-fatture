#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP="${1:-$ROOT/artifacts/macos/Lettore Fatture.app}"
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP"
if [[ -d "$APP/Contents/PlugIns/FatturePreview.appex" ]]; then
    pluginkit -a "$APP/Contents/PlugIns/FatturePreview.appex"
    pluginkit -e use -i it.edoardotona.lettorefatture.preview
    echo 'Estensione Quick Look registrata e abilitata.'
fi
echo 'Registrata in Apri con. Per renderla predefinita: Finder → Ottieni informazioni → Apri con → Modifica tutto.'
