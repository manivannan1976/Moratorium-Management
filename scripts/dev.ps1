<#
.SYNOPSIS  Development mode: API with hot reload on :5080 and Angular dev server on :4200 (proxying /api).
#>
$root = Split-Path -Parent $PSScriptRoot

Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$root\src\MoratoriumManagement.Api'; dotnet watch run --launch-profile http"
Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$root\web'; npx ng serve --proxy-config proxy.conf.json --open"
