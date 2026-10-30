# Public-boundary regression test for StatsPopupViewModel's app-list projection.
# Compiles the production view model, its base class, the refresher and the models, replacing only the
# external boundaries (StatsManager singleton, icon lookup, composition root, localization resource)
# with the stand-ins in tests/StatsPopupTestStubs.cs. Shows no window and never starts KeyStats or
# reads user statistics.
param(
    [string]$ViewModelPath = (Join-Path $PSScriptRoot '../KeyStats/ViewModels/StatsPopupViewModel.cs')
)
$ErrorActionPreference = 'Stop'

$sdkLine = & dotnet --list-sdks | Select-Object -Last 1
if ($sdkLine -notmatch '^(\S+) \[(.+)\]$') { throw 'A .NET SDK is required to compile the popup projection test.' }
$compiler = Join-Path $Matches[2] ($Matches[1] + '/Roslyn/bincore/csc.dll')
$referenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
if (-not (Test-Path -LiteralPath (Join-Path $referenceRoot 'PresentationFramework.dll'))) {
    throw "The .NET Framework 4.8 targeting pack is required (missing $referenceRoot)."
}
$app = Join-Path $PSScriptRoot '../KeyStats'
$testExecutable = Join-Path ([IO.Path]::GetTempPath()) ('KeyStatsPopupProjection-' + [Guid]::NewGuid().ToString('N') + '.exe')

# The models carry System.Text.Json attributes (no serialization is performed here), so the package
# must be referenced explicitly when compiling outside the SDK project. Resolve it through the
# project's existing restore output instead of assuming a global package path.
$assetsPath = Join-Path $app 'obj/project.assets.json'
if (-not (Test-Path -LiteralPath $assetsPath)) {
    throw "Missing $assetsPath. Run 'dotnet restore KeyStats.Windows/KeyStats/KeyStats.csproj' first."
}
$assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
$jsonLibrary = $assets.libraries.PSObject.Properties.Name |
    Where-Object { $_ -match '^System\.Text\.Json/' } |
    Select-Object -First 1
if (-not $jsonLibrary) { throw "System.Text.Json is not restored for the KeyStats project ($assetsPath)." }
$jsonVersion = $jsonLibrary.Substring($jsonLibrary.IndexOf('/') + 1)
$packageRoot = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
if (-not $packageRoot) { throw "No package folder recorded in $assetsPath." }
$jsonReference = Join-Path $packageRoot "system.text.json/$jsonVersion/lib/net462/System.Text.Json.dll"
if (-not (Test-Path -LiteralPath $jsonReference)) {
    throw "Missing restored asset: $jsonReference. Run 'dotnet restore KeyStats.Windows/KeyStats/KeyStats.csproj' first."
}

try {
    & dotnet $compiler /nologo /target:exe /langversion:10 /nullable:enable `
        "/out:$testExecutable" `
        "/reference:$referenceRoot/mscorlib.dll" `
        "/reference:$referenceRoot/System.dll" `
        "/reference:$referenceRoot/System.Core.dll" `
        "/reference:$referenceRoot/WindowsBase.dll" `
        "/reference:$referenceRoot/PresentationCore.dll" `
        "/reference:$referenceRoot/PresentationFramework.dll" `
        "/reference:$jsonReference" `
        (Resolve-Path -LiteralPath $ViewModelPath).Path `
        (Join-Path $app 'ViewModels/ViewModelBase.cs') `
        (Join-Path $app 'Helpers/BackgroundStatsRefresher.cs') `
        (Join-Path $app 'Models/AppStats.cs') `
        (Join-Path $app 'Models/DailyStats.cs') `
        (Join-Path $app 'Models/AppSettings.cs') `
        (Join-Path $PSScriptRoot 'tests/StatsPopupTestStubs.cs') `
        (Join-Path $PSScriptRoot 'tests/StatsPopupProjectionTest.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Popup projection test compilation failed.' }

    & $testExecutable
    if ($LASTEXITCODE -ne 0) { throw "Popup projection test failed for $ViewModelPath." }
}
finally {
    # The repository forbids deleting files; move build artifacts aside instead.
    $quarantine = Join-Path ([IO.Path]::GetTempPath()) 'KeyStatsPopupProjectionArtifacts'
    New-Item -ItemType Directory -Force -Path $quarantine | Out-Null
    if (Test-Path -LiteralPath $testExecutable) { Move-Item -LiteralPath $testExecutable -Destination $quarantine -Force }
}
