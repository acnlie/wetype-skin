# WeType Skin Studio

[简体中文](README.md) | [English](README.en.md)

WeType Skin Studio 是一个给微信输入法换皮肤的社区工具。

Windows 版可以把皮肤应用到微信输入法的原生候选框和工具条，并运行独立的 DeepSeek Chan 桌宠。macOS 版目前主要用于编辑、预览和测试输入法副本，官方微信输入法不会被修改。

> 这是第三方项目，不是腾讯或 DeepSeek 的官方软件。Windows 原生换肤只支持已经核对过的微信输入法版本，不能当作通用的输入法皮肤接口使用。

项目地址：[GitHub](https://github.com/acnlie/wetype-skin) · 作者：[X / Tokupup](https://x.com/Tokupup)

## 你可以用它做什么

| 功能 | Windows | macOS |
| --- | --- | --- |
| 编辑背景、文字、强调色、边框和圆角 | 支持 | 支持 |
| 导入背景图片、调整位置和缩放 | 支持 | 支持编辑和预览 |
| 调整透明度 | 支持 | 支持编辑和预览；原生效果仍在适配 |
| 应用到真实候选框 | 支持微信输入法 2.1.4.6 | 仅应用到用户目录中的输入法副本 |
| 应用到输入法工具条 | 支持已核对的 Windows 资源 | 尚未完成稳定适配 |
| 导入和导出 `.wtskin.json` | 支持 | 支持，格式互通 |
| DeepSeek Chan 桌宠 | 支持，独立进程运行 | 支持，独立进程运行 |

编辑器中的候选框只是预览。只有点击“应用皮肤”后，Windows 版才会修改微信输入法文件并重启输入法。

## 安装

### Windows

1. 从 [GitHub Releases](https://github.com/acnlie/wetype-skin/releases) 下载 `WeTypeSkinStudio-Windows-x64.exe`。
2. 双击安装。程序安装到当前用户目录，不需要手动下载 WebView2。
3. 从开始菜单打开“微信输入法皮肤工作室”。
4. 选择预设或编辑皮肤，然后点击“应用皮肤”。第一次应用时，只有负责修改微信安装文件的助手会请求管理员权限。

编辑器本身应以普通权限运行。不要使用“以管理员身份运行”启动整个工作室，否则重启后的微信输入法可能无法正常显示候选框。

安装包内含微软 WebView2 离线安装程序。支持 Windows 10 2004 及以上的 64 位系统；这些系统通常已经包含程序所需的 .NET Framework 4.8。

### macOS

1. 下载 `WeTypeSkinStudio-macOS-Intel.dmg`。
2. 打开 DMG，把 `WeType Skin Studio.app` 拖到 Applications。
3. 双击应用启动。

当前发布包面向 Intel Mac，要求 macOS 13 或更高版本。编辑、预览和桌宠不需要 Python、Node 或 WebView2。需要按键状态反馈时，按系统提示在“系统设置 → 隐私与安全性 → 辅助功能”中授权实际安装的应用。

## Windows 使用方法

### 应用皮肤

1. 在左侧选择一个预设，或在“配色”“图片”“桌宠”面板中修改参数。
2. 图片可以直接拖动，也可以调整位置和缩放。支持 PNG、JPEG、BMP，以及 GIF 的第一帧。
3. 点击“应用皮肤”，等待页面显示应用完成。
4. 在记事本、浏览器或其他文本框中输入几个字，检查候选框、工具条和桌宠。

皮肤文件会自动保存到用户目录，也可以使用“导出”保存为 `.wtskin.json`。图片最大 8 MB，宽高最大 4096 像素，皮肤文件最大 13 MB。

### 重新打开工作室

工作室启动时会重新读取微信输入法的实际文件和保存记录，因此关闭后再次打开仍能识别已经应用的皮肤。修改草稿不会自动写入原生文件，必须再次点击“应用皮肤”。

已应用的皮肤会继续保留，即使关闭工作室或重启 Windows 也不会自动恢复官方样式。

### 桌宠

桌宠是独立进程。关闭工作室后，桌宠仍可以继续显示；重新打开工作室时会接管已有的桌宠进程，不会重复启动。桌宠会跟随候选框移动，候选框暂时消失时只隐藏窗口，不会退出进程。

桌宠不读取候选词和输入内容，也不会改变候选框的文字、布局或鼠标操作。需要关闭桌宠时，可以重新打开工作室，在桌宠设置中关闭，或使用托盘菜单中的“关闭桌宠”。

### 还原官方皮肤

优先打开工作室，点击“还原官方”。程序会使用应用前创建的官方备份恢复候选框和工具条，并重新检查输入法状态。

如果工作室无法打开，可以在安装目录执行：

```powershell
$app = "$env:LOCALAPPDATA\Programs\WeTypeSkinStudio\WeTypeSkinStudio.exe"
Start-Process $app -ArgumentList '--restore-native' -Wait
```

还原过程中不要手动删除备份或状态文件。默认备份目录是：

```text
%LOCALAPPDATA%\WeTypeSkinStudio\backups\2.1.4.6\
```

## macOS 使用方法

macOS 版可以编辑和导出与 Windows 兼容的皮肤文件，也可以把官方输入法复制到用户目录中进行原生候选框实验。系统必须实际识别并选中 `WeType Skin` 输入源，副本实验才会继续；失败时程序会停止副本并保留官方输入法。

官方微信输入法位于系统目录中，macOS 版不会修改、重签名或替换它。还原时会先切回官方输入源，再停止副本，避免输入法突然失效。

桌宠由应用包中的独立辅助程序运行。关闭编辑器或按 `⌘Q` 后，桌宠仍可继续运行；菜单栏图标可以重新打开编辑器或退出桌宠。候选框不可见时，桌宠会暂时隐藏并等待下一次候选框出现。

目前已在 Intel macOS 15.7.9 和微信输入法 2.2.3（build 657）上确认副本能够出词、候选框能够显示已应用的颜色并跟随候选框移动。图片、透明度、工具条以及所有输入状态动画仍属于实验功能，效果会受微信版本、窗口类型和显示器布局影响。

## 支持范围和已知限制

### Windows

- 系统：Windows 10 2004 或更高版本，x64。
- 输入法：微信输入法 2.1.4.6，默认安装目录。
- 微信输入法升级、移动安装目录，或原生文件被其他程序改动后，工具会拒绝写入。
- 原生补丁修改的是微信自己的文件。虽然程序会先备份并支持还原，但仍建议先保存重要数据。
- 不同 DPI、多个显示器和不同文本软件可能造成候选框或桌宠位置差异。

### macOS

- 发布包：Intel Mac；Apple Silicon 需要自行构建和验证。
- 原生实验只作用于用户目录中的输入法副本，不能直接修改官方输入法。
- 候选框跟随和桌宠已经接入，但工具条、部分中英文模式、半角/全角和标点状态仍在适配。
- 应用使用本地 ad-hoc 签名，未进行 Developer ID 公证。不要为了运行它而关闭 SIP 或全局 Gatekeeper。

## 从源码构建

### Windows

需要 Windows PowerShell、Windows 自带的 .NET Framework C# 编译器，以及用于运行编辑器的 WebView2 Runtime。仓库已经包含构建所需的 WebView2 SDK 文件。

```powershell
.\build.ps1 -OutputDirectory build\local
```

输出文件：

```text
build\local\WeTypeSkinStudio.exe
build\local\DeepSeekChan.InputPet.exe
```

构建 Windows 安装包需要一个由 Microsoft Corporation 签名的 WebView2 离线安装程序：

```powershell
.\package-installer.ps1 `
  -WebView2Installer C:\Downloads\MicrosoftEdgeWebView2RuntimeInstallerX64.exe
```

### macOS

需要 macOS 13 或更高版本，以及 Swift 5.9 或更高版本的 Xcode / Command Line Tools。脚本只构建当前 Mac 的 CPU 架构：

```bash
bash macos/build.sh
open "build/macos-$(uname -m)/WeType Skin Studio.app"
```

运行离线检查和打包：

```bash
bash macos/check.sh
bash macos/package.sh
```

清理本地构建产物：

```powershell
.\tools\clean.ps1
```

## 项目结构

```text
src/                    Windows C# 编辑器、原生补丁和桌宠
frontend/               Windows 与 macOS 共用的编辑器页面
macos/                  macOS 宿主、输入法副本和打包脚本
assets/pet/             桌宠动画、许可和来源信息
vendor/webview2/        Windows 构建使用的 WebView2 SDK 文件
docs/                   架构和验证说明
tools/                  清理和辅助脚本
```

`research/`、`build/`、`Release/` 和 `demo/` 是本地研究或生成目录，不属于源码发布内容。

更详细的实现说明见 [架构说明](docs/ARCHITECTURE.md)，验证边界见 [验证说明](docs/VERIFICATION.md)，macOS 的副本实验细节见 [macos/README.md](macos/README.md)。

## 许可和第三方资源

项目代码使用 [MIT License](LICENSE)。

- WebView2 SDK 的许可和声明见 [vendor/webview2/NOTICE.md](vendor/webview2/NOTICE.md)。
- DeepSeek Chan 动画来自 [dsh-whale-girl-pet](https://github.com/yanzwzz/dsh-whale-girl-pet)，许可和固定素材信息见 [assets/pet/ATTRIBUTION.md](assets/pet/ATTRIBUTION.md)。
- 角色形象参考 [deepseek-whalechan](https://github.com/Neko3000/deepseek-whalechan)。本项目不分发该项目的代码或图片。

提交问题时，请写明系统版本、微信输入法版本、安装路径，以及问题能否通过“还原官方”解决。
