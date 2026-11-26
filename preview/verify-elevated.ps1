# killer 一次性提权验收：把需要管理员权限的零风险命令全跑一遍并落盘。
#
# 为什么需要它：killer.exe 的清单是 requireAdministrator，任何参数都要先过 UAC；
# AI 会话通常不是提权的，只能由用户点一次"是"，之后这里一次跑完，不用反复弹窗。
#
# 用法（在任意 PowerShell 里执行，脚本会自己弹 UAC 提权）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File verify-elevated.ps1
#
# 产物：
#   verify-elevated.txt   全部命令的退出码与输出
#   preview\shot-verify.png      预演结果窗截图
#   killer-plan.json + killer-keep-exclude.txt   计划与白名单命中来源
#
# 全是零风险命令：没有一条是"不带 --dry-run 的真清理"。

param(
    [string]$Dir
)

$ErrorActionPreference = "Continue"
if (-not $Dir) { $Dir = Split-Path -Parent $PSScriptRoot }
$Dir = (Resolve-Path -LiteralPath $Dir -ErrorAction Stop).Path
$exe = Join-Path $Dir 'killer.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Build killer.exe first: $exe" }
$log = Join-Path $Dir "verify-elevated.txt"

function Write-Log([string]$t) {
    Write-Host $t
    try { Add-Content -LiteralPath $log -Value $t -Encoding UTF8 } catch { }
}

# ---------- 自提权：父进程只负责弹一次 UAC，然后等子进程跑完 ----------
$me = $PSCommandPath
if (-not $me) { $me = $MyInvocation.MyCommand.Path }
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "当前不是管理员，正在弹 UAC 请求提权……" -ForegroundColor Yellow
    Write-Host "(弹出的窗口里选「是」；脚本会在提权后的窗口里继续跑，这个窗口会等它结束。)"
    try {
        $argsList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"' + $me + '"'), "-Dir", ('"' + $Dir + '"'))
        $p = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $argsList -Verb RunAs -Wait -PassThru
        Write-Host ("提权子进程结束，退出码 " + $p.ExitCode) -ForegroundColor Green
    } catch {
        Write-Host ("提权被取消或失败：" + $_.Exception.Message) -ForegroundColor Red
        Write-Host "没有跑任何验收命令。" -ForegroundColor Red
        exit 1
    }
    if (Test-Path $log) {
        Write-Host ""
        Write-Host "=========== verify-elevated.txt ===========" -ForegroundColor Cyan
        Get-Content $log -Encoding UTF8 | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host "没有生成 verify-elevated.txt —— 提权后的那一步可能没跑起来。" -ForegroundColor Red
    }
    exit $p.ExitCode
}

# ---------- 以下只在提权进程里执行 ----------
$prev = Join-Path $Dir "preview"
if (-not (Test-Path $prev)) { New-Item -ItemType Directory -Path $prev | Out-Null }
if (Test-Path $log) { Remove-Item -LiteralPath $log -Force }

Write-Log "killer 提权验收 · $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Log ("exe = " + $exe)
Write-Log ("elevated = " + $isAdmin)

$codes = [ordered]@{}
function Run([string]$title, [string[]]$killerArgs, [string]$key) {
    Write-Log ""
    Write-Log ("=" * 70)
    Write-Log ("# " + $title)
    Write-Log ("> killer.exe " + ($killerArgs -join " "))
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = & $exe @killerArgs 2>&1 | Out-String
    $code = $LASTEXITCODE
    $sw.Stop()
    if ($null -eq $code) { $code = "n/a" }
    $codes[$key] = $code
    Write-Log ("exit=" + $code + "  用时 " + [Math]::Round($sw.Elapsed.TotalSeconds, 1) + "s")
    if ($out -and $out.Trim().Length -gt 0) { Write-Log $out.Trim() }
}

Run "版本串（从 CHANGELOG.md 读取）" @("--version") "version"
Run "只校验 keep.json" @("--check-config", "--silent") "check-config"
Run "只扫描：写出计划与白名单命中来源（不碰进程）" @("--scan-only", "--silent") "scan-only"
Run "预演（不碰进程）" @("--dry-run", "--silent") "dry-run"
Run "结果窗出图" @("--dry-run", "--screenshot", (Join-Path $prev "shot-verify.png")) "shot-result"
Run "三段状态出图：清理中 / 驻留复查 / 原地切结果（不碰进程、不写日志）" `
    @("--instant-preview", "--silent", "--screenshot", (Join-Path $prev "shot-instant.png")) "instant-preview"
Run "进度条丝滑度量化：脚本化跑一遍清理中进度条（不碰进程）" `
    @("--instant-preview-film", "--silent", "--screenshot", (Join-Path $prev "shot-film.png")) "instant-film"

Write-Log ""
Write-Log ("=" * 70)
Write-Log "# 汇总"
foreach ($k in $codes.Keys) { Write-Log ("  {0,-14} exit={1}" -f $k, $codes[$k]) }

Write-Log ""
Write-Log "# 附带检查"
Write-Log ("  killer-crash.log 存在? " + (Test-Path (Join-Path $Dir "killer-crash.log")) + "  (期望 False)")
foreach ($f in @("killer-plan.json", "killer-keep-exclude.txt", "preview\shot-verify.png",
                 "preview\shot-instant-1-cleaning.png", "preview\shot-instant-2-review.png",
                 "preview\shot-instant-3-result.png", "preview\shot-film.png")) {
    $p = Join-Path $Dir $f
    Write-Log ("  " + $f + " : " + $(if (Test-Path $p) { (Get-Item $p).Length.ToString() + " 字节" } else { "缺失" }))
}
$plan = Join-Path $Dir "killer-plan.json"
if (Test-Path $plan) {
    try {
        $j = Get-Content $plan -Raw -Encoding UTF8 | ConvertFrom-Json
        Write-Log ("  计划进程数 = " + $j.planCount + "，计划内存 = " + $j.planTotalMb + " MB，白名单命中 = " + $j.keepHitCount + "，baseDir 排除 = " + $j.baseDirExcludedCount)
    } catch { Write-Log ("  killer-plan.json 解析失败: " + $_.Exception.Message) }
}
$keepTxt = Join-Path $Dir "killer-keep-exclude.txt"
if (Test-Path $keepTxt) {
    $hits = @(Select-String -Path $keepTxt -Pattern '^KEEP ' -Encoding UTF8)
    Write-Log ("  白名单命中清单行数 = " + $hits.Count)
    Write-Log "  命中的规则（前 25 条）："
    $hits | Select-Object -First 25 | ForEach-Object { Write-Log ("    " + $_.Line) }
}
$tail = Join-Path $Dir "kill.log"
if (Test-Path $tail) {
    Write-Log ""
    Write-Log "# kill.log 最后 25 行"
    Get-Content $tail -Tail 25 -Encoding UTF8 | ForEach-Object { Write-Log ("  " + $_) }
}

Write-Host ""
Write-Host ("结果已写入 " + $log) -ForegroundColor Green
if (@($codes.Values | Where-Object { $_ -ne 0 }).Count) { exit 1 }
exit 0
