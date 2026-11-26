[CmdletBinding()]
param([string]$OutDir, [switch]$Ui)

$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = (Join-Path $PSScriptRoot 'artifacts/tests') }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw "csc.exe not found: $csc" }

$null = New-Item -ItemType Directory -Path $OutDir -Force
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$testExe = Join-Path $OutDir 'keep-tests.exe'

Push-Location -LiteralPath $PSScriptRoot
try {
    $common = @(
        '/nologo', '/target:exe', '/codepage:65001',
        '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
        '/r:System.Management.dll', '/r:System.Runtime.Serialization.dll', '/r:Accessibility.dll',
        'src\killall-back.cs', 'src\ui.cs', 'src\runtime.cs', 'src\keep-rules.cs', 'src\whitelist-ui.cs', 'src\cleanup.cs'
    )
    & $csc /main:KillerKeepTest.Program "/out:$testExe" @common tests\KeepTests.cs
    if ($LASTEXITCODE -ne 0) { throw "Test compilation failed ($LASTEXITCODE)" }

    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)" }

    if ($Ui) {
        $uiExe = Join-Path $OutDir 'whitelist-ui-tests.exe'
        & $csc /main:KillAllBack.WhitelistUiTests "/out:$uiExe" @common tests\WhitelistUiTests.cs
        if ($LASTEXITCODE -ne 0) { throw "UI test compilation failed ($LASTEXITCODE)" }

        & $uiExe (Join-Path $OutDir 'screenshots')
        if ($LASTEXITCODE -ne 0) { throw "UI tests failed ($LASTEXITCODE)" }
    }
} finally {
    Pop-Location
}
