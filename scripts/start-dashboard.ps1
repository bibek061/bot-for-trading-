param([int]$Port = 5080)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repository
try {
    $env:Dashboard__Port = $Port.ToString()
    Write-Host "Starting the local paper dashboard at http://127.0.0.1:$Port"
    Write-Host 'The server prints the location of your local access key. Keep this terminal running.'
    dotnet run --project src/AutopilotQuant.Web --no-launch-profile
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard stopped with an error.' }
} finally { Pop-Location }
