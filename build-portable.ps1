param(
    [Parameter(Mandatory = $true)][string]$TemplateDirectory,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\MirrorDeck-1.0.36-win-x64'),
    [switch]$KeepInstalledReShade,
    [switch]$CleanDefaults
)
$ErrorActionPreference = 'Stop'
$template = (Resolve-Path -LiteralPath $TemplateDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
if (-not (Test-Path -LiteralPath (Join-Path $template 'MirrorDeck.App.runtimeconfig.json'))) {
    throw 'Template must be an existing self-contained MirrorDeck portable folder.'
}
$dlssRuntime = Join-Path $template 'PreviewHost\nvngx_dlss.dll'
if (-not (Test-Path -LiteralPath $dlssRuntime)) { throw 'The template is missing its NVIDIA DLSS runtime.' }
$runtimeHash = (Get-FileHash -LiteralPath $dlssRuntime -Algorithm SHA256).Hash
if ($runtimeHash -ne 'C85F971CE023C9F3492FC7455F0B01A24BA18EA39636407A846902C4360B0B7E') {
    throw 'Native DLSS preview requires the tested NVIDIA-signed 310.8.0.0 runtime from the 1.0.29 backup.'
}
$nrRuntime = Join-Path $PSScriptRoot 'artifacts\NR-runtime-verified\nvngx_dlssnr.dll'
if (-not (Test-Path -LiteralPath $nrRuntime) -or (Get-FileHash -LiteralPath $nrRuntime -Algorithm SHA256).Hash -ne '6EB209E764F39872625DEBD6ABAF45E2BB6322F6F270F781F70C059AE30B3927') {
    throw 'Missing or unverified native NR SF-v2 compatibility runtime. See NATIVE-DLSS.txt.'
}
& cmake -S (Join-Path $PSScriptRoot 'native') -B (Join-Path $PSScriptRoot 'native\build') -A x64
if ($LASTEXITCODE -ne 0) { throw 'Native DLSS configure failed. Install CMake, MSVC x64 and the Windows SDK.' }
& cmake --build (Join-Path $PSScriptRoot 'native\build') --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native DLSS build failed.' }
& dotnet build (Join-Path $PSScriptRoot 'MirrorDeck.sln') -c Release -v quiet /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Source build failed.' }
& dotnet publish (Join-Path $PSScriptRoot 'updater\MirrorDeck.Updater.csproj') -c Release -o (Join-Path $PSScriptRoot 'artifacts\updater-publish') -v quiet /clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Updater build failed.' }
New-Item -ItemType Directory -Path $output | Out-Null
$nestedApps = @(Get-ChildItem -LiteralPath $template -Directory | Where-Object {
    Test-Path -LiteralPath (Join-Path $_.FullName 'MirrorDeck.App.runtimeconfig.json')
} | Select-Object -ExpandProperty Name)
foreach ($file in Get-ChildItem -LiteralPath $template -File -Recurse) {
    $relative = $file.FullName.Substring($template.Length).TrimStart('\')
    # Extracting a portable ZIP into its existing folder can nest an older app.
    # Keep that copy in the template/backup, not inside the new release.
    if ($relative.Split('\')[0] -in $nestedApps) { continue }
    if ($CleanDefaults -and ($file.Name -match '\.(log[0-9]*|pdb)$' -or $file.Name -in 'MirrorDeck.settings.json','preview-render-settings.json','settings.json','ReShade.ini' -or $relative -match '(^|\\)(configs|[^\\]*screenshots|NGXLogs|ReShade-runtime-backups)\\')) { continue }
    if (-not $KeepInstalledReShade -and ($relative -match '^(PreviewHost\\)?(dxgi|d3d11|d3d10|d3d9|opengl32|ReShade64|ReShade32)\.dll(\..*)?$' -or $relative -match '(^|\\)ReShade-runtime-backups\\' -or $file.Name -match '^ReShade_Setup.*\.exe$')) {
        continue
    }
    $destination = Join-Path $output $relative
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
$native = Join-Path $output 'PreviewHost\NativeDLSS'
New-Item -ItemType Directory -Path $native -Force | Out-Null
Copy-Item -LiteralPath $dlssRuntime -Destination (Join-Path $native 'nvngx_dlss.dll') -Force
Copy-Item -LiteralPath $nrRuntime -Destination (Join-Path $native 'nvngx_dlssnr.dll') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native\NrResolve.hlsl') -Destination $native -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native\Guides.hlsl') -Destination $native -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native\Stabilize.hlsl') -Destination $native -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native\ProtectedUpscale.hlsl') -Destination $native -Force
foreach ($name in 'ffx_a.h','ffx_fsr1.h') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "src\MirrorDeck.PreviewHost\Shaders\Vendor\$name") -Destination $native -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native\build\Release\MirrorDeck.NativeDlss.dll') -Destination (Join-Path $output 'PreviewHost') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native\build\Release\nvngx.dll_MirrorDeckNR.dll') -Destination (Join-Path $output 'PreviewHost') -Force
# Preserve the dependency/runtime payload; replace both hosts and the shared settings model.
$built = Join-Path $PSScriptRoot 'src\MirrorDeck.App\bin\Release\net8.0-windows7.0'
foreach ($name in 'MirrorDeck.App.dll', 'MirrorDeck.App.exe', 'MirrorDeck.Audio.dll', 'MirrorDeck.Core.dll', 'MirrorDeck.Rendering.dll', 'MirrorDeck.Settings.dll', 'Vortice.Direct3D12.dll', 'Vortice.DirectX.dll', 'Vortice.DXGI.dll', 'Vortice.Mathematics.dll', 'SharpGen.Runtime.dll') {
    Copy-Item -LiteralPath (Join-Path $built $name) -Destination (Join-Path $output $name) -Force
}
# Merge new GPU-detection dependencies into the self-contained runtime manifest.
# Replacing the entire manifest with a framework-dependent build loses its runtime.
$depsPath = Join-Path $output 'MirrorDeck.App.deps.json'
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json -AsHashtable
$builtDeps = Get-Content -LiteralPath (Join-Path $built 'MirrorDeck.App.deps.json') -Raw | ConvertFrom-Json -AsHashtable
$target = $deps.targets[$deps.runtimeTarget.name]
$builtTarget = $builtDeps.targets[$builtDeps.runtimeTarget.name]
$appKey = @($target.Keys | Where-Object { $_ -like 'MirrorDeck.App/*' })[0]
foreach ($key in $builtTarget.Keys | Where-Object { $_ -like 'Vortice.*/*' -or $_ -like 'SharpGen.Runtime/*' }) {
    $target[$key] = $builtTarget[$key]
    $deps.libraries[$key] = $builtDeps.libraries[$key]
    $parts = $key.Split('/')
    $target[$appKey].dependencies[$parts[0]] = $parts[1]
}
$deps | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $depsPath -Encoding utf8
$preview = Join-Path $PSScriptRoot 'src\MirrorDeck.PreviewHost\bin\Release\net8.0-windows10.0.19041.0'
foreach ($name in 'MirrorDeck.PreviewHost.dll', 'MirrorDeck.PreviewHost.exe', 'MirrorDeck.Core.dll', 'MirrorDeck.Rendering.dll', 'MirrorDeck.Settings.dll') {
    Copy-Item -LiteralPath (Join-Path $preview $name) -Destination (Join-Path "$output\PreviewHost" $name) -Force
}
$licenses = Join-Path $output 'licenses\upscalers'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
foreach ($name in 'FSR-LICENSE.txt','Anime4K-LICENSE.txt') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "src\MirrorDeck.PreviewHost\Shaders\Vendor\$name") -Destination $licenses
}
foreach ($name in 'THIRD-PARTY-UPSCALERS.txt','UPSCALING-TEST-NOTES.txt','FRAME-PACING-TEST-NOTES.txt','NATIVE-DLSS.txt','README-PORTABLE.txt','GITHUB-SETUP.txt','RELEASE-NOTES-1.0.36.txt') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $output
}
$nativeLicenses = Join-Path $output 'licenses\native-dlss'
New-Item -ItemType Directory -Path $nativeLicenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native\vendor\dlss\LICENSE.txt') -Destination (Join-Path $nativeLicenses 'NVIDIA-RTX-SDK-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native\vendor\nvof\LICENSE') -Destination (Join-Path $nativeLicenses 'NVIDIA-OPTICAL-FLOW-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native\NR-CONTRACT-LICENSE.txt') -Destination $nativeLicenses
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'artifacts\updater-publish\MirrorDeck.Updater.exe') -Destination $output
@{ schema = 1; version = '1.0.36'; architecture = 'win-x64' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'MirrorDeck.release.json') -Encoding utf8
# The empty JSON object loads the model's populated defaults, without personal IDs.
if ($CleanDefaults) {
    '{}' | Set-Content -LiteralPath (Join-Path $output 'MirrorDeck.settings.json') -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packaging\ReShade.ini') -Destination (Join-Path $output 'PreviewHost\ReShade.ini')
}
Write-Output "Portable ready: $output"
