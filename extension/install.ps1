# Packages the Mastermind VS Code shell into a .vsix and installs it into VS Code — the daily install, a milestone only:
# a first install loads into the open windows at once, but an update loads only on a full VS Code restart, which
# stops every running Claude session. Develop in an Extension Development Host instead
# (code --extensionDevelopmentPath=<this folder> <a scratch folder>), whose reload restarts only itself.
#
# Run from anywhere:  pwsh -File <this folder>/install.ps1
#
# Builds and smoke-tests first and stops on a failure. A hand-built .vsix, no vsce: VS Code loads only extensions
# listed in ~/.vscode/extensions/extensions.json, which `code --install-extension` writes — a raw folder copy sits on
# disk unseen.

$ErrorActionPreference = 'Stop'

$src  = $PSScriptRoot                                    # extension/
$repo = Split-Path -Parent $src                          # the repo root

Push-Location $src
try {
  & npm run build
  if ($LASTEXITCODE -ne 0) { throw "npm run build failed (exit $LASTEXITCODE)" }
  & node smoke.js
  if ($LASTEXITCODE -ne 0) { throw "smoke.js failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }

$pkg  = Get-Content (Join-Path $src 'package.json') -Raw | ConvertFrom-Json
$id   = "$($pkg.publisher).$($pkg.name)"
$ver  = "$($pkg.version)"
$eng  = "$($pkg.engines.vscode)"

function Xml([string]$s) { $s -replace '&','&amp;' -replace '<','&lt;' -replace '>','&gt;' }

# --- what the package carries: the manifest, the README (the details page), the build, the walkthrough's media ----
$files = [ordered]@{
  'extension/package.json' = Join-Path $src 'package.json'
  'extension/README.md'    = Join-Path $src 'README.md'
}
foreach ($f in Get-ChildItem (Join-Path $src 'out') -Filter *.js) { $files["extension/out/$($f.Name)"] = $f.FullName }
foreach ($f in Get-ChildItem (Join-Path $src 'media') -Filter *.md) { $files["extension/media/$($f.Name)"] = $f.FullName }

# --- stage (the repo's out/, gitignored — never OS temp) --------------------------------------------------------------
$tmp   = Join-Path $repo 'out'
$stage = Join-Path $tmp "vsix-build-$($pkg.name)"
New-Item -ItemType Directory -Force $stage | Out-Null

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011" xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">
  <Metadata>
    <Identity Language="en-US" Id="$($pkg.name)" Version="$ver" Publisher="$($pkg.publisher)" />
    <DisplayName>$(Xml $pkg.displayName)</DisplayName>
    <Description xml:space="preserve">$(Xml $pkg.description)</Description>
    <Tags>claude,agents</Tags>
    <Categories>Other</Categories>
    <GalleryFlags>Public</GalleryFlags>
    <Properties>
      <Property Id="Microsoft.VisualStudio.Code.Engine" Value="$eng" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionDependencies" Value="" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionPack" Value="" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionKind" Value="ui" />
    </Properties>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Code" />
  </Installation>
  <Dependencies />
  <Assets>
    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
    <Asset Type="Microsoft.VisualStudio.Services.Content.Details" Path="extension/README.md" Addressable="true" />
  </Assets>
</PackageManifest>
"@

$contentTypes = @"
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="json" ContentType="application/json" />
  <Default Extension="js" ContentType="application/javascript" />
  <Default Extension="md" ContentType="text/markdown" />
  <Default Extension="vsixmanifest" ContentType="text/xml" />
</Types>
"@

# [Content_Types].xml has literal brackets (PowerShell wildcards) — .NET file APIs, never the provider cmdlets.
$manifestPath = Join-Path $stage 'extension.vsixmanifest'
$ctypesPath   = Join-Path $stage '[Content_Types].xml'
[System.IO.File]::WriteAllText($manifestPath, $manifest)
[System.IO.File]::WriteAllText($ctypesPath,   $contentTypes)

# --- zip to .vsix (entry names MUST use forward slashes; FileMode.Create truncates) ---------------------------------
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$vsix = Join-Path $tmp "$id-$ver.vsix"
$fs   = [System.IO.File]::Open($vsix, [System.IO.FileMode]::Create)
$zip  = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $manifestPath, 'extension.vsixmanifest') | Out-Null
[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $ctypesPath, '[Content_Types].xml') | Out-Null
foreach ($name in $files.Keys) {
  [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $files[$name], $name) | Out-Null
}
$zip.Dispose(); $fs.Dispose()
Write-Host "Built $vsix"

# --- install through the VS Code CLI (writes the extensions.json manifest entry) ------------------------------------
& code --install-extension $vsix --force
if ($LASTEXITCODE -ne 0) { throw "code --install-extension failed (exit $LASTEXITCODE)" }

Write-Host ""
Write-Host "Installed $id v$ver. A first install is already live in the open windows. An update loads only once every"
Write-Host "VS Code window is closed (Alt+F4) and reopened, and that restart stops every running Claude session."
