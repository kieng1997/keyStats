# Rendering smoke test for the keyboard heatmap control (KeyboardHeatmapControl).
# Compiles the production control plus ThemeManager into an isolated harness that hosts the control in a
# real WPF window, renders it off-screen and checks the rendered pixels; screenshots are written as PNG.
# Never builds or starts KeyStats, installs input hooks, reads user statistics, or touches system settings.
# Requires Windows with a .NET SDK and the .NET Framework 4.8 targeting pack
# (%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8).
#   -ControlPath <file>  validate a different revision, e.g. the pre-upgrade baseline:
#                        git show HEAD:KeyStats.Windows/KeyStats/Views/Controls/KeyboardHeatmapControl.cs > baseline.cs
#   -OutputDir <dir>     where the PNG artifacts are written (default: a fresh folder under %TEMP%)
param(
    [string]$ControlPath = (Join-Path $PSScriptRoot '../KeyStats/Views/Controls/KeyboardHeatmapControl.cs'),
    [string]$OutputDir = (Join-Path ([IO.Path]::GetTempPath()) ('ks-heatmap-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'

$sdkLine = & dotnet --list-sdks | Select-Object -Last 1
if ($sdkLine -notmatch '^(\S+) \[(.+)\]$') { throw 'A .NET SDK is required to compile the isolated heatmap rendering test.' }
$compiler = Join-Path $Matches[2] ($Matches[1] + '/Roslyn/bincore/csc.dll')
$referenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
if (-not (Test-Path -LiteralPath (Join-Path $referenceRoot 'PresentationFramework.dll'))) {
    throw "The .NET Framework 4.8 targeting pack is required (missing $referenceRoot)."
}
# Each run compiles into its own archive folder under the temp directory. Build artifacts are archived
# there and never deleted, so a run cannot disturb an earlier one.
$archiveDir = Join-Path ([IO.Path]::GetTempPath()) ('KeyStatsHeatmapRenderingTest-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $archiveDir -Force | Out-Null
$testExecutable = Join-Path $archiveDir 'KeyboardHeatmapRenderingSmokeTest.exe'

& dotnet $compiler /nologo /target:exe /langversion:10 /nullable:enable `
    "/out:$testExecutable" `
    "/reference:$referenceRoot/mscorlib.dll" `
    "/reference:$referenceRoot/System.dll" `
    "/reference:$referenceRoot/System.Core.dll" `
    "/reference:$referenceRoot/System.Drawing.dll" `
    "/reference:$referenceRoot/System.Windows.Forms.dll" `
    "/reference:$referenceRoot/WindowsBase.dll" `
    "/reference:$referenceRoot/PresentationCore.dll" `
    "/reference:$referenceRoot/PresentationFramework.dll" `
    "/resource:$(Join-Path $PSScriptRoot '../KeyStats/Resources/Icons/tray-icon.png'),KeyStats.Resources.Icons.tray-icon.png" `
    (Resolve-Path -LiteralPath $ControlPath).Path `
    (Join-Path $PSScriptRoot '../KeyStats/Helpers/ThemeManager.cs') `
    (Join-Path $PSScriptRoot 'tests/KeyboardHeatmapRenderingSmokeTest.cs')
if ($LASTEXITCODE -ne 0) { throw "Isolated heatmap rendering test compilation failed (build archived at $archiveDir)." }
Write-Host "  build: $archiveDir"

& $testExecutable $OutputDir
if ($LASTEXITCODE -ne 0) { throw "Heatmap rendering smoke test failed for $ControlPath." }
