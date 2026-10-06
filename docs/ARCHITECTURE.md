# 架构说明

## 组件

`WeTypeSkinStudio.exe` 是 WinForms 外壳。它嵌入 WebView2 SDK、HTML/CSS/JavaScript 编辑器、皮肤预设和独立桌宠组件，启动时把前端资源释放到 `%LOCALAPPDATA%\WeTypeSkinStudio\frontend`，页面通过本地 WebView2 消息桥与 C# 通信。

`NativeSkinService` 是原生变更的事务协调器。它同时管理两份独立文件：微信 Flutter 运行文件 `app.so` 和工具条资源所在的 `wetype_update.exe`。普通权限编辑器负责准备请求、触发输入法重启和确认运行状态；管理员助手只负责停止服务、替换文件和回滚。

`NativePatchStore` 与 `NativeToolbarStore` 各自维护官方备份、当前哈希、待提交哈希和皮肤记录。`NativeSkinService.InspectDetailed()` 会同时读取两者。只有候选框和工具条都为官方，或两者都记录了同一套皮肤，编辑器才会把状态标记为完整可用；部分应用状态只能进入还原流程，避免误报为成功。

`DeepSeekChan.InputPet.exe` 是独立进程。它不引用 WebView2，也不拥有原生皮肤写入权限。工作室关闭后，桌宠进程仍可运行；工作室再次打开时只读取和同步状态。桌宠位置跟随微信窗口，点击穿透窗口不改变候选框 HWND。

## 数据流

```text
前端编辑器
    -> WebView2 消息桥
    -> FrontendEditor
    -> ThemeStore (%LOCALAPPDATA%/WeTypeSkinStudio/current.wtskin.json)
    -> NativeSkinService
       -> 普通权限请求文件
       -> 管理员写入助手
          -> app.so / wetype_update.exe
       -> 普通权限重启与候选窗口检查

FrontendEditor <-> PetWorker <-> runtime JSON <-> DeepSeekChan.InputPet.exe
```

## macOS 编辑器

Mac 宿主独立于 Windows 补丁实现，使用 AppKit 和系统 WKWebView。Swift 的 SkinTheme / ThemeStore 与 Windows 版本 1 皮肤格式保持字段和默认值一致。保存目录为 ~/Library/Application Support/WeTypeSkinStudio；图片校验使用 ImageIO，文件通过原子替换保存。关闭窗口或 ⌘Q 时，宿主取回最新前端快照并保存，失败则取消退出。

消息桥只接收主页面的主框架请求，不允许把 WebView 导航到其他页面。Mac 原生文件面板分别处理皮肤导入和图片选择；取消导出会返回 cancelled，不报保存成功。前端收到 platform=macOS 后显示副本实验状态；后端 apply 会复制到 ~/Library/Input Methods、加载副本 hook，并验证 TIS 输入源列表，注册或实际切换失败就恢复原输入源、停止副本并返回错误。restore 先恢复原输入源，再停止副本，不接触官方包。独立 MacPet 进程由编辑器启动，编辑器退出不会结束它。

macos/build.sh 只用系统工具构建当前架构的应用包，不启动服务，也不下载依赖。Contents/Helpers/WeType Skin Pet.app 有独立的 Bundle ID，动画资源从父应用包读取，进程使用独立锁与空的标准输入输出，不依赖编辑器或开发目录。Swift 编译的 WeTypeSkinPrepare 随应用提供，运行时无需 Python 或开发工具；它在临时副本完成版本核对、补丁与签名验证后才替换用户副本。Mac 版只重签用户目录中的输入法副本；官方微信输入法不写入、不重新签名、不重启。原生 hook 不替换候选窗口。

Mac 皮肤草稿保存在 current.wtskin.json，原生 hook 读取 applied.wtskin.json。编辑器重开时结合应用记录和 TIS 当前输入源显示状态。还原事务必须先实际切换到官方微信拼音输入源，随后才能停止副本；切换失败时不停止副本。桌宠依据窗口所属进程和几何位置定位，其层级跟随候选窗口。状态文件 pet-status.json 供界面显示真实运行状态，不包含输入文字。

## Windows 版本保护

补丁偏移、资源槽位和官方文件哈希只对微信输入法 `2.1.4.6` 的已核对安装成立。每个修改点带有 expected bytes 检查；未知版本、外部修改或备份不一致时拒绝写入。这些偏移是针对该版本文件核验得到的实现细节，不是通用 API。

## Windows 原生补丁失败与恢复

写入前先建立备份和事务状态。写入或启动验证失败时，两份文件一起回滚；如果回滚自身失败，状态会标记为需要恢复，并保留操作审计。用户可以重新打开工作室执行“还原官方”，或按 README 的恢复命令处理。工作室关闭不会自动还原已应用皮肤。
