param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'AutopilotQuantWebTests'))
$testDirectory = Join-Path $testRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$url = "http://127.0.0.1:$port"
$dashboardHeaders = @{ 'X-Dashboard-Key' = 'smoke-dashboard-only-0123456789-abcdef' }
$adapterHeaders = @{ 'X-Adapter-Key' = 'smoke-adapter-only-0123456789-abcdef' }
$dll = Join-Path $repository "src/AutopilotQuant.Web/bin/$Configuration/net10.0/AutopilotQuant.Web.dll"
if (!(Test-Path -LiteralPath $dll)) { throw 'Build the web project before running this smoke test.' }

function Start-TestServer([bool]$WithAdapter) {
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.ArgumentList.Add($dll)
    $info.WorkingDirectory = Join-Path $repository 'src/AutopilotQuant.Web'
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Environment['Dashboard__Port'] = $port.ToString()
    $info.Environment['Dashboard__DataDirectory'] = $testDirectory
    $info.Environment['Dashboard__AccessKey'] = $dashboardHeaders['X-Dashboard-Key']
    $info.Environment['MarketData__AdapterKey'] = $(if ($WithAdapter) { $adapterHeaders['X-Adapter-Key'] } else { '' })
    $info.Environment['MarketData__T4__ApiKey'] = ''
    $info.Environment['Logging__LogLevel__Default'] = 'Warning'
    $process = [Diagnostics.Process]::Start($info)
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($process.HasExited) { throw "Test server failed: $($process.StandardError.ReadToEnd())" }
        try { $response = Invoke-WebRequest "$url/" -TimeoutSec 1 -SkipHttpErrorCheck; if ($response.StatusCode -eq 200) { return $process } } catch { }
        Start-Sleep -Milliseconds 100
    }
    $process.Kill($true)
    throw 'Test server startup timed out.'
}
function Check([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
    Write-Host "PASS: $Message"
}
$server = $null
try {
    $server = Start-TestServer $false
    $homePage = Invoke-WebRequest "$url/"
    $csp = $homePage.Headers['Content-Security-Policy'] -join ' '
    Check ($csp -match "script-src 'self';" -and $csp -match "connect-src 'self';" -and $csp -match "frame-src https://www.tradingview-widget.com;" -and $csp -match "frame-ancestors 'none';" -and $csp -notmatch 'unsafe-inline|unsafe-eval') 'TradingView is isolated to a fixed external frame while dashboard scripts and connections stay local'
    Check ($homePage.Content -match 'tradingview-reference.js' -and $homePage.Content -notmatch '<script[^>]+src="https?://' -and !(($homePage.Content).Contains($dashboardHeaders['X-Dashboard-Key']))) 'Dashboard assets contain no remote executable scripts or operator key'
    Check ((Invoke-WebRequest "$url/tradingview-reference.js" -SkipHttpErrorCheck).StatusCode -eq 200) 'TradingView integration asset is served locally'
    Check ((Invoke-WebRequest "$url/api/state" -Headers (@{'Origin'='https://www.tradingview-widget.com'} + $dashboardHeaders) -SkipHttpErrorCheck).StatusCode -eq 403) 'TradingView frame origin cannot access local account API even with an operator header'
    Check ((Invoke-WebRequest "$url/api/state" -SkipHttpErrorCheck).StatusCode -eq 401) 'Account API requires authentication'
    Check ((Invoke-WebRequest "$url/api/state" -Headers $adapterHeaders -SkipHttpErrorCheck).StatusCode -eq 401) 'Adapter key cannot read account API'
    Check ((Invoke-WebRequest "$url/api/feed/quotes" -Method Post -ContentType 'application/json' -Body '{}' -Headers $adapterHeaders -SkipHttpErrorCheck).StatusCode -eq 503) 'Feed disabled without configured adapter'
    Check ((Invoke-WebRequest "$url/api/control/pause" -Method Post -Headers (@{'Origin'='https://untrusted.example'} + $dashboardHeaders) -SkipHttpErrorCheck).StatusCode -eq 403) 'Cross-origin command rejected'
    Check ((Invoke-WebRequest "$url/api/state" -Headers (@{'Host'='untrusted.example'} + $dashboardHeaders) -SkipHttpErrorCheck).StatusCode -eq 403) 'DNS-rebinding host rejected'
    $initial = Invoke-RestMethod "$url/api/state" -Headers $dashboardHeaders
    Check ($initial.session.mode -eq 'PAPER' -and $initial.session.paused -and !$initial.provider.liveRoutingEnabled) 'Startup is paused paper mode with live routing disabled'
    Check ((Invoke-WebRequest "$url/api/control/resume" -Method Post -Headers $dashboardHeaders -SkipHttpErrorCheck).StatusCode -eq 409) 'Resume rejected without fresh data'
    $fixture = Join-Path $repository 'tests/AutopilotQuant.Tests/Fixtures/sample_mes_5m_synthetic.csv'
    $run = Invoke-RestMethod "$url/api/replays" -Method Post -ContentType 'text/csv' -InFile $fixture -Headers (@{'X-File-Name'='SYNTHETIC%20TEST%20ONLY'} + $dashboardHeaders)
    Check ($run.report.barsProcessed -gt 0 -and $run.name -eq 'SYNTHETIC TEST ONLY') 'Synthetic replay runs and filename is decoded'
    $after = Invoke-RestMethod "$url/api/state" -Headers $dashboardHeaders
    Check ($after.session.equity -eq $initial.session.equity -and $after.session.positions.Count -eq 0) 'Replay does not change forward account'
    $settings = $after.session.settings
    $settings.stopTicks = 24
    Invoke-RestMethod "$url/api/settings" -Method Put -ContentType 'application/json' -Body ($settings | ConvertTo-Json) -Headers $dashboardHeaders | Out-Null
    Check ((Invoke-WebRequest "$url/api/replays" -Method Post -ContentType 'text/csv' -Body 'bad csv' -Headers $dashboardHeaders -SkipHttpErrorCheck).StatusCode -eq 400) 'Malformed CSV returns a useful client error'
    $server.Kill($true); $server.WaitForExit(); $server.Dispose(); $server = $null
    $server = Start-TestServer $true
    $restored = Invoke-RestMethod "$url/api/state" -Headers $dashboardHeaders
    Check ($restored.session.settings.stopTicks -eq 24 -and $restored.session.paused) 'Settings survive restart and engine stays paused'
    $saved = Invoke-RestMethod "$url/api/replays/$($run.id)" -Headers $dashboardHeaders
    Check ($saved.id -eq $run.id) 'Replay report survives restart'
    Check ((Invoke-WebRequest "$url/api/market-data" -SkipHttpErrorCheck).StatusCode -eq 401) 'Market-data settings require dashboard authentication'
    $marketConfiguration = @{provider='external'; instruments=@(@{symbol='MES'; enabled=$true; exchangeId=''; productId=''; marketId='SYNTHETIC-API-TEST'}, @{symbol='MNQ'; enabled=$false; exchangeId=''; productId=''; marketId=''})}
    $configured = Invoke-RestMethod "$url/api/market-data" -Method Put -ContentType 'application/json' -Body ($marketConfiguration | ConvertTo-Json -Depth 5) -Headers $dashboardHeaders
    Check ($configured.configured -and $configured.adapterKeyPresent -and !$configured.t4ApiKeyPresent -and !$configured.connection.active) 'External contract configuration is ready without claiming a T4 connection'
    Check ((Invoke-WebRequest "$url/api/market-data" -Method Put -ContentType 'application/json' -Body '{"provider":"t4-live","instruments":[]}' -Headers $dashboardHeaders -SkipHttpErrorCheck).StatusCode -eq 400) 'Live provider configuration rejected'
    Check ((Invoke-WebRequest "$url/api/market-data" -Method Put -ContentType 'application/json' -Body '{"provider":"t4-simulator","instruments":[],"apiKey":"DO-NOT-SAVE"}' -Headers $dashboardHeaders -SkipHttpErrorCheck).StatusCode -eq 400) 'Browser-supplied provider credentials rejected'
    $quote = @{symbol='MES'; contractId='SYNTHETIC-API-TEST'; timestamp=[DateTimeOffset]::UtcNow.ToString('O'); bid=5000; ask=5000.25} | ConvertTo-Json
    Check ((Invoke-WebRequest "$url/api/feed/quotes" -Method Post -ContentType 'application/json' -Body $quote -Headers $dashboardHeaders -SkipHttpErrorCheck).StatusCode -eq 401) 'Dashboard key cannot inject market data'
    $withoutOffset = @{symbol='MES'; contractId='SYNTHETIC-API-TEST'; timestamp=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss'); bid=5000; ask=5000.25} | ConvertTo-Json
    Check ((Invoke-WebRequest "$url/api/feed/quotes" -Method Post -ContentType 'application/json' -Body $withoutOffset -Headers $adapterHeaders -SkipHttpErrorCheck).StatusCode -eq 400) 'Offset-free timestamp rejected at the API boundary'
    $fed = Invoke-RestMethod "$url/api/feed/quotes" -Method Post -ContentType 'application/json' -Body $quote -Headers $adapterHeaders
    Check ($fed.instruments[0].fresh -and $fed.paused) 'Authenticated adapter quote is visible without arming execution'
    Check ((Invoke-WebRequest "$url/api/feed/quotes" -Method Post -ContentType 'application/json' -Body $quote -Headers $adapterHeaders -SkipHttpErrorCheck).StatusCode -eq 400) 'Duplicate quote rejected over HTTP'
    $wrongContract = @{symbol='MES'; contractId='OTHER-EXPIRY'; timestamp=[DateTimeOffset]::UtcNow.ToString('O'); bid=5000; ask=5000.25} | ConvertTo-Json
    Check ((Invoke-WebRequest "$url/api/feed/quotes" -Method Post -ContentType 'application/json' -Body $wrongContract -Headers $adapterHeaders -SkipHttpErrorCheck).StatusCode -eq 400) 'Wrong expiring contract rejected over HTTP'
    $marketConfiguration.provider = 't4-simulator'
    $configured = Invoke-RestMethod "$url/api/market-data" -Method Put -ContentType 'application/json' -Body ($marketConfiguration | ConvertTo-Json -Depth 5) -Headers $dashboardHeaders
    Check (!$configured.configured -and $configured.missing.Count -gt 0) 'Incomplete T4 setup can be saved with explicit missing requirements'
    Check ((Invoke-WebRequest "$url/api/market-data/test" -Method Post -Headers $dashboardHeaders -SkipHttpErrorCheck).StatusCode -eq 409) 'T4 test blocked without credentials and complete IDs'
    Check ((Invoke-WebRequest "$url/api/market-data/connect" -Method Post -Headers $dashboardHeaders -SkipHttpErrorCheck).StatusCode -eq 409) 'Continuous T4 connection blocked without complete server configuration'
    Check ((Invoke-WebRequest "$url/api/market-data/connect" -Method Post -Headers $adapterHeaders -SkipHttpErrorCheck).StatusCode -eq 401) 'Adapter credentials cannot operate the provider connection'
    Check ((Invoke-WebRequest "$url/api/feed/quotes" -Method Post -ContentType 'application/json' -Body $quote -Headers $adapterHeaders -SkipHttpErrorCheck).StatusCode -eq 409) 'External injection disabled when T4 is selected'
    $changed = Invoke-RestMethod "$url/api/state" -Headers $dashboardHeaders
    Check ($changed.session.paused -and !$changed.session.instruments[0].fresh -and $null -eq $changed.session.instruments[0].quote) 'Provider change invalidates old quotes and keeps paper paused'
    $server.Kill($true); $server.WaitForExit(); $server.Dispose(); $server = $null
    $server = Start-TestServer $true
    $persisted = Invoke-RestMethod "$url/api/market-data" -Headers $dashboardHeaders
    Check ($persisted.settings.provider -eq 't4-simulator' -and $persisted.settings.instruments[0].marketId -eq 'SYNTHETIC-API-TEST' -and $null -eq $persisted.lastTest) 'Market-data bindings survive restart without persisting verified connection claims'
    Write-Host 'Dashboard HTTP smoke tests passed.'
} finally {
    if ($null -ne $server) { if (!$server.HasExited) { $server.Kill($true); $server.WaitForExit() }; $server.Dispose() }
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if ($resolved.StartsWith($testRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
