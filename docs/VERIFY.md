# 构建与验收（v1.1.7-dev）

## 安全边界

项目的 `AGENTS.md` 是执行约束：**不要运行不带 `--dry-run` 的正常模式 `killer.exe`**。正常模式会结束当前交互会话内未列入白名单的进程。所有提权界面验收必须在用户交互会话运行。

允许的无清理验证：

- `--version`
- `--check-config --silent`
- `--scan-only --silent`
- `--dry-run --silent`
- `--dry-run --screenshot <path>`
- `--keep-editor --screenshot <path>`（截图不点击任何写盘按钮）
- `--keep-picker-preview --screenshot <path>`（只读枚举当前登录会话并出图，不写 keep.json）
- `--instant-preview` / `--instant-preview-film`（仅合成数据）

## 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
# 可选图标样式：-Style tile-blue|tile-forest|tile-light|tile-graphite|mono-dark
```

默认图标样式为 `tile-blue`。脚本生成图标资源，编译 `src/` 中的程序源码，并将 `CHANGELOG.md` 首个版本标题写入 exe。

## 离线回归

`tests/KeepTests.cs` 与主程序源码一起编译，覆盖白名单匹配、规则保存与撤销、PID 身份、日志截断、版本解析和 JSON 转义。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\test.ps1
```

通过标准为断言全部通过、退出码 0。在 Windows 交互会话下，可加 `-Ui` 运行白名单页面联测。联测使用临时配置和合成结果，截图输出到测试目录的 `screenshots/`。

结果页联测应覆盖「下次保留」写入后重新读取、同名不同路径的隔离、同文件实例的状态同步、撤销后保留其他命中规则，以及坏配置下的写入反馈。配置失败的安全验收使用独立输出目录和临时配置，确认 `--check-config --silent`、`--dry-run --silent` 与 `--scan-only --silent` 都返回 2，扫描不会生成新计划。

## 提权安全验证

GitHub Actions 执行离线测试与从当前提交重新打包。需要检查实时扫描或界面时，在独立 Windows 用户会话手动执行。

每条命令都需要管理员权限。使用 `Start-Process -Verb RunAs -Wait` 在当前用户会话提权，不要把 GUI 截图放到 Session 0：

```powershell
Start-Process -FilePath .\killer.exe -ArgumentList '--version' -Verb RunAs -Wait
Start-Process -FilePath .\killer.exe -ArgumentList '--check-config','--silent' -Verb RunAs -Wait
Start-Process -FilePath .\killer.exe -ArgumentList '--scan-only','--silent' -Verb RunAs -Wait
Start-Process -FilePath .\killer.exe -ArgumentList '--dry-run','--silent' -Verb RunAs -Wait
Start-Process -FilePath .\killer.exe -ArgumentList '--dry-run','--screenshot',"$env:TEMP\killer-ui.png" -Verb RunAs -Wait
```

适合人工肉眼检查的其他截图：

```powershell
.\killer.exe --instant-preview --screenshot .\preview\instant.png
.\killer.exe --instant-preview-film .\preview\film.png
.\killer.exe --keep-editor --screenshot .\preview\keep-editor.png
```

`instant.png` 会生成 `instant-1-cleaning.png`、`instant-2-review.png`、`instant-3-result.png`。胶片会输出每 30ms 采样的最大跳变、超过 2% 的帧数、静止帧数和轨迹。预览使用合成数据。

## UI 尺寸与 DPI

截图使用 `--dry-run --screenshot <path> --ui-width N --ui-height N`，覆盖设计宽度 1180、900、760、560、390。检查响应式断点时重点看 `<1000`、`<820`、`<760`、`<560` 和 `<=390`。截图角标记录 `windowDpi` 与缩放比例；当前交付在 150%（144 DPI）显示器完成检查，其他物理 DPI 尚未实测。

## 性能记录与日志

安全预演各跑三次，再报告每条命令的原始时间、p50 和 p95。`--version`、配置校验、扫描、dry-run 的耗时受当前进程数量、GPU/网络检测和系统负载影响，dry-run 时间不代表真实清理时间。`kill.log` 的 `phases=` 记录阶段时间，`killcost:` 记录单进程耗时；`degraded=1 reason=budget` 仅用于触发清理预算的真实路径，dry-run 不应显示该标记。

比较白名单变更前后的扫描时，先将 `killer-plan.json` 与 `killer-keep-exclude.txt` 另存为基线，再运行 `--scan-only --silent`。计划中 PID、数量和工作集会随用户会话变化；把新增/消失进程与 `KEEP` 规则差异分开判断。不要修改或覆盖 `keep.json` 来配合验收。

## 禁止项与交付约束

- 不运行没有 `--dry-run` 的 `killer.exe`；不验证任何真实杀进程或停止服务的效果。
- 不改 `keep.json` 的 `system` 组，也不删除、重命名现有 `keep.json.bak-*`。
- `package.ps1` 要求干净工作树，从 Git HEAD 重新构建。发布 ZIP 包含 `MANIFEST.txt`，记录提交和包内文件的 SHA256。
- 交付报告要区分源码验证、合成 UI 预览、安全 dry-run 与真实清理；不能把 dry-run 数字写成真实提速结果。
