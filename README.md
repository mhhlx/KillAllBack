# KillAllBack（killer）

Windows 便携工具。它会结束当前交互会话中未命中保留规则的进程，临时停止配置中的服务，并在一个窗口内显示进度和结果。首次使用双击「预演和白名单」，检查清理计划并保存工作后再运行正式清理。

## 使用便携包

有发布版本时，可在仓库的 Releases 下载便携 ZIP，也可以按下文的[开发步骤](#开发)自行打包。

1. 解压 `killer-<版本>.zip`。
2. 双击 `预演和白名单.cmd`，按 UAC 提示授权，检查将结束的进程和将临时停止的服务。
3. 在同窗的「白名单」调整规则，再双击预演入口核对计划。确认后双击 `killer.exe` 执行清理。

配置 `keep.json` 和日志 `kill.log` 与程序放在同一目录。程序在首次读取配置时创建默认文件，也可以复制 `keep.example.json` 为 `keep.json` 后编辑。升级时将已有 `keep.json` 复制到新目录。需要桌面入口时运行 `killer.exe --install-shortcut`。

在程序内保存规则变更时，保存前的 `keep.json` 会备份到同目录下的 `backups/`。首次产生备份时，程序会自动创建该文件夹。

## 开发

环境为 64 位 Windows、Windows PowerShell 和系统自带的 .NET Framework 4.x 编译器，无 NuGet 依赖。打包还需要 Git。

```powershell
.\build.ps1
.\test.ps1
.\package.ps1
```

`build.ps1` 默认在仓库根目录生成 `killer.exe`，可用 `-OutDir` 指定独立输出目录。打包从干净工作树的 Git 提交重新构建，在 `dist/` 生成便携 ZIP 和包内文件校验清单。

- [贡献指南](CONTRIBUTING.md) · [架构](docs/ARCHITECTURE.md) · [验证](docs/VERIFY.md) · [已知问题](docs/KNOWN-ISSUES.md) · [变更记录](CHANGELOG.md)

## 行为

- 支持同时打开多个窗口。同一安装下，一个运行中的目标进程由认领它的清理实例处理，其他实例可继续清理各自的目标。
- **直接强杀，不确认**：未保存的文档/下载会丢失（这是选定的行为）。
- 哪些程序会结束取决于 `keep.json` 中实际命中的规则。执行前检查预演计划，保留要继续运行的应用及其相关进程。
- `keep.json` 读取或校验失败时，程序在扫描和清理前停止，并提示检查配置或备份。
- 进程扫描与结束仅限当前交互式会话（explorer 所在的 Session），不重启 explorer、不清待机缓存；`keep.json` 中显式配置的 Windows 服务通过服务管理器单独停止。
- 结果顶部列出未结束、未停服务和跳过的项目，明细中优先显示这些记录及原因。首卡按「清理后减清理前」显示**物理内存已用量的带符号变化**。预演首卡显示计划进程工作集累计值。
- 日志里对应 `memory before / after / delta` 三行，含 `commit=` 与 `gpu=` 字段。
- 高分屏：清单声明**显示器级 DPI 感知（PerMonitorV2）**，布局以 96 DPI 设计单位书写，运行时按实际 DPI 缩放；窗口拖到另一块不同 DPI 的显示器或运行中改系统缩放时，`DpiAwareForm` 处理 `WM_DPICHANGED` 重新缩放整棵控件树（**v1.1.1-dev 起不再需要重启程序**）。文字不会被系统位图拉伸（150% 下实测窗口 1792×1406 px、windowDpi=144）。
- **真跑只有一个窗口**：点开就是「清理中」（首卡换成当前动作 + 细粒度进度条 + 实时内存采样，清单逐条上屏），跑完在**同一个窗口原地切成结果态** —— 不再关掉再弹第二个；窗口**不会自动关闭**，看完点「关闭」。
- 清理中选择关闭窗口时会先确认。确认后清理继续执行，运行结果写入日志。
- 除交互式会话的进程外，还会按配置**停止指定的 Windows 服务**（详见下面 `services`）。

## 命令行

| 命令 | 作用 |
|---|---|
| `killer.exe` | 正常清理（双击等同于这个） |
| `killer.exe --dry-run` | 预演：只列出将结束的进程并写日志，不动任何进程 |
| `killer.exe --dry-run --silent` | 预演且不弹窗口（用于验收/自动化） |
| `killer.exe --install-shortcut` | 重新创建桌面快捷方式 `killer.lnk` |
| `killer.exe --silent` | 静默执行清理（无窗口） |
| `killer.exe --screenshot out.png` | 将预演界面保存成 PNG，可配合 `--silent`；显式指定合成或白名单预览时截取对应界面 |
| `killer.exe --scan-only` | 只扫描、把计划与白名单命中来源写成 `killer-plan.json` / `killer-keep-exclude.txt`，**不碰任何进程**（用于改动白名单逻辑前后的逐条 diff） |
| `killer.exe --plan-out <路径>` / `--keep-exclude <路径>` | 配合 `--scan-only` 指定输出位置 |
| `killer.exe --check-config` | 只校验 `keep.json` 并退出（解析失败返回 2） |
| `killer.exe --keep-editor` | 直接打开同款白名单管理页，查看或维护用户规则 |
| `killer.exe --keep-picker-preview --screenshot out.png` | 只读预览当前登录会话的进程选择器，不写配置 |
| `killer.exe --version` | 显示编译时从 `CHANGELOG.md` 写入程序的版本 |
| `killer.exe --instant-preview --screenshot out.png` | 生成清理中、驻留复查、结果三张合成数据预览图，不扫描或操作进程 |
| `killer.exe --instant-preview-film out.png` | 合成进度动画并输出跳变统计和胶片图，不扫描或操作进程（也支持 `--screenshot out.png`） |
| `killer.exe --ui-width N --ui-height N` | 指定预演设计尺寸，可配合截图参数 |

## 白名单（keep.json）

`keep.json` 的 `system` 和 `user` 进程保留规则各支持两种写法。

- **进程名规则**：不含 `\` `/` `:` 的条目（如 `Pogget`、`Bongo Cat Mver`、`vivo_remote_watch.exe`）→ 只跟进程名**全等**比较，大小写不敏感，结尾的 `.exe` 可写可不写。
- **路径规则**：含 `\` `/` `:` 的条目（如 `C:\Apps\Example`）→ 按可执行文件完整路径**子串**匹配。
- **精确文件和目录规则**。`exe=` 后写完整 exe 路径，`dir=` 后写完整目录。校验发现相对路径或重复的配置字段时停止。如果旧条目碰巧以 `exe=` 或 `dir=` 开头，请改用对应的完整路径规则。

> ⚠️ 历史坑（已修）：以前所有条目一律当路径子串用，默认 `system` 名单里的 `"System"` 因此命中了 `C:\Windows\System32\...`，把 notepad / cmd / powershell / taskmgr / regedit 一整片**静默免疫**掉。升级到 v1.1.1-dev 后这些程序会重新进入清理计划 —— 如果不想要，就显式把它们的进程名写进 `user`。

- `system` 保留系统外壳与关键组件（如 explorer、输入法、dwm）。省略该字段或设为 `[]` 会使用默认保护。仅含空白项的非空数组会报错。
- `user`：用户要保留的进程名或路径规则。已有安装以本地 `keep.json` 为准。
  - 对刚关闭的应用，可以在清理结果对应行点「下次保留」。按完整可执行文件路径立即保存，覆盖该文件的所有进程实例。最近一次添加可在原处撤销，本次结束记录照常显示。
  - 清理完成后，在同一窗口切到「白名单」，搜索进程并点击行内「保留」。默认精确匹配当前 exe；也可以在详情中明确选择同名进程或指定目录。保存成功后可以在本页撤销。
- `services` 指定本轮临时停止的 Windows 服务，默认含 `Everything`。服务原有启动类型继续有效。Windows 拒绝停止有活动依赖的服务时，结果页会显示原因。点「重新打开应用」可尝试启动本轮已停服务。
- `graceful` 中的进程会先收到正常关闭消息（`CloseMainWindow`）。等待时间由整轮清理的 3 秒共享预算决定，程序会用剩余时间尝试结束仍在运行的目标。默认名单包含快加速的三个进程。
- `networkRepair` 为 `true` 时，清理后运行只读网络检测。发现可修问题时，在网络详情中提供手动修复入口。

### 网络检测与手动修复

检测检查域名解析、连通性、代理和虚拟网卡状态。清理结果可以先显示，网络详情在检测结束后更新。

选择「修复网络…」并确认后，程序重新检查当前状态，按需关闭失效本地代理、重置失效 WinHTTP 代理或处理回环 DNS。修复结果和复测写入日志。PAC 自动代理脚本和虚拟网卡默认路由仅提供诊断。遇到脚本不可达时，可在连接公司网络或 VPN 后重新检查。

改完保存即生效，**不需要重新编译**。想连 NVIDIA 浮窗一起杀，就把 `user` 里的 NVIDIA 相关条目删掉。

## 日志与重建

### 界面规范（雾蓝白 · 克莱因蓝）

- **固定浅色**：页面底 `#EEF3FD`、白卡、发丝线 `#D8E2F7`、正文 `#16233D`、次级文字 `#33475B`、弱化文字 `#6B7D9C`；按钮与强调值采用克莱因蓝 `#002FA7`，警告与错误使用语义色。
- **主视觉**：首卡恢复 135° 克莱因蓝渐变（`#1A3FD4 → #002FA7`），白色进度环与 1px 内高光；清理前柱使用浅蓝渐变，清理后柱使用半透明白。卡片使用轻薄蓝灰边线，不使用投影。
- **组件与状态**：自绘按钮支持默认、悬停、聚焦、按下、禁用和忙碌状态；白名单编辑器与结果页共用雾蓝白/克莱因蓝令牌。结果反馈显示在状态卡，不弹 MessageBox。
- **进度动效**：清理中的进度按 30ms 采样，胶片验收同时报告最大单帧跳变、超过 2% 的帧数、静止帧数和轨迹。动效遵循 Windows 动画设置；截图模式关闭入场动画。
- **响应式断点**：设计宽度 `<1000` 时明细表将清理前/后合并展示并保留变化列；`<900` 缩小清单行距；`<820` 隐藏路径列；`<760` 页脚拆成多行；`<560` 首卡改为纵向排列、操作按钮堆叠；`<=390` 使用更紧凑数值排版。验收截图使用的窗口最小尺寸为 320×360 设计单位。
- **验收参数**：`--dry-run --screenshot out.png` 只预演并渲染结果窗；`--keep-editor --screenshot out.png` 渲染白名单编辑器；`--instant-preview --screenshot out.png` 生成 `out-1-cleaning.png`、`out-2-review.png`、`out-3-result.png`；`--instant-preview-film --screenshot out.png` 生成进度胶片；`--ui-width N` / `--ui-height N` 指定设计尺寸。`--auto-reopen` 在正常结果窗中会自动执行重开；配合 `--dry-run --screenshot` 时只显示忙碌态预览，不启动程序。

- 每次运行**追加**一段到 `kill.log`（纯追加，超过 512 KB 时自动只保留最近 20 次），含时间、是否提权、被杀清单与释放量。注意 `killed:` 行里的 `freed` 是"被结束进程自身工作集累加"，**不是实测释放量**；实测释放看同一段的 `memory delta` 行。
- `tools/make-icon.cs` 生成多尺寸图标和预览。用 `build.ps1 -UseExistingIcon` 可复用输出目录的图标，或使用仓库内的 `assets/app.ico`。
- 快捷方式图标由 `--install-shortcut` 写入（`IconLocation = killer.exe,0`），重装/重建后运行一次该命令即可刷新；可用 `build.ps1 -Style tile-blue` 等参数切换图标样式。

## 已知限制

- 「重新打开应用」通过当前登录用户的 Windows 桌面提交请求。无法识别启动入口或桌面不可用时，结果页会列出需要手动打开的程序。
- 个别受保护进程可能杀不掉，会列在结果窗口的"未结束"行里。
