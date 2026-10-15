param([string]$Executable = (Join-Path $PSScriptRoot '../../artifacts/windows-x64/LettoreFatture.exe'))
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path $Executable).Path
$directory = Join-Path ([IO.Path]::GetTempPath()) ('fatture-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $directory | Out-Null
$oldReport = $env:FATTURE_SMOKE_RESULT
$oldMode = $env:FATTURE_SMOKE_WINDOW_CLOSE
try {
    foreach ($mode in @('ready', 'loading')) {
        $env:FATTURE_SMOKE_RESULT = Join-Path $directory "$mode.json"
        $env:FATTURE_SMOKE_WINDOW_CLOSE = $mode
        $process = Start-Process -FilePath $executablePath -ArgumentList '--smoke-test' -PassThru
        if (-not $process.WaitForExit(45000)) {
            & taskkill /PID $process.Id /T /F | Out-Null
            throw "Timeout verifica $mode."
        }
        if ($process.ExitCode -ne 0) {
            if (Test-Path $env:FATTURE_SMOKE_RESULT) { Get-Content $env:FATTURE_SMOKE_RESULT | Write-Host }
            throw "Verifica $mode fallita (codice $($process.ExitCode))."
        }
        $result = Get-Content $env:FATTURE_SMOKE_RESULT -Raw | ConvertFrom-Json
        if (-not $result.ok) { throw $result.error }
        if ($result.workerId -and (Get-Process -Id $result.workerId -ErrorAction SilentlyContinue)) { throw 'Processo core ancora attivo.' }
        if (Test-Path $result.sessionDirectory) { throw 'PDF temporanei della sessione non rimossi.' }
        Write-Host "Windows $mode`: rendering, documenti e chiusura app/core OK."
    }
} finally {
    $env:FATTURE_SMOKE_RESULT = $oldReport
    $env:FATTURE_SMOKE_WINDOW_CLOSE = $oldMode
    Remove-Item $directory -Recurse -Force
}
