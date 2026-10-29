# Builds the mod and packs the release zip into dist\. The zip is extracted into the game's Mods
# folder, so it holds MSCCoop.dll at its root and MSCCoop.Net.dll in References.
#
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

dotnet build (Join-Path $repo 'src\MSCCoop\MSCCoop.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$mod = Join-Path $repo 'src\MSCCoop\CoopMod.cs'
$version = (Select-String -Path $mod -Pattern 'Version => "([^"]+)"').Matches[0].Groups[1].Value
$dist = Join-Path $repo 'dist'
$stage = Join-Path $dist "MSCCoop-$version"
$zip = Join-Path $dist "MSCCoop-$version.zip"

if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}
New-Item -ItemType Directory (Join-Path $stage 'References') -Force | Out-Null
Copy-Item (Join-Path $repo 'src\MSCCoop\bin\Release\net35\MSCCoop.dll') $stage
Copy-Item (Join-Path $repo 'src\MSCCoop.Net\bin\Release\net35\MSCCoop.Net.dll') (Join-Path $stage 'References')
Copy-Item (Join-Path $repo 'LICENSE'), (Join-Path $repo 'README.md') $stage

if (Test-Path $zip) {
    Remove-Item $zip -Force
}
# Windows PowerShell's zip commands write backslashes into entry names, which other unzip tools take
# as part of the file name.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $name = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $name) | Out-Null
    }
} finally {
    $archive.Dispose()
}
Write-Host "Packed $zip"
