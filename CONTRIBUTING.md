# 贡献指南

感谢你关注 WeType Skin Studio。这个项目修改的是特定版本微信输入法的本地文件，提交功能改动时请把版本边界、恢复路径和实际验证结果一起说明。

## Windows 开发准备

- Windows x64、.NET Framework 4.8 和 Microsoft Edge WebView2 Runtime。
- 目标微信输入法必须是 README 中列出的已核对版本。
- 不要把微信安装文件、用户皮肤、截图、日志或本地 `artifacts` 提交到仓库。

## 修改规则

Mac 编辑器开发需要 macOS 13+ 和 Swift 5.9+ 工具链。先阅读 macos/README.md 的能力边界；不要将编辑器预览描述为原生换肤或常驻桌宠。

1. 先阅读 `docs/ARCHITECTURE.md`，明确编辑器、原生补丁和独立桌宠之间的边界。
2. 原生补丁必须保留原始字节和 SHA-256 校验，不能用模糊搜索替代版本保护。
3. 文件写入必须经过状态记录、原子替换和可恢复路径。
4. 前端改动需要覆盖保存、导入导出、应用状态和窄窗口布局。
5. 新的第三方资源需要在 `assets` 或 `vendor` 中附带来源、版本和许可证。

## 本地检查

```powershell
.\build.ps1 -OutputDirectory build\local
Start-Process .\build\local\WeTypeSkinStudio.exe -ArgumentList '--self-test', '.\build\self-test' -Wait
Start-Process .\build\local\WeTypeSkinStudio.exe -ArgumentList '--verify-update', '.\build\update-check' -Wait
```

如果改动涉及原生应用事务，再运行 `--native-check`。真实输入法验收只能在匹配版本的 Windows 安装副本上进行，结果应写入本地验证目录，不要提交原始安装文件。

## 提交说明

Mac 端执行 bash macos/build.sh 和 bash macos/check.sh。修改输入源生命周期时，从 DMG 安装后执行 bash macos/check.sh --lifecycle <安装后的应用路径>；此检查操作真实输入源，最后重新应用原皮肤。修改消息桥或共用前端时，在已登录桌面中执行 bash macos/check.sh --ui；检查只使用临时皮肤目录。跨平台字段改动应同时更新 src/Theme.cs 与 macos/src/SkinTheme.swift，并保持版本 1 文件可互通。

提交信息请说明行为变化，例如 `fix: reject partial native skin state`。Pull Request 应包含：问题、改动、验证命令、已知限制，以及是否需要管理员权限或真实微信输入法环境。
