param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Version = '',
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $Version) { $Version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim() }
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
    throw 'Versione non valida: usa una versione SemVer, ad esempio 0.4.0 o 0.4.0-beta.1.'
}
$output = Join-Path $root "artifacts/windows-$($Runtime.Substring(4))"
$stage = Join-Path $root ('artifacts/.windows-package-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $stage 'publish'
$backup = Join-Path $stage 'previous'
$suffix = if ($FrameworkDependent) { '-framework-dependent' } else { '' }
$archive = Join-Path $root "artifacts/LettoreFatture-$Runtime$suffix.zip"
New-Item -ItemType Directory -Force $publish | Out-Null
try {
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    & dotnet publish (Join-Path $root 'src/Fatture.Windows/Fatture.Windows.csproj') `
        -c Release -r $Runtime --self-contained $selfContained -m:1 -nodeReuse:false `
        "-p:Version=$Version" -p:PublishSingleFile=false -p:EnableCompressionInSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=false -p:PublishTrimmed=false `
        -p:UseSharedCompilation=false -p:DebugType=none -p:DebugSymbols=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Compilazione Windows non riuscita.' }
    # The referenced console project contributes a config not used by this host.
    foreach ($unused in @('fatture.exe', 'fatture.deps.json', 'fatture.runtimeconfig.json')) {
        Remove-Item (Join-Path $publish $unused) -ErrorAction SilentlyContinue
    }
    $licenses = Join-Path $publish 'Licenses'
    New-Item -ItemType Directory $licenses | Out-Null
    Copy-Item (Join-Path $root 'packaging/licenses/*.txt') $licenses
    if (-not $FrameworkDependent) {
        $nuget = (& dotnet msbuild (Join-Path $root 'src/Fatture.Windows/Fatture.Windows.csproj') -nologo -getProperty:NuGetPackageRoot).Trim()
        foreach ($pack in @('microsoft.netcore.app.runtime', 'microsoft.windowsdesktop.app.runtime')) {
            $runtimePackage = Join-Path $nuget "$pack.$Runtime/10.0.13"
            $prefix = if ($pack -like '*windowsdesktop*') { 'DotnetDesktop' } else { 'Dotnet' }
            $license = if (Test-Path (Join-Path $runtimePackage 'LICENSE.TXT')) { 'LICENSE.TXT' } else { 'LICENSE' }
            Copy-Item (Join-Path $runtimePackage $license) (Join-Path $licenses "$prefix-LICENSE.txt")
            $notices = Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT'
            if (Test-Path $notices) { Copy-Item $notices (Join-Path $licenses "$prefix-THIRD-PARTY-NOTICES.txt") }
        }
    }
    Copy-Item (Join-Path $PSScriptRoot 'register.ps1') $publish
    @'
Lettore Fatture per Windows

Mantieni insieme tutti i file della cartella. Avvia LettoreFatture.exe
e trascina XML, P7M, EML o PDF nella finestra.
Ctrl+O apre documenti; Ctrl+S salva il PDF; Ctrl+Maiusc+E esporta le fatture in Excel.
Ctrl+clic/Maiusc+clic selezionano più documenti. X, Canc e Ctrl+W li chiudono.
Il pannello Informazioni parte chiuso.

Per Apri con: colloca prima la cartella in una posizione permanente, poi usa
File > Associa estensioni. Windows permette di scegliere l'app predefinita
nelle Impostazioni. Non servono privilegi di amministratore.
Per rimuovere la registrazione: avvia register.ps1 -Remove da PowerShell.

L'anteprima usa il motore PDF di Windows. Per selezionare/cercare testo usa
Vista > Apri PDF nel lettore di sistema. I PDF salvati mantengono il testo originale.
Tutto avviene localmente. I PDF generati dalle fatture sono copie di cortesia.
'@ | Set-Content (Join-Path $publish 'Leggimi.txt') -Encoding utf8
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $stage 'app.zip') -CompressionLevel Optimal
    if (Test-Path $output) { Move-Item $output $backup }
    try { Move-Item $publish $output }
    catch { if (Test-Path $backup) { Move-Item $backup $output }; throw }
    Move-Item (Join-Path $stage 'app.zip') $archive -Force
    Write-Host "App: $output"
    Write-Host "Archivio: $archive"
} finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
