# 贡献指南

## 开发流程

1. 从 `main` 建工作分支，让每个提交围绕一个可独立验证的改动。
2. 修改源码后，从仓库根目录运行 `.\build.ps1` 和 `.\test.ps1`。
3. 涉及 CLI、界面或配置时，更新相应文档。实时会话与截图验收见 [验证说明](docs/VERIFY.md)。
4. 提交 PR 时说明修改原因、验证结果和面向用户的影响。Windows CI 会执行离线测试并生成便携包。

`keep.json`、运行日志、扫描结果、截图和构建产物留在本地。提交问题或 PR 时，可用 [示例配置](keep.example.json) 和合成数据复现，并移除本机路径、进程清单和用户信息。

## 目录

| 路径 | 内容 |
| --- | --- |
| `src/` | CLI、清理引擎、界面和运行时实现 |
| `assets/` | Windows 清单与备用图标 |
| `tools/` | 图标生成器 |
| `tests/` | 无需管理员权限的离线回归 |
| `docs/` | 架构、验收和已知问题 |
| `preview/` | 在交互用户会话运行的手动预演脚本 |
| `.github/` | Windows CI、Issue 和 PR 模板 |

## 打包和发布

在 `CHANGELOG.md` 顶部维护版本号和变更说明。提交后从干净工作树运行 `.\package.ps1`，或取 Windows CI 的 `killer-windows-portable` 产物。解压便携 ZIP 后，对照 `MANIFEST.txt` 校验文件与源提交，再在独立 Windows 用户会话按验证说明检查预演。获准发布时，使用与变更日志一致的 Git 标签，在 Releases 上传该 ZIP。

本仓库从 v1.1.7-dev 的源码快照开始维护。对外分发前由项目所有者确定许可证。
