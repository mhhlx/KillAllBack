# KillAllBack 项目约定

## 执行边界

- 禁止为开发或验收运行 `killer.exe` 的正常清理模式。该模式会结束当前交互会话中未命中保留规则的进程。
- 允许的主程序验证入口为 `--version`、`--check-config --silent`、`--scan-only --silent`、`--dry-run` 和合成数据预览。编辑器和进程选择器截图不触发保存操作。
- 无窗口的离线回归使用 `test.ps1`。需要管理员权限的界面截图在交互用户会话中运行，不在 Session 0 运行。
- 保留本地 `keep.json` 及 `keep.json.bak-*`。示例与默认规则的变更单独核对，不覆盖本机配置。

## 开发和交付

- 版本由 `CHANGELOG.md` 第一个 `## v` 标题提供。
- 主源码在 `src/`，Windows 清单在 `assets/`，图标生成器在 `tools/`。默认图标样式为 `tile-blue`。
- 修改源码后执行 `.\build.ps1` 和 `.\test.ps1`，后者期望离线断言全部通过。按影响范围补做 [安全验收](docs/VERIFY.md)。
- 打包脚本从干净工作树的 Git 提交重建便携 ZIP。Git 操作以本目录为仓库根目录。
