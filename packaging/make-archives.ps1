param([Parameter(Mandatory=$true)][string]$PortableDirectory, [Parameter(Mandatory=$true)][string]$DownloadDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$portable = (Resolve-Path -LiteralPath $PortableDirectory).Path
$downloads = (Resolve-Path -LiteralPath $DownloadDirectory).Path
$zip = Join-Path $downloads 'MirrorDeck-1.0.36-win-x64.zip'
if (Test-Path -LiteralPath $zip) { throw 'Release ZIP already exists; do not overwrite a published artifact.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($portable, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
$private = Join-Path $downloads 'MirrorDeck-1.0.36-BuildInputs-PRIVATE.zip'
if (Test-Path -LiteralPath $private) { throw 'Private inputs archive already exists.' }
$archive = [IO.Compression.ZipFile]::Open($private, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($folder in 'lib','native\vendor','artifacts\NR-runtime-verified') {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root $folder) -Recurse -File) {
            if ($folder -eq 'artifacts\NR-runtime-verified' -and $file.Name -ne 'nvngx_dlssnr.dll') { continue }
            $name = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
} finally { $archive.Dispose() }
Write-Output "Release: $zip"
Write-Output "SHA256: $hash"
Write-Output "Private build inputs: $private"
