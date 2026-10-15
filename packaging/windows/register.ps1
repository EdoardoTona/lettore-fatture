param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'LettoreFatture.exe'
if (-not (Test-Path $executable)) {
    $executable = Join-Path $PSScriptRoot '../../artifacts/windows-x64/LettoreFatture.exe'
}
if (-not (Test-Path $executable)) { throw 'LettoreFatture.exe non trovato. Esegui prima package.ps1.' }
$operation = if ($Remove) { '--unregister' } else { '--register' }
$process = Start-Process -FilePath $executable -ArgumentList $operation -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Registrazione non riuscita (codice $($process.ExitCode))." }
if ($Remove) { Write-Host 'Registrazione di Lettore Fatture rimossa.' }
else { Write-Host 'Registrata in Apri con. Per i predefiniti: Impostazioni > App > App predefinite.' }
