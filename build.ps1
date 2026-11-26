[CmdletBinding()]
param(
    [ValidateSet('tile-blue', 'tile-forest', 'tile-graphite', 'tile-light', 'mono-dark')]
    [string]$Style = 'tile-blue',
    [switch]$UseExistingIcon,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = $PSScriptRoot }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw "csc.exe not found: $csc" }

$null = New-Item -ItemType Directory -Path $OutDir -Force
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$ico = Join-Path $OutDir 'app.ico'
$exe = Join-Path $OutDir 'killer.exe'
$iconTool = Join-Path $OutDir 'make-icon.exe'
$versionSource = Join-Path ([IO.Path]::GetTempPath()) ('killer-version-' + [guid]::NewGuid().ToString('N') + '.cs')

Push-Location -LiteralPath $PSScriptRoot
try {
    $heading = Select-String -LiteralPath 'CHANGELOG.md' -Encoding UTF8 -Pattern '^## v((\d+\.\d+\.\d+)(?:-[0-9A-Za-z.-]+)?)(?:\s|$)' |
        Select-Object -First 1
    if (-not $heading) { throw 'No version heading found in CHANGELOG.md.' }
    $version = $heading.Matches[0].Groups[1].Value
    $numericVersion = $heading.Matches[0].Groups[2].Value
    [IO.File]::WriteAllLines($versionSource, [string[]]@(
        "[assembly: System.Reflection.AssemblyVersion(""$numericVersion.0"")]",
        "[assembly: System.Reflection.AssemblyFileVersion(""$numericVersion.0"")]",
        "[assembly: System.Reflection.AssemblyInformationalVersion(""$version"")]"
    ))

    if ($UseExistingIcon) {
        if (-not (Test-Path -LiteralPath $ico -PathType Leaf)) {
            Copy-Item -LiteralPath 'assets\app.ico' -Destination $ico
        }
    } else {
        & $csc /nologo /target:exe /platform:anycpu /codepage:65001 /r:System.Drawing.dll "/out:$iconTool" tools\make-icon.cs
        if ($LASTEXITCODE -ne 0) { throw "Icon generator compilation failed ($LASTEXITCODE)" }

        & $iconTool $ico (Join-Path $OutDir 'preview-256.png') (Join-Path $OutDir 'preview-strip.png') $Style (Join-Path $OutDir 'styles.png')
        if ($LASTEXITCODE -ne 0) { throw "Icon generation failed ($LASTEXITCODE)" }
    }

    $arguments = @(
        '/nologo', '/target:winexe', '/platform:anycpu', '/codepage:65001', '/optimize+',
        "/win32icon:$ico", '/win32manifest:assets\app.manifest', "/out:$exe",
        '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
        '/r:System.Management.dll', '/r:System.Runtime.Serialization.dll', '/r:Accessibility.dll',
        'src\killall-back.cs', 'src\ui.cs', 'src\runtime.cs', 'src\keep-rules.cs', 'src\whitelist-ui.cs', 'src\cleanup.cs',
        $versionSource
    )
    & $csc @arguments
    if ($LASTEXITCODE -ne 0) { throw "Application compilation failed ($LASTEXITCODE)" }

    if ($OutDir -ne $PSScriptRoot) {
        Copy-Item -LiteralPath 'CHANGELOG.md' -Destination $OutDir
    }

    Write-Host "Build OK -> $exe"
} finally {
    [IO.File]::Delete($versionSource)
    Pop-Location
}
