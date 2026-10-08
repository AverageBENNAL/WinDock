# Builds the release downloads: a single WinDock.exe with .NET bundled in, as an installer and as a portable zip.
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'release'
$zip = Join-Path $root 'WinDock-win-x64.zip'
$setup = Join-Path $root 'WinDock-Setup.exe'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'WinDock.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# The installer is the same exe: started under a "Setup" name, it installs itself.
Copy-Item (Join-Path $out 'WinDock.exe') $setup -Force
"Wrote $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"

Copy-Item (Join-Path $root 'LICENSE') $out
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
"Wrote $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
