# Packages Agent Mastermind: builds the app into out/pack/app (app/publish.ps1), builds and smoke-tests the extension,
# and writes one .vsix per Windows target into out/ with vsce, each carrying the same x64 build under bin/app — native
# ARM64 VS Code installs nothing without its own package. -SkipPublish packages a build already in out/pack/app, so a
# release can sign it in between; -Install puts the x64 package into this machine's VS Code. The README sits in
# extension/, which vsce's link rewrite cannot see in the repository URL, so its base URLs are passed.
#
#   pwsh -File extension/pack.ps1 [-SkipPublish] [-Install]
param([switch]$SkipPublish, [switch]$Install)
$ErrorActionPreference = 'Stop'
$ext   = $PSScriptRoot
$repo  = Split-Path -Parent $ext
$out   = Join-Path $repo 'out'
$build = Join-Path $out 'pack\app'
$pkg   = Get-Content (Join-Path $ext 'package.json') -Raw | ConvertFrom-Json
$web   = $pkg.repository.url

if (-not $SkipPublish) {
  & pwsh -NoProfile -File (Join-Path $repo 'app\publish.ps1') -Out $build
  if ($LASTEXITCODE -ne 0) { throw "app/publish.ps1 failed (exit $LASTEXITCODE)" }
}
if (-not (Test-Path (Join-Path $build 'Mastermind.exe'))) { throw "no build in $build - run app/publish.ps1 -Out $build" }

$bin = Join-Path $ext 'bin\app'
Remove-Item -Recurse -Force (Join-Path $ext 'bin') -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item (Join-Path $build '*') $bin -Recurse
Set-Content -NoNewline -Path (Join-Path $bin 'version.txt') -Value $pkg.version
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $ext 'LICENSE')

Push-Location $ext
try {
  & npm ci
  if ($LASTEXITCODE -ne 0) { throw "npm ci failed (exit $LASTEXITCODE)" }
  & npm run build
  if ($LASTEXITCODE -ne 0) { throw "npm run build failed (exit $LASTEXITCODE)" }
  & node smoke.js
  if ($LASTEXITCODE -ne 0) { throw "smoke.js failed (exit $LASTEXITCODE)" }
  foreach ($target in 'win32-x64', 'win32-arm64') {
    $vsix = Join-Path $out "mastermind-$target-$($pkg.version).vsix"
    & npx vsce package --target $target -o $vsix `
      --baseContentUrl "$web/blob/HEAD/extension" --baseImagesUrl "$web/raw/HEAD/extension"
    if ($LASTEXITCODE -ne 0) { throw "vsce package --target $target failed (exit $LASTEXITCODE)" }
    "{0}  {1:N1} MB" -f (Split-Path $vsix -Leaf), ((Get-Item $vsix).Length / 1MB)
  }
} finally { Pop-Location }

if ($Install) {
  & code --install-extension (Join-Path $out "mastermind-win32-x64-$($pkg.version).vsix") --force
  if ($LASTEXITCODE -ne 0) { throw "code --install-extension failed (exit $LASTEXITCODE)" }
  Write-Host 'Installed. An update loads only once every VS Code window is closed and reopened, which stops every running Claude session.'
}
