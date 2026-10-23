# Builds Mastermind: self-contained, the managed code bundled into Mastermind.exe and the native libraries written
# beside it, uncompressed — nothing unpacks to %TEMP% at a start, which temp cleaners break, and nothing
# decompresses. One folder, every file of which ships.
#
#   pwsh -File app/publish.ps1 [-Out <folder>]
#
# Publishes inside this project's bin\, then copies the folder to -Out. Never publish into a folder holding the
# project: the SDK drops the publish folder from the source globs, and a publish folder containing the project
# compiles no source at all — an exe that starts, shows nothing and never listens, with a clean build log.
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'out\app'))
$ErrorActionPreference = 'Stop'
$stage = Join-Path $PSScriptRoot 'bin\folder'
dotnet publish (Join-Path $PSScriptRoot 'Mastermind.csproj') -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false `
  -p:DebugType=none -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
New-Item -ItemType Directory -Force $Out | Out-Null
for ($try = 1; ; $try++) {
  try { Copy-Item (Join-Path $stage '*') $Out -Recurse -Force; break }
  catch {
    if ($try -ge 10) { throw "copy into $Out failed - is Mastermind.exe still running? $_" }
    Start-Sleep -Seconds 1   # a --hook run holds the exe for a moment
  }
}
Write-Host "Built $(Join-Path $Out 'Mastermind.exe')"
