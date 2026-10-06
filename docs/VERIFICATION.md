# 验证说明

验证分为离线检查、发布包检查和真实输入法验收。构建成功只说明 C#、资源和前端能够编译，不能证明微信输入法窗口在所有应用和显示器上表现一致。

## 常规验证

```powershell
.\build.ps1 -OutputDirectory build\local
Start-Process .\build\local\WeTypeSkinStudio.exe -ArgumentList '--self-test', '.\build\self-test' -Wait
Start-Process .\build\local\WeTypeSkinStudio.exe -ArgumentList '--verify-update', '.\build\update-check' -Wait
Start-Process .\build\local\WeTypeSkinStudio.exe -ArgumentList '--verify-pet-render', '.\build\pet-render' -Wait
Start-Process .\build\local\WeTypeSkinStudio.exe -ArgumentList '--native-check', '.\build\native-check' -Wait
```

这些命令不写入微信安装目录。`--self-test` 覆盖皮肤模型、图片构图、预览渲染和内嵌资源；`--verify-update` 覆盖桌宠动作和状态切换；`--verify-pet-render` 覆盖透明预乘缓冲和尺寸变化；`--native-check` 只生成补丁计划并检查当前安装是否匹配。

## 发布验证

```powershell
.\package-installer.ps1 -WebView2Installer C:\Downloads\MicrosoftEdgeWebView2RuntimeInstallerX64.exe
Start-Process .\Release\WeTypeSkinStudio-Windows-x64.exe -ArgumentList '--extract', 'D:\Temp\wetype-package-check' -Wait
Get-AuthenticodeSignature D:\Temp\wetype-package-check\runtime\WebView2OfflineSetup.exe
Get-ChildItem D:\Temp\wetype-package-check -Recurse -File | Select-Object FullName, Length
```

Windows 安装器负载应只包含编辑器、独立桌宠、卸载程序、许可证、第三方归属和微软离线 WebView2 安装器。不得包含 `TASK-GOAL.md`、`artifacts`、研究结果、微信业务文件或用户备份。

## Mac 编辑器验证

~~~bash
bash macos/build.sh
bash macos/check.sh --ui
~~~

离线检查覆盖六套预设、图片/构图/透明度往返、旧字段默认值、非法文件拒绝和原文件保留。UI 检查在真实 WKWebView 上确认初始化、图片拖放、缩放与位置调整、自动保存、重新打开和动画预览；原生副本会加载 hook，TIS 未注册时必须安全失败并停止副本。测试皮肤使用独立临时目录，不会覆盖用户的皮肤。

原生文件面板的鼠标操作、中文输入法试打、跨显示器行为和长期性能仍需人工验收。Mac 候选框跟随和部分输入动作已接入；半角／全角、标点与微信内部中英模式尚未完成。2026-10-06 单次真实窗口核验：官方候选框 (123,164,510,32)，桌宠 (545,74,88,88)，右侧对齐并位于上方。工具条分类、多屏和键盘动画仍待真实验收。独立桌宠的进程存活已在编辑器退出后验证。

2026-10-05 在 Intel macOS 15.7.9 / Xcode 26.1（Swift 6.2.1）虚拟机上通过了上述离线和 WKWebView 集成检查，生成当前架构的 ad-hoc 签名应用及 ZIP。编辑器和动画预览的截图作为本地构建证据保留，不进入发布包。

同一环境中的官方微信输入法 2.2.3（build 657）保持签名校验通过，主程序 SHA-256 为 a68a92300cc38e5941f59832c516a37eb098c10c6bbe47ab3dcbe812705cf5ee。它只用于侦察前后未变化的核对，不是 Mac 原生皮肤支持名单。

## Windows 真实验收

在匹配的微信输入法 `2.1.4.6` 上，分别检查：应用预设、图片拖动和缩放、候选框透明度、工具条颜色、输入/删除、翻页、鼠标选词、还原官方、工作室退出后桌宠是否仍在，以及重启 Windows 后皮肤是否保持。任何结果都应记录输入法版本和官方文件哈希。短时间无闪烁采样不能推导出长期或所有程序中绝对无闪烁。


## 2026-10-06 安装版收口验证

Windows 启动检测回归：旧版测试在“页面初始化重新核对已应用皮肤”断言失败；修复后 253 项通过，覆盖失败后恢复、检测失败时不沿用已应用缓存，以及检测成功清除错误。从最终 EXE 安装后运行 --native-check，识别暮紫；实际 WebView2 页面显示已应用并启用还原。输入法三个原生文件和应用记录与本轮前一致。首次异常的具体原因未稳定复现；窗口只检测一次、永久缓存失败状态的路径已复现并修复。

Windows：使用 `package-installer.ps1` 构建含微软 WebView2 离线完整安装器的 EXE。通过安装器的 `--extract <绝对目录>` 验证文件完整性、工作室和桌宠哈希、微软运行时签名；从解包工作室运行离线检查，253 项通过。已实际完成当前用户安装，并核对开始菜单路径和卸载注册；当前主机已有 WebView2，因此没有在不含 WebView2 的干净系统上执行运行时安装分支。正式安装目录为当前用户的 `%LOCALAPPDATA%/Programs/WeTypeSkinStudio`，卸载保留用户皮肤与恢复备份。

Mac：从 DMG 安装到 `/Applications/WeType Skin Studio.app` 后执行 `bash macos/check.sh --lifecycle "/Applications/WeType Skin Studio.app"`，核对独立辅助应用路径、编辑器退出和重开后同一个桌宠 PID、已应用状态识别、官方输入源切换后才停止副本、还原后的重开状态，最后重新应用用户原皮肤。

真实文本编辑器候选框保持打开时，桌宠状态为 visible；候选框 `[123,604,510,32]`、桌宠 `[545,638,88,88]`（AppKit 坐标），右侧对齐并位于上方。Computer Use 只读画面确认角色可见。旧桌宠使用普通浮窗层级，会被微信接近系统最高层级的候选窗口遮挡；新版使用候选层级加一。Spotlight 切换、多屏和长期稳定性仍待进一步视觉验收。

Mac 运行时不再调用 Python 或开发工具 lipo，副本准备程序随应用编译打包；开发工具仅用于源码构建。官方微信输入法主程序哈希与签名仍未变化。最终 Release 目录只含 Windows EXE 安装包与 macOS Intel DMG；校验值写入发布说明。
