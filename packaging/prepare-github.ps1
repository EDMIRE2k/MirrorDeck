param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$target = [IO.Path]::GetFullPath($Destination)
if (-not (Test-Path -LiteralPath (Join-Path $target '.git'))) { throw 'Destination must be your separately cloned repository.' }
$folders = 'src','native','tests','pacing-tests','update-tests','update-smoke','updater','packaging'
foreach ($folder in $folders) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root $folder) -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
        if ($relative -match '(^|\\)(bin|obj|build)\\|^native\\vendor\\' -or $file.Extension -in '.dll','.exe','.pdb','.lib','.log','.zip') { continue }
        $to = Join-Path $target $relative
        New-Item -ItemType Directory -Path (Split-Path $to) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $to -Force
    }
}
foreach ($name in 'MirrorDeck.sln','Directory.Build.props','build-portable.ps1','README-PORTABLE.txt','GITHUB-SETUP.txt','RELEASE-NOTES-1.0.36.txt','NATIVE-DLSS.txt','THIRD-PARTY-UPSCALERS.txt','UPSCALING-TEST-NOTES.txt','FRAME-PACING-TEST-NOTES.txt') {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $target -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'source.gitignore') -Destination (Join-Path $target '.gitignore') -Force
Write-Output "Prepared source clone: $target (nothing committed or pushed)"
