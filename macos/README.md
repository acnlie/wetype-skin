# Mac 编辑版

这是 WeType Skin Studio 的 macOS 编辑器，使用 AppKit 和系统 WKWebView。无需 Node、Electron、WebView2 或本地 HTTP 服务。

Mac 版支持编辑和预览皮肤、调整图片构图、预览 DeepSeek Chan 动作、自动保存、导入和导出 .wtskin.json。原生实验功能会把官方输入法复制到用户的 ~/Library/Input Methods/WeTypeSkinStudioInputMethod.app，仅对副本加载 hook；官方 /Library/Input Methods/WeType.app 不会被改写。只有 macOS 将副本加入当前输入源列表后，“应用皮肤”才会成功；否则应用会停止副本并明确报错，继续保留官方输入法。

Mac 桌宠由独立进程运行。关闭编辑器后桌宠继续存在，菜单栏图标可以重新打开编辑器、显示桌宠或退出桌宠；关闭皮肤中的桌宠开关后，下一次保存会结束它。当前 Mac 桌宠按官方微信输入法浮窗的位置显示，候选框不可见时隐藏，不再跟随鼠标。窗口分类使用所属进程、层级与尺寸，候选框和小型工具条的识别仍属实验功能。已接入候选出现、停留、输入与翻页按键、系统输入源切换动画；按键反馈依赖系统授权，微信内部中英模式、半角／全角和标点切换尚未实现完整识别。桌宠使用独立的进程锁，不会因为编辑器退出或重新打开而重复启动，也不会创建 Dock 图标。桌宠启动失败会写入系统日志，并不影响编辑器和官方输入法。

## 运行

打开 DMG，将 WeType Skin Studio.app 拖到 Applications，然后双击打开。编辑器和候选框位置跟随不需要开发工具、Python、辅助功能、录屏或管理员授权；可选的按键动作反馈需要辅助功能权限。关闭窗口或按 ⌘Q 会保存当前有效编辑后完全退出编辑器；保存失败会保持窗口打开并显示原因。独立桌宠位于应用包的 Contents/Helpers 内，使用单独的应用身份和实例锁；编辑器退出不会结束桌宠。

构建产物仅使用本地 ad-hoc 签名，尚未做 Developer ID 签名和公证。网上下载的 DMG 可能受到 Gatekeeper 限制。不要因此关闭 SIP 或全局 Gatekeeper；可从源码在自己的 Mac 构建。Intel 和 Apple Silicon 使用各自架构的包，当前实际运行验证环境为 Intel macOS 15.7.9。

## 从源码构建

需要 macOS 13+ 和提供 Swift 5.9+ 的 Xcode / Command Line Tools；开发机已有工具链时不需要另行安装。

~~~bash
bash macos/build.sh
open "build/macos-$(uname -m)/WeType Skin Studio.app"
~~~

脚本只构建当前主机架构，编译使用单个进程，不在线下载依赖。前端和动画资源全部放入应用包，动画仅在打开动作预览页时加载，最多缓存三张帧图。

~~~bash
bash macos/check.sh
bash macos/package.sh
~~~

check.sh 验证皮肤文件校验、原子保存和格式往返；加上 --ui 会在已登录的图形桌面中启动 WKWebView 集成检查并保存编辑器截图。检查使用临时皮肤目录，不覆盖当前用户皮肤，也不修改微信输入法。

发布包写入 build/macos-<架构>/，只生成 DMG；DMG 内含应用和 Applications 快捷入口。打包时检查独立桌宠、全部动作资源和原生启动组件，缺少组件会终止打包。源码、用户配置、研究文件和编译缓存不进入应用包。公开发布仍需维护者完成签名、公证和对应架构的真实测试。

安装后的集中检查：`bash macos/check.sh --lifecycle "/Applications/WeType Skin Studio.app"`。该检查使用真实用户输入源，会应用当前保存的皮肤、退出和重开编辑器、执行官方还原，最后重新应用原皮肤；不修改官方包。它与使用临时皮肤目录的编辑器预览检查不同。

## 保存与恢复

编辑结果保存在：

~~~text
~/Library/Application Support/WeTypeSkinStudio/current.wtskin.json
~~~

Windows 与 Mac 使用同一版本 1 皮肤格式，背景图片内嵌保存。图片限 8 MB、宽高限 4096 像素，皮肤文件限 13 MB。PNG、JPEG、BMP 和 GIF 首帧均可使用。长时间编辑时建议导出重要皮肤作为备份。

上次保存的皮肤损坏时，程序会保留为 invalid-<编号>.wtskin.json 并显示默认皮肤；备份失败时暂停自动保存，用户仍可导出。恢复时从“导入”选择有效备份。要重置编辑器，退出后将 current.wtskin.json 移至其他位置，再重新打开。

已应用皮肤另存为 applied.wtskin.json。应用期间使用 applying.wtskin.json 记录尚未完成的事务，只有实际切换成功才提交；重开检测到未完成事务时会提示还原。编辑草稿不会即时覆盖原生显示；重新打开会读取应用记录并核对当前输入源，区分“正在使用 WeType Skin”与“当前使用其他输入源”。选择“还原官方”会先启用并验证官方微信拼音输入源，然后停止副本、禁用其拼音输入源并移除应用记录；官方切换失败时保留副本，不结束正在使用的输入法。关闭编辑器不会自动还原。

桌宠运行状态写入同一目录的 pet-status.json，仅包含进程 ID、动画名称、窗口位置和错误，不记录候选词或输入内容。候选框短暂消失时桌宠会保留位置约 240 毫秒，之后隐藏，进程继续等待下一次候选框出现。桌宠层级高于所跟随的候选窗口，避免被 Spotlight 遮挡。

## 原生适配现状

在 macOS 15.7.9 / Intel / 微信输入法 2.2.3（build 657）上完成了只读侦察：输入法由 AppKit/InputMethodKit 实现，包启用 Hardened Runtime 并由腾讯签名，没有找到可配置候选图片或工具条皮肤的公开接口。Windows 的 app.so / WXZ 补丁不能移植到这个实现。

原生实验使用 AppKit CandidateContentView / CandidatesPanel 的运行时视图 hook，在副本中改 CALayer 背景、边框、圆角、透明度和内嵌图片。它不会把输入法窗口替换成自绘候选框，因此保留文字、鼠标和原生布局。hook 通过副本的 ad-hoc entitlement 加载，官方包的 Developer ID 签名和文件哈希不会变化。

macOS 的输入源注册仍由系统管理。2026-10-06 已修复副本 Bundle ID，系统现在可枚举 `org.wetypeskinstudio.inputmethod.wetype.pinyin`。同时修复了已核对 Intel 2.2.3（657）副本的启动路径检查，并为实际 IMKServer 实现设置独立连接名。副本不再依赖终端环境变量加载皮肤组件。

2026-10-06 后续检查已确认系统选中副本拼音输入源，用户确认正常出词且真实候选框背景变为“暮紫”。初次注册曾出现 -50 和系统列表延迟发现，不能把注册返回 0 当作安装完成。编辑器必须实际切换输入源才会标记应用成功，失败时切回原输入源并停止副本。辅助功能授权不能替代这一步。系统列表中副本名称为 **WeType Skin**。未核对的输入法哈希及 Apple Silicon 原生补丁会被拒绝；编辑器预览不受影响。

### 已核对与待核对

本轮实际核对的是 Intel 2.2.3（657）的副本输入、候选背景颜色和浮窗跟随。图片、透明度、工具条样式、所有输入状态动画及长期稳定性尚未完成整体验收。应用同一版本的皮肤时复用已签名安装的副本，避免删除重建导致系统重新发现输入源。
