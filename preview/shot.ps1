# killer 结果窗出图（自提权）。killer.exe 的清单是 requireAdministrator，
# 任何参数都要先过 UAC，所以这里由脚本自己弹一次 UAC，子进程跑完把输出回显给父进程。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File preview\shot.ps1 -Out shot-step2.png
#   powershell -NoProfile -ExecutionPolicy Bypass -File preview\shot.ps1 -Out shot-busy.png -Mode --instant-preview
#
# 产物：preview\<Out>（界面实拍）+ preview\shot.log（子进程的控制台输出）
# 只跑零风险通道：默认 --dry-run，绝不跑不带 --dry-run 的真清理。

param(
    [string]$Dir,
    [ValidatePattern('^[^\\/:*?"<>|]+\.png$')]
    [string]$Out = "shot-ui.png",
    [ValidateSet('--dry-run', '--instant-preview', '--instant-preview-film', '--keep-editor', '--keep-picker-preview')]
    [string]$Mode = "--dry-run",
    [int]$Width = 1180,
    [int]$Height = 930
)

$ErrorActionPreference = "Continue"
if (-not $Dir) { $Dir = Split-Path -Parent $PSScriptRoot }
$Dir = (Resolve-Path -LiteralPath $Dir -ErrorAction Stop).Path
$exe = Join-Path $Dir 'killer.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Build killer.exe first: $exe" }
$prev = Join-Path $Dir "preview"
$log = Join-Path $prev "shot.log"

$me = $PSCommandPath
if (-not $me) { $me = $MyInvocation.MyCommand.Path }
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "非管理员：正在弹 UAC 提权（在弹出的窗口里选「是」）……" -ForegroundColor Yellow
    try {
        $argsList = @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"' + $me + '"'),
            "-Dir", ('"' + $Dir + '"'), "-Out", ('"' + $Out + '"'), "-Mode", $Mode,
            "-Width", $Width, "-Height", $Height
        )
        $p = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $argsList -Verb RunAs -Wait -PassThru
        Write-Host ("提权子进程结束，退出码 " + $p.ExitCode) -ForegroundColor Green
    } catch {
        Write-Host ("提权被取消或失败：" + $_.Exception.Message) -ForegroundColor Red
        exit 1
    }
    if ($p.ExitCode -ne 0) { exit $p.ExitCode }
    if (Test-Path $log) {
        Write-Host ""
        Write-Host "=========== shot.log ===========" -ForegroundColor Cyan
        Get-Content $log -Encoding UTF8 | ForEach-Object { Write-Host $_ }
    }
    $png = Join-Path $prev $Out
    if ($Mode -eq '--instant-preview') {
        $png = Join-Path $prev ([IO.Path]::GetFileNameWithoutExtension($Out) + '-3-result.png')
    }
    if (Test-Path -LiteralPath $png) {
        $it = Get-Item $png
        Write-Host ("出图成功：" + $it.FullName + "  " + $it.Length + " 字节  " + $it.LastWriteTime) -ForegroundColor Green
    } else {
        Write-Host ("没有生成 " + $png) -ForegroundColor Red
        exit 1
    }
    exit 0
}

# ---------- 以下只在提权进程里执行 ----------
if (-not (Test-Path $prev)) { New-Item -ItemType Directory -Path $prev | Out-Null }
if (Test-Path $log) { Remove-Item -LiteralPath $log -Force }

$png = Join-Path $prev $Out
if (Test-Path $png) { Remove-Item -LiteralPath $png -Force }

$killerArgs = @($Mode, "--silent", "--screenshot", $png, "--ui-width", $Width, "--ui-height", $Height)
"killer 结果窗出图 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Add-Content -LiteralPath $log -Encoding UTF8
("exe = " + $exe) | Add-Content -LiteralPath $log -Encoding UTF8
("args = " + ($killerArgs -join " ")) | Add-Content -LiteralPath $log -Encoding UTF8

$out = & $exe @killerArgs 2>&1 | Out-String
$code = $LASTEXITCODE
("exit=" + $code) | Add-Content -LiteralPath $log -Encoding UTF8
if ($out -and $out.Trim().Length -gt 0) { $out.Trim() | Add-Content -LiteralPath $log -Encoding UTF8 }
exit $code
