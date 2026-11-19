# Rebuilds the offline Cubism runtime for the desktop pet viewer.
#
# The Cubism SDK is NOT part of this repository. Pass the path to a locally downloaded
# Cubism SDK for Web 5-r.5 (from https://www.live2d.com/en/sdk/download/web/) that you have
# accepted the Live2D license for.
#
#   powershell -ExecutionPolicy Bypass -File scripts/build-desktop-pet-vendor.ps1 -CubismSdk <path>
#
# Outputs (all under KeyStats.Windows/KeyStats/Resources/DesktopPet/vendor):
#   live2dcubismcore.min.js, framework.js, shaders/*, LICENSE-Live2D.md, VERSION.txt

param(
    [Parameter(Mandatory = $true)][string]$CubismSdk,
    [string]$Esbuild = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$RepoDir = Split-Path -Parent $ScriptDir
$PetDir = Join-Path $RepoDir "KeyStats.Windows\KeyStats\Resources\DesktopPet"
$VendorDir = Join-Path $PetDir "vendor"
$VendorSrcDir = Join-Path $PetDir "vendor-src"

if (-not (Test-Path $CubismSdk)) {
    throw "Cubism SDK not found: $CubismSdk"
}

# The bundle entry lives outside the SDK, so the import specifiers must be absolute: a relative
# SDK path (e.g. tmp/CubismSdkForWeb) would resolve against the temporary entry's own folder.
$CubismSdk = (Resolve-Path -Path $CubismSdk).ProviderPath.TrimEnd('\', '/')

$CoreJs = Join-Path $CubismSdk "Core\live2dcubismcore.min.js"
$CoreLicense = Join-Path $CubismSdk "Core\LICENSE.md"
$CoreRedistributable = Join-Path $CubismSdk "Core\RedistributableFiles.txt"
$FrameworkLicense = Join-Path $CubismSdk "LICENSE.md"
$ShaderDir = Join-Path $CubismSdk "Framework\Shaders\WebGL"
foreach ($required in @($CoreJs, $CoreLicense, $CoreRedistributable, $FrameworkLicense, $ShaderDir)) {
    if (-not (Test-Path $required)) { throw "Cubism SDK is incomplete, missing: $required" }
}

if ([string]::IsNullOrWhiteSpace($Esbuild)) {
    $candidate = Join-Path $RepoDir "services\sync-worker\node_modules\.bin\esbuild.cmd"
    if (Test-Path $candidate) { $Esbuild = $candidate }
    else {
        $resolved = Get-Command esbuild -ErrorAction SilentlyContinue
        if ($resolved) { $Esbuild = $resolved.Source }
    }
}
if ([string]::IsNullOrWhiteSpace($Esbuild) -or -not (Test-Path $Esbuild)) {
    throw "esbuild not found. Pass -Esbuild <path> (available in services/sync-worker/node_modules/.bin)."
}

New-Item -ItemType Directory -Force -Path $VendorDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $VendorDir "shaders") | Out-Null

# The generated entry keeps the legal banner of the SDK sources it pulls in.
$infoPath = Join-Path $CubismSdk "cubism-info.yml"
$sdkVersion = "unknown"
if (Test-Path $infoPath) {
    $versionLine = Select-String -Path $infoPath -Pattern '^version:\s*(.+)$'
    if ($versionLine) { $sdkVersion = $versionLine.Matches[0].Groups[1].Value.Trim() }
}

$FrameworkBanner = @"
/**
 * Live2D Cubism Framework bundle for the KeyStats desktop pet viewer.
 * Copyright(c) Live2D Inc. All rights reserved.
 * Compiled from Cubism SDK for Web $sdkVersion sources (CubismWebFramework).
 * Licensed under the Live2D Open Software License; this file is NOT covered by the KeyStats MIT license.
 * See LICENSE-Live2D-Framework.md and VERSION.txt in this folder.
 */
"@

# The SDK is never modified: write the generated entry with the same unchanged content next to it, in
# the workspace scratch folder, so the run is reproducible and nothing is deleted afterwards.
$EntryDir = Join-Path $RepoDir "tmp\desktop-pet-vendor-build"
New-Item -ItemType Directory -Force -Path $EntryDir | Out-Null
$EntryPath = Join-Path $EntryDir "framework-entry.generated.ts"
$SdkForImport = $CubismSdk.Replace('\', '/')
$EntrySource = Get-Content (Join-Path $VendorSrcDir "framework-entry.ts") -Raw
$EntrySource = $EntrySource.Replace("__SDK__", $SdkForImport)
Set-Content -Path $EntryPath -Value $EntrySource -Encoding UTF8

$FrameworkOut = Join-Path $VendorDir "framework.js"
$IntermediateOut = Join-Path $EntryDir "framework.bundle.js"
$EsbuildArgs = @(
    $EntryPath,
    "--bundle",
    "--format=iife",
    "--target=es2020",
    "--minify",
    "--legal-comments=none",
    "--outfile=$IntermediateOut"
)
& $Esbuild @EsbuildArgs
if ($LASTEXITCODE -ne 0) { throw "esbuild failed with exit code $LASTEXITCODE" }

# esbuild's CLI banner cannot carry newlines reliably, so the banner is prepended here. The
# intermediate stays in the scratch folder (nothing is deleted).
$bundle = Get-Content $IntermediateOut -Raw
Set-Content -Path $FrameworkOut -Value ($FrameworkBanner + "`r`n" + $bundle) -Encoding UTF8
if (-not (Test-Path $FrameworkOut)) { throw "framework bundle was not written" }
if ((Get-Content $FrameworkOut -TotalCount 1) -notmatch '^/\*\*') { throw "framework bundle is missing its license banner" }

Copy-Item $CoreJs (Join-Path $VendorDir "live2dcubismcore.min.js") -Force
Copy-Item (Join-Path $ShaderDir "*.vert") (Join-Path $VendorDir "shaders") -Force
Copy-Item (Join-Path $ShaderDir "*.frag") (Join-Path $VendorDir "shaders") -Force

# Both licenses and the Core redistribution list ship with the runtime.
Copy-Item $CoreLicense (Join-Path $VendorDir "LICENSE-Live2D-Core.md") -Force
Copy-Item $CoreRedistributable (Join-Path $VendorDir "RedistributableFiles-Core.txt") -Force
Copy-Item $FrameworkLicense (Join-Path $VendorDir "LICENSE-Live2D-Framework.md") -Force

$infoPath = Join-Path $CubismSdk "cubism-info.yml"
$sdkVersion = "unknown"
if (Test-Path $infoPath) {
    $versionLine = Select-String -Path $infoPath -Pattern '^version:\s*(.+)$'
    if ($versionLine) { $sdkVersion = $versionLine.Matches[0].Groups[1].Value.Trim() }
}
$coreHash = (Get-FileHash (Join-Path $VendorDir "live2dcubismcore.min.js") -Algorithm SHA256).Hash
$frameworkHash = (Get-FileHash (Join-Path $VendorDir "framework.js") -Algorithm SHA256).Hash

@(
    "Cubism SDK for Web version: $sdkVersion",
    "",
    "Files in this folder:",
    "  live2dcubismcore.min.js      Cubism Core (unmodified SDK file)",
    "    SHA256: $coreHash",
    "  framework.js                 CubismWebFramework bundle, built with esbuild",
    "    SHA256: $frameworkHash",
    "  shaders\                     Cubism WebGL shaders (unmodified SDK files)",
    "  LICENSE-Live2D-Core.md       Live2D Proprietary Software License (Core)",
    "  RedistributableFiles-Core.txt  Core files the license allows redistributing",
    "  LICENSE-Live2D-Framework.md  Live2D Open Software License (Framework, samples, shaders)",
    "",
    "These files are Live2D Cubism Components and are NOT covered by the KeyStats MIT license.",
    "  Core license:      https://www.live2d.com/eula/live2d-proprietary-software-license-agreement_en.html",
    "  Framework license: https://www.live2d.com/eula/live2d-open-software-license-agreement_en.html",
    "",
    "The third-party desktop pet model is NOT part of this distribution and must be supplied by the user.",
    "",
    "Rebuild with: scripts/build-desktop-pet-vendor.ps1 -CubismSdk <path>"
) | Set-Content -Path (Join-Path $VendorDir "VERSION.txt") -Encoding UTF8

Write-Host "Desktop pet vendor runtime rebuilt in $VendorDir" -ForegroundColor Green
Write-Host "  SDK version: $sdkVersion"
Write-Host "  Core SHA256: $coreHash"
Write-Host "  Framework SHA256: $frameworkHash"
