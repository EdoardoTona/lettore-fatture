param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Version = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $Version) { $Version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim() }
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
    throw 'Versione SemVer non valida.'
}
$publish = Join-Path $root "artifacts/windows-$($Runtime.Substring(4))"
$destination = Join-Path $root "artifacts/releases/$Runtime"
$stage = Join-Path $root ('artifacts/.velopack-' + [guid]::NewGuid().ToString('N'))
Push-Location $root
try {
    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'package.ps1') -Runtime $Runtime -Version $Version }
    if (-not (Test-Path (Join-Path $publish 'LettoreFatture.exe'))) { throw 'App Windows non trovata.' }
    $runtimeConfig = Get-Content (Join-Path $publish 'LettoreFatture.runtimeconfig.json') -Raw | ConvertFrom-Json
    if (-not $runtimeConfig.runtimeOptions.includedFrameworks) { throw "L'installer richiede la build self-contained." }
    $dependencies = Get-Content (Join-Path $publish 'LettoreFatture.deps.json') -Raw | ConvertFrom-Json
    if (-not $dependencies.libraries.PSObject.Properties["LettoreFatture/$Version"]) {
        throw 'La versione pubblicata non corrisponde a quella richiesta. Ricompila senza -SkipBuild.'
    }
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Ripristino del tool Velopack non riuscito.' }
    New-Item -ItemType Directory -Force $stage | Out-Null
    & dotnet tool run vpk -- '[win]' pack --packId LettoreFatture --packTitle 'Lettore Fatture' `
        --packAuthors 'Edoardo Tona' --packVersion $Version --packDir $publish --mainExe LettoreFatture.exe `
        --runtime $Runtime --channel $Runtime --icon (Join-Path $root 'src/Fatture.Windows/Assets/App.ico') `
        --shortcuts StartMenuRoot --noPortable --delta None --outputDir $stage --skip-updates --yes
    if ($LASTEXITCODE -ne 0) { throw 'Creazione installer Velopack non riuscita.' }
    $setups = @(Get-ChildItem $stage -Filter '*Setup.exe')
    if ($setups.Count -ne 1) { throw 'Installer Setup.exe mancante o ambiguo.' }
    $setupName = "LettoreFatture-$Runtime-Setup.exe"
    if ($setups[0].Name -ne $setupName) { Rename-Item $setups[0].FullName $setupName }
    Copy-Item (Join-Path $root "artifacts/LettoreFatture-$Runtime.zip") $stage
    $backup = Join-Path $root ('artifacts/.velopack-previous-' + [guid]::NewGuid().ToString('N'))
    if (Test-Path $destination) { Move-Item $destination $backup }
    try { New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null; Move-Item $stage $destination }
    catch { if (Test-Path $backup) { Move-Item $backup $destination }; throw }
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    Write-Host "Installer: $(Join-Path $destination $setupName)"
} finally {
    Pop-Location
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
}
