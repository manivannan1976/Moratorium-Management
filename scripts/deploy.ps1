<#
.SYNOPSIS
  Builds the Angular UI and the .NET API, publishes them to .\deploy\app and starts the app locally.
  The API serves the UI, so everything runs on one origin: http://localhost:5080

.PARAMETER SkipTests   Skip the test run.
.PARAMETER NoStart     Build and publish only.
#>
param([switch]$SkipTests, [switch]$NoStart)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deploy = Join-Path $root 'deploy\app'
$url = 'http://localhost:5080'

function Step($t) { Write-Host "`n==> $t" -ForegroundColor Cyan }

& (Join-Path $PSScriptRoot 'stop.ps1') -Quiet

if (-not $SkipTests) {
    Step 'Running tests'
    dotnet test (Join-Path $root 'MoratoriumManagement.sln') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

Step 'Building Angular UI'
Push-Location (Join-Path $root 'web')
try {
    if (-not (Test-Path 'node_modules')) { npm ci; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' } }
    npx ng build --configuration production
    if ($LASTEXITCODE -ne 0) { throw 'Angular build failed.' }
} finally { Pop-Location }

Step 'Publishing API'
# Keep the SQLite database across redeploys.
$storage = Join-Path $deploy 'storage'
$keep = Join-Path $root 'deploy\storage.bak'
if (Test-Path $storage) { Remove-Item $keep -Recurse -Force -ErrorAction SilentlyContinue; Move-Item $storage $keep }
if (Test-Path $deploy) { Remove-Item $deploy -Recurse -Force }
dotnet publish (Join-Path $root 'src\MoratoriumManagement.Api\MoratoriumManagement.Api.csproj') -c Release -o $deploy --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
if (Test-Path $keep) { Move-Item $keep $storage }

Step 'Copying UI into wwwroot'
$wwwroot = Join-Path $deploy 'wwwroot'
New-Item -ItemType Directory -Force $wwwroot | Out-Null
Copy-Item (Join-Path $root 'web\dist\web\browser\*') $wwwroot -Recurse -Force

if ($NoStart) { Write-Host "`nPublished to $deploy" -ForegroundColor Green; return }

Step 'Starting application'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:ASPNETCORE_URLS = $url
$log = Join-Path $root 'deploy\app.log'
$proc = Start-Process -FilePath 'dotnet' -ArgumentList 'MoratoriumManagement.Api.dll' -WorkingDirectory $deploy `
    -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError (Join-Path $root 'deploy\app.err.log')
$proc.Id | Set-Content (Join-Path $root 'deploy\app.pid')

$ok = $false
foreach ($i in 1..40) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { break }
    try { if ((Invoke-RestMethod "$url/api/health").status -eq 'Healthy') { $ok = $true; break } } catch { }
}
if (-not $ok) {
    Write-Host (Get-Content (Join-Path $root 'deploy\app.err.log') -Raw -ErrorAction SilentlyContinue)
    throw "Application did not become healthy. See deploy\app.log and deploy\app.err.log."
}

Write-Host "`nMoratorium Management is running (PID $($proc.Id))" -ForegroundColor Green
Write-Host "  App:      $url"
Write-Host "  Swagger:  $url/swagger"
Write-Host "  Admin:    admin@portal.local / Admin@12345"
Write-Host "  Viewer:   viewer@portal.local / Viewer@12345"
Write-Host "  Stop with scripts\stop.ps1"
