# Regression smoke test for the tray popup's rounded corners (StatsPopupWindow tray mode).
# Compiles the production WindowBackdropHelper/NativeInterop/ThemeManager plus a small harness that
# shows a real WPF window with the tray-popup chrome over a synthetic backdrop and samples the corner
# pixels. Never builds, starts or touches KeyStats or user statistics.
# Requires Windows with a .NET SDK and the .NET Framework 4.8 targeting pack
# (%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8).
#   -HelperPath <file>  validate a different revision, e.g. the unfixed baseline:
#                       git show HEAD:KeyStats.Windows/KeyStats/Helpers/WindowBackdropHelper.cs > baseline.cs
param(
    [string]$HelperPath = (Join-Path $PSScriptRoot '../KeyStats/Helpers/WindowBackdropHelper.cs')
)
$ErrorActionPreference = 'Stop'

$sdkLine = & dotnet --list-sdks | Select-Object -Last 1
if ($sdkLine -notmatch '^(\S+) \[(.+)\]$') { throw 'A .NET SDK is required to compile the isolated popup corner test.' }
$compiler = Join-Path $Matches[2] ($Matches[1] + '/Roslyn/bincore/csc.dll')
$referenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
if (-not (Test-Path -LiteralPath (Join-Path $referenceRoot 'PresentationFramework.dll'))) {
    throw "The .NET Framework 4.8 targeting pack is required (missing $referenceRoot)."
}
$testExecutable = Join-Path ([IO.Path]::GetTempPath()) ('KeyStatsPopupCornerTest-' + [Guid]::NewGuid().ToString('N') + '.exe')

try {
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
        (Resolve-Path -LiteralPath $HelperPath).Path `
        (Join-Path $PSScriptRoot '../KeyStats/Helpers/NativeInterop.cs') `
        (Join-Path $PSScriptRoot '../KeyStats/Helpers/ThemeManager.cs') `
        (Join-Path $PSScriptRoot 'tests/StatsPopupCornerSmokeTest.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated popup corner test compilation failed.' }

    & $testExecutable
    if ($LASTEXITCODE -ne 0) { throw "Popup corner smoke test failed for $HelperPath." }
}
finally {
    if (Test-Path -LiteralPath $testExecutable) { Remove-Item -LiteralPath $testExecutable }
}
