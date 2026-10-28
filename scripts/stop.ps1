param([switch]$Quiet)

$pidFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'deploy\app.pid'
if (Test-Path $pidFile) {
    $id = [int](Get-Content $pidFile)
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -eq 'dotnet') {
        Stop-Process -Id $id -Force
        $p.WaitForExit(5000) | Out-Null
        if (-not $Quiet) { Write-Host "Stopped Moratorium Management (PID $id)." }
    }
    Remove-Item $pidFile -Force
} elseif (-not $Quiet) {
    Write-Host 'Not running.'
}
