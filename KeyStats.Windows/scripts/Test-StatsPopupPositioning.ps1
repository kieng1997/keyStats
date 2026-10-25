# Regression smoke test for tray-popup anchoring (StatsPopupWindow.PositionNearTray / OnLoaded /
# SizeChanged). Extracts the production methods from the real source, compiles them into a tiny WPF
# harness and shows a real window. Never builds, starts or touches KeyStats or user statistics.
# Requires Windows with .NET Framework 4.8 and a .NET SDK.
#   -SourcePath <file>   validate a different revision, e.g. the unfixed HEAD baseline:
#                        git show HEAD:KeyStats.Windows/KeyStats/Views/StatsPopupWindow.xaml.cs > baseline.cs
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '../KeyStats/Views/StatsPopupWindow.xaml.cs')
)
$ErrorActionPreference = 'Stop'

$sdkLine = & dotnet --list-sdks | Select-Object -Last 1
if ($sdkLine -notmatch '^(\S+) \[(.+)\]$') { throw 'A .NET SDK is required to compile the isolated popup test.' }
$compiler = Join-Path $Matches[2] ($Matches[1] + '/Roslyn/bincore/csc.dll')
$referenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
$source = (Resolve-Path -LiteralPath $SourcePath).Path
$generated = Join-Path ([IO.Path]::GetTempPath()) ('KeyStatsPopupExtract-' + [Guid]::NewGuid().ToString('N') + '.cs')
$testExecutable = Join-Path ([IO.Path]::GetTempPath()) ('KeyStatsPopupTest-' + [Guid]::NewGuid().ToString('N') + '.exe')

# Keep only the positioning members so the harness needs no StatsManager, XAML or app services.
$text = [IO.File]::ReadAllText($source)
$members = ([regex]::Matches($text, '(?m)^    private const double \w+ = .*?;\r?\n') | ForEach-Object Value) -join ''
foreach ($name in 'OnLoaded', 'PositionNearTray') {
    $match = [regex]::Match($text, "(?ms)^    private void $name\(.*?^    \}\r?\n")
    if (-not $match.Success) { throw "Method $name not found in $source" }
    $members += $match.Value
}
foreach ($pattern in '(?ms)^    private void OnWindowBoundsChanged\(object\? sender, SizeChangedEventArgs e\).*?^    \}\r?\n',
                     '(?ms)^    private void OnWindowBoundsChanged\(object\? sender, EventArgs e\).*?^    \}\r?\n') {
    $match = [regex]::Match($text, $pattern)
    if ($match.Success) { $members += $match.Value }
}

$extract = @"
using System;
using System.Windows;
using System.Windows.Forms;
namespace KeyStats.Views
{
    internal sealed partial class StatsPopupWindowHarness
    {
$members
    }
}
"@
[IO.File]::WriteAllText($generated, $extract)

try {
    & dotnet $compiler /nologo /target:exe /langversion:10 /nullable:enable /nowarn:CS0169,CS0414,CS0649 `
        "/out:$testExecutable" `
        "/reference:$referenceRoot/mscorlib.dll" `
        "/reference:$referenceRoot/System.dll" `
        "/reference:$referenceRoot/System.Core.dll" `
        "/reference:$referenceRoot/System.Drawing.dll" `
        "/reference:$referenceRoot/System.Windows.Forms.dll" `
        "/reference:$referenceRoot/WindowsBase.dll" `
        "/reference:$referenceRoot/PresentationCore.dll" `
        "/reference:$referenceRoot/PresentationFramework.dll" `
        $generated `
        (Join-Path $PSScriptRoot 'tests/StatsPopupPositioningSmokeTest.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Isolated popup test compilation failed.' }

    & $testExecutable
    if ($LASTEXITCODE -ne 0) { throw "Popup positioning smoke test failed for $source." }
}
finally {
    foreach ($path in $generated, $testExecutable) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
}
