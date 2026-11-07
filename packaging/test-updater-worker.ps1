$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$fixture = Join-Path $root ('artifacts\worker-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
& dotnet publish (Join-Path $root 'update-smoke\MirrorDeck.UpdateSmoke.csproj') -c Release -o (Join-Path $fixture 'stub') -v quiet /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed' }
$old = Join-Path $fixture 'app'
$payload = Join-Path $fixture 'package'
New-Item -ItemType Directory -Path $old,(Join-Path $payload 'PreviewHost'),(Join-Path $fixture 'worker') | Out-Null
Copy-Item -LiteralPath (Join-Path $fixture 'stub\MirrorDeck.App.exe') -Destination $payload
foreach ($name in 'MirrorDeck.App.dll','MirrorDeck.App.runtimeconfig.json','MirrorDeck.Core.dll','PreviewHost\MirrorDeck.PreviewHost.exe') {
    '{}' | Set-Content -LiteralPath (Join-Path $payload $name)
}
@{schema=1; version='1.0.36'; architecture='win-x64'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'MirrorDeck.release.json')
'original executable' | Set-Content -LiteralPath (Join-Path $old 'MirrorDeck.App.exe')
'original core' | Set-Content -LiteralPath (Join-Path $old 'MirrorDeck.Core.dll')
'preserved settings' | Set-Content -LiteralPath (Join-Path $old 'MirrorDeck.settings.json')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $fixture 'package.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip)
$hash = (Get-FileHash -LiteralPath $zip).Hash
$helper = Join-Path $fixture 'worker\MirrorDeck.Updater.exe'
Copy-Item -LiteralPath (Join-Path $root 'artifacts\updater-publish\MirrorDeck.Updater.exe') -Destination $helper
$start = [Diagnostics.ProcessStartInfo]::new($helper)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WorkingDirectory = Split-Path $helper
foreach ($arg in $old,$zip,$hash,'1.0.36','2147483647',([DateTime]::UtcNow.Ticks.ToString())) { $start.ArgumentList.Add($arg) }
$process = [Diagnostics.Process]::Start($start)
if (-not $process.WaitForExit(60000)) { throw 'Worker did not finish; fixture retained for inspection.' }
if ($process.ExitCode -ne 0) { throw 'Worker failed. See fixture update-result.json.' }
$deadline = [DateTime]::UtcNow.AddSeconds(15)
while (-not (Test-Path -LiteralPath (Join-Path $old 'restarted-ok.txt')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
if (-not (Test-Path -LiteralPath (Join-Path $old 'restarted-ok.txt'))) { throw 'Updated app did not restart.' }
$result = Get-Content -LiteralPath (Join-Path $fixture 'worker\update-result.json') -Raw | ConvertFrom-Json
if (-not $result.success -or -not (Test-Path -LiteralPath $result.backup)) { throw 'Missing successful result or rollback directory.' }
if ((Get-Content -LiteralPath (Join-Path $old 'MirrorDeck.settings.json') -Raw).Trim() -ne 'preserved settings') { throw 'Settings changed.' }
Write-Output "PASS: published standalone updater extracted, installed, backed up, preserved settings and restarted a harmless test app. $fixture"
