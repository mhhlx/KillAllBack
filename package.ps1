[CmdletBinding()]
param(
    [ValidatePattern('^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$OutDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = (Join-Path $PSScriptRoot 'dist') }
$repo = $PSScriptRoot
$commit = & git -C $repo rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0) { throw 'Packaging requires a Git checkout with a commit.' }

$dirty = & git -C $repo status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0) { throw 'Could not read Git working tree status.' }
if ($dirty) { throw 'Commit the working tree changes before packaging.' }

$heading = Select-String -LiteralPath (Join-Path $repo 'CHANGELOG.md') -Encoding UTF8 -Pattern '^## (v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)(?:\s|$)' |
    Select-Object -First 1
if (-not $heading) { throw 'No version heading found in CHANGELOG.md.' }

$sourceVersion = $heading.Matches[0].Groups[1].Value
if ($Version -and $Version -cne $sourceVersion) { throw "Requested version $Version does not match CHANGELOG.md ($sourceVersion)." }
$Version = $sourceVersion

$null = New-Item -ItemType Directory -Path $OutDir -Force
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$name = "killer-$Version"
$zipPath = Join-Path $OutDir "$name.zip"
if ((Test-Path -LiteralPath $zipPath) -and -not $Force) { throw "Already exists: $zipPath. Use -Force to replace it." }

$stage = Join-Path ([IO.Path]::GetTempPath()) ('killer-package-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage
try {
    $sourceZip = Join-Path $stage 'source.zip'
    $source = Join-Path $stage 'source'
    $build = Join-Path $stage 'build'
    $portable = Join-Path $stage $name
    $requiredSource = @('build.ps1', 'src', 'assets', 'tools/make-icon.cs', 'CHANGELOG.md', '快速开始.txt', 'keep.example.json', '预演和白名单.cmd')
    & git -C $repo archive --format=zip "--output=$sourceZip" $commit -- @requiredSource
    if ($LASTEXITCODE -ne 0) { throw 'Could not export the release source from Git.' }

    Expand-Archive -LiteralPath $sourceZip -DestinationPath $source
    & (Join-Path $source 'build.ps1') -OutDir $build
    $null = New-Item -ItemType Directory -Path $portable
    $payload = [ordered]@{
        'killer.exe' = $build
        'app.ico' = $build
        'CHANGELOG.md' = $source
        '快速开始.txt' = $source
        'keep.example.json' = $source
        '预演和白名单.cmd' = $source
    }
    $manifest = @("version: $Version", "commit: $commit", '', 'SHA256  File')
    foreach ($file in $payload.Keys) {
        $path = Join-Path $payload[$file] $file
        Copy-Item -LiteralPath $path -Destination $portable
        $hash = (Get-FileHash -LiteralPath (Join-Path $portable $file)).Hash
        $manifest += "$hash  $file"
    }
    [IO.File]::WriteAllLines((Join-Path $portable 'MANIFEST.txt'), [string[]]$manifest, [Text.UTF8Encoding]::new($false))

    $stagedZip = Join-Path $stage "$name.zip"
    Compress-Archive -LiteralPath $portable -DestinationPath $stagedZip -CompressionLevel Optimal
    [IO.File]::Copy($stagedZip, $zipPath, [bool]$Force)
    Write-Host "Packaged -> $zipPath"
} finally {
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    if ($resolvedStage -eq $stage -and (Split-Path -Parent $resolvedStage) -eq ([IO.Path]::GetTempPath().TrimEnd('\'))) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
