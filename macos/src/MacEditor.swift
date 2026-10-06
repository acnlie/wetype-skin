import AppKit
import WebKit
import UniformTypeIdentifiers
import Carbon

private struct BridgeRequest: Decodable {
    let id: String
    let action: String
    let theme: SkinTheme?
}

private struct EditorState: Encodable {
    let current: SkinTheme
    let presets = SkinTheme.presets
    let platform = "macOS"
    let nativeSupported: Bool
    let petSupported = true
    let applied: SkinTheme?
    let compatible: Bool
    let recoveryRequired: Bool
    let operationError: String?
    let pet: PetState
    let version: String
    let cloneRunning: Bool
    let message: String
    let cancelled: Bool
}

private struct BridgeReply: Encodable {
    let id: String
    let ok: Bool
    let result: EditorState
    let error: String?
}

private struct ProgressReply: Encodable {
    let id: String
    let progress = true
    let action: String
    let step: Int
    let total: Int
    let progressMessage: String
}

struct PetManifest: Decodable {
    struct Clip: Decodable {
        let name: String
        let file: String
        let frameSize: Int
        let columns: Int
        let frames: Int
        let fps: Int
    }
    let clips: [Clip]
}

private struct PetState: Encodable {
    let state: String
    let motion: String
    let enabled: Bool
    let error: String?
}

@MainActor
final class MacEditor: NSObject, NSApplicationDelegate, NSWindowDelegate, WKScriptMessageHandler, WKUIDelegate, WKNavigationDelegate {
    private(set) var window: NSWindow?
    private(set) var webView: WKWebView?
    private let store: ThemeStore
    private let resources: URL?
    private var current = SkinTheme()
    private var startupMessage = "macOS：编辑器、副本原生实验和独立桌宠可用；官方输入法保持不变。"
    private var ready = false
    private var panelBusy = false
    private var closing = false
    private var terminationPending = false
    private var mayReplaceCurrent = true
    private var indexURL: URL?
    private var nativeAvailable = false
    private var cloneRunning = false
    private var nativeBusy = false
    private var applied: SkinTheme?
    private var operationError: String?
    private var petProcess: Process?
    private var petError: String?
    private var stateMonitor: Timer?
    private var observedSource: String?
    private var recoveryRequired = false

    init(store: ThemeStore = .standard, resources: URL? = Bundle.main.resourceURL) {
        self.store = store
        self.resources = resources
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        do { try start() }
        catch { showError("无法启动 Mac 编辑器", error); NSApp.terminate(nil) }
    }

    func start() throws {
        guard let resources else { throw StudioError.invalid("应用包缺少 Resources 目录，请重新构建。") }
        let frontend = resources.appendingPathComponent("frontend", isDirectory: true)
        let index = frontend.appendingPathComponent("index.html")
        for file in ["index.html", "studio.css", "studio.js", "lucide.min.js"] {
            guard FileManager.default.fileExists(atPath: frontend.appendingPathComponent(file).path) else {
                throw StudioError.invalid("应用包缺少前端文件：\(file)")
            }
        }
        try FileManager.default.createDirectory(at: store.directory, withIntermediateDirectories: true)
        nativeAvailable = FileManager.default.fileExists(atPath: resources.appendingPathComponent("native/libWeTypeSkinHook.dylib").path)
        if FileManager.default.fileExists(atPath: store.currentURL.path) {
            do { current = try store.load() }
            catch {
                let failure = error.localizedDescription
                do {
                    let preserved = try store.preserveInvalidCurrent()
                    startupMessage = "\(failure) 已保留原文件：\(preserved.lastPathComponent)。当前显示默认皮肤。"
                } catch {
                    mayReplaceCurrent = false
                    startupMessage = "\(failure) 原文件无法备份，请先处理保存目录；本次仍可预览和导出。"
                }
            }
        }

        refreshNativeState(migrateLegacy: true)

        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        let controller = WKUserContentController()
        controller.add(self, name: "wetype")
        controller.addUserScript(WKUserScript(source: try bridgeScript(frontend: frontend),
                                              injectionTime: .atDocumentStart, forMainFrameOnly: true))
        configuration.userContentController = controller
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.uiDelegate = self
        view.navigationDelegate = self
        webView = view
        indexURL = index.standardizedFileURL

        let frame = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let size = NSSize(width: min(1180, frame.width), height: min(800, frame.height))
        let editorWindow = NSWindow(contentRect: NSRect(origin: .zero, size: size),
                                    styleMask: [.titled, .closable, .miniaturizable, .resizable],
                                    backing: .buffered, defer: false)
        editorWindow.title = "微信输入法皮肤工作室 · Mac 编辑版"
        editorWindow.contentView = view
        editorWindow.delegate = self
        editorWindow.isReleasedWhenClosed = false
        editorWindow.minSize = NSSize(width: min(900, frame.width), height: min(600, frame.height))
        editorWindow.center()
        window = editorWindow
        installMenu()
        view.loadFileURL(index, allowingReadAccessTo: frontend)
        editorWindow.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            do { try self.startPetIfEnabled() }
            catch { self.petError = error.localizedDescription; NSLog("独立桌宠启动失败：%@", error.localizedDescription) }
        }
        let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, self.ready, !self.nativeBusy, !self.closing else { return }
                if self.observedSource != self.currentSourceID() {
                    self.refreshNativeState()
                    self.reply("installation-event", ok: true)
                }
                self.sendPetState()
            }
        }
        stateMonitor = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.frameInfo.isMainFrame,
              message.frameInfo.request.url?.standardizedFileURL == indexURL,
              let body = message.body as? [String: Any],
              let id = body["id"] as? String, (1...80).contains(id.utf16.count) else { return }
        do {
            let bytes = try JSONSerialization.data(withJSONObject: body)
            guard bytes.count <= ThemeStore.maximumFileSize else { throw StudioError.invalid("界面请求过大。") }
            let request = try JSONDecoder().decode(BridgeRequest.self, from: bytes)
            guard !panelBusy, !closing, !nativeBusy else { throw StudioError.invalid("请先完成当前操作。") }
            switch request.action {
            case "open-github", "open-x":
                let url = URL(string: request.action == "open-github" ? "https://github.com/acnlie/wetype-skin" : "https://x.com/Tokupup")!
                guard NSWorkspace.shared.open(url) else { throw StudioError.invalid("无法打开默认浏览器。") }
                reply(id, ok: true)
            case "bootstrap":
                ready = true
                refreshNativeState(migrateLegacy: true)
                reply(id, ok: true)
            case "save":
                let theme = try requireTheme(request.theme)
                try persist(theme)
                try syncPet(theme)
                reply(id, ok: true)
            case "import":
                chooseFile(id: id, imageTheme: nil)
            case "image":
                chooseFile(id: id, imageTheme: try requireTheme(request.theme))
            case "export":
                export(id: id, theme: try requireTheme(request.theme))
            case "pet-state":
                reply(id, ok: true)
            case "apply":
                let theme = try requireTheme(request.theme)
                beginNativeOperation(request.id, action: "apply", theme: theme)
            case "restore":
                beginNativeOperation(request.id, action: "restore", theme: nil)
            default:
                throw StudioError.invalid("未知界面操作。")
            }
        } catch {
            operationError = error.localizedDescription
            reply(id, ok: false, error: error.localizedDescription)
        }
    }

    private func requireTheme(_ theme: SkinTheme?) throws -> SkinTheme {
        guard let theme else { throw StudioError.invalid("皮肤内容为空。") }
        try theme.validate()
        return theme
    }

    private func persist(_ theme: SkinTheme) throws {
        guard mayReplaceCurrent else {
            throw StudioError.invalid("原皮肤文件未能备份，已暂停自动保存。请导出皮肤并检查保存目录权限。")
        }
        try store.save(theme)
        current = theme
    }

    private var nativeDirectory: URL { resources!.appendingPathComponent("native", isDirectory: true) }
    private var cloneURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Input Methods/WeTypeSkinStudioInputMethod.app")
    }
    private func beginNativeOperation(_ id: String, action: String, theme: SkinTheme?) {
        nativeBusy = true
        Task { @MainActor in
            defer { nativeBusy = false }
            do {
                sendProgress(id, action: action, step: 1, message: "检查当前皮肤与副本状态")
                if let theme {
                    try persist(theme)
                    try await startNativeClone { step, message in
                        self.sendProgress(id, action: action, step: step, message: message)
                    }
                    try syncPet(theme)
                } else {
                    sendProgress(id, action: action, step: 2, message: "先切回并确认官方微信输入源")
                    try await stopNativeClone {
                        self.sendProgress(id, action: action, step: 3, message: "官方输入源已确认，正在停止皮肤副本")
                    }
                    sendProgress(id, action: action, step: 4, message: "确认官方微信输入法文件未修改")
                }
                sendProgress(id, action: action, step: 5, message: startupMessage)
                operationError = nil
                reply(id, ok: true)
            } catch {
                operationError = error.localizedDescription
                recoveryRequired = FileManager.default.fileExists(atPath: pendingURL.path)
                reply(id, ok: false, error: error.localizedDescription)
            }
        }
    }

    private func runNativeScript(_ name: String) async throws {
        guard nativeAvailable else { throw StudioError.invalid("Mac 原生组件未随应用包提供，请重新构建。") }
        let script = nativeDirectory.appendingPathComponent(name).path
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            DispatchQueue.global(qos: .utility).async {
                do {
                    let process = Process()
                    process.executableURL = URL(fileURLWithPath: "/bin/bash")
                    process.arguments = [script]
                    process.standardOutput = FileHandle.nullDevice
                    let error = Pipe(); process.standardError = error
                    try process.run()
                    // Drain stderr while running: a full pipe must not block
                    // codesign. Waiting stays off the AppKit/WKWebView thread.
                    let bytes = error.fileHandleForReading.readDataToEndOfFile()
                    process.waitUntilExit()
                    guard process.terminationStatus == 0 else {
                        let detail = (String(data: bytes, encoding: .utf8) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                        throw StudioError.invalid(detail.isEmpty ? "Mac 输入法副本操作失败。" : detail)
                    }
                    continuation.resume()
                } catch { continuation.resume(throwing: error) }
            }
        }
    }
    private let cloneSourceID = "org.wetypeskinstudio.inputmethod.wetype.pinyin"
    private let officialSourceID = "com.tencent.inputmethod.wetype.pinyin"
    private var appliedURL: URL { store.directory.appendingPathComponent("applied.wtskin.json") }
    private var pendingURL: URL { store.directory.appendingPathComponent("applying.wtskin.json") }

    private func usingClone(_ identifier: String?) -> Bool {
        identifier == "org.wetypeskinstudio.inputmethod.wetype"
            || identifier?.hasPrefix("org.wetypeskinstudio.inputmethod.wetype.") == true
    }

    private func refreshNativeState(migrateLegacy: Bool = false) {
        observedSource = currentSourceID()
        cloneRunning = usingClone(observedSource)
        applied = try? store.load(appliedURL)
        let pending = FileManager.default.fileExists(atPath: pendingURL.path)
        recoveryRequired = pending || (FileManager.default.fileExists(atPath: appliedURL.path) && applied == nil)
        if cloneRunning, applied == nil, !pending, migrateLegacy, mayReplaceCurrent,
           !FileManager.default.fileExists(atPath: appliedURL.path), let legacy = try? store.load() {
            // Older builds applied current.wtskin.json directly. Migrate only
            // when the system actually reports the skin input source selected.
            do { try store.save(legacy, to: appliedURL); applied = legacy }
            catch { operationError = error.localizedDescription }
        }
        if cloneRunning {
            recoveryRequired = pending || applied == nil
            startupMessage = "已应用皮肤，当前使用 WeType Skin。"
        } else if applied != nil {
            recoveryRequired = pending || !FileManager.default.fileExists(atPath: cloneURL.path)
            startupMessage = "已保存应用状态，当前使用其他输入源；可以还原官方。"
        }
    }

    private func inputSource(_ identifier: String) -> TISInputSource? {
        guard let list = TISCreateInputSourceList(nil, true)?.takeRetainedValue() else { return nil }
        for index in 0..<CFArrayGetCount(list) {
            let source = unsafeBitCast(CFArrayGetValueAtIndex(list, index), to: TISInputSource.self)
            if sourceID(source) == identifier { return source }
        }
        return nil
    }

    private func sourceID(_ source: TISInputSource) -> String? {
        guard let raw = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { return nil }
        return Unmanaged<CFString>.fromOpaque(raw).takeUnretainedValue() as String
    }

    private func currentSourceID() -> String? {
        TISCopyCurrentKeyboardInputSource().map { sourceID($0.takeRetainedValue()) } ?? nil
    }

    private func selectSource(_ identifier: String) throws {
        if currentSourceID() == identifier { return }
        let parentID = identifier == cloneSourceID ? "org.wetypeskinstudio.inputmethod.wetype"
            : identifier == officialSourceID ? "com.tencent.inputmethod.wetype" : nil
        if let parentID, let parent = inputSource(parentID) {
            guard TISEnableInputSource(parent) == noErr else {
                throw StudioError.invalid("无法启用输入法的父输入源。")
            }
        }
        guard let source = inputSource(identifier), TISEnableInputSource(source) == noErr,
              let enabled = inputSource(identifier) else {
            throw StudioError.invalid("输入源尚未注册或无法启用：\(identifier)")
        }
        let result = TISSelectInputSource(enabled)
        guard result == noErr, currentSourceID() == identifier else {
            throw StudioError.invalid("输入源切换未成功（\(result)），未将皮肤标记为已应用。")
        }
    }

    private func startNativeClone(progress: ((Int, String) -> Void)? = nil) async throws {
        do {
            try store.save(current, to: pendingURL)
            // A version upgrade may replace the copy. Never replace a server
            // while it is the selected input source.
            if usingClone(currentSourceID()) { try selectSource(officialSourceID) }
            progress?(2, "检查已安装的皮肤输入法，仅在需要升级时重建")
            try await runNativeScript("run-clone.sh")
            progress?(3, "注册独立输入源")
            if inputSource(cloneSourceID) == nil {
                let result = TISRegisterInputSource(cloneURL as CFURL)
                guard result == noErr else { throw StudioError.invalid("输入源注册失败（\(result)）。") }
            }
            progress?(4, "启用并切换到皮肤输入法")
            try selectSource(cloneSourceID)
            try store.save(current, to: appliedURL)
            try FileManager.default.removeItem(at: pendingURL)
            cloneRunning = true
            applied = current
            observedSource = currentSourceID()
            recoveryRequired = false
            startupMessage = "已应用皮肤，当前使用 WeType Skin。"
        } catch {
            let failure = error
            do { try await stopNativeClone() }
            catch { throw StudioError.invalid("\(failure.localizedDescription)；恢复未完成：\(error.localizedDescription)") }
            throw failure
        }
    }

    private func stopNativeClone(beforeStop: (() -> Void)? = nil) async throws {
        // A saved previous source may be ABC or stale. Restoration always
        // selects and verifies the official source before stopping anything.
        try selectSource(officialSourceID)
        guard currentSourceID() == officialSourceID else {
            throw StudioError.invalid("官方微信输入源尚未切换成功，保留皮肤副本运行。")
        }
        beforeStop?()
        try await runNativeScript("stop-clone.sh")
        if let source = inputSource(cloneSourceID), TISDisableInputSource(source) != noErr {
            recoveryRequired = true
            throw StudioError.invalid("官方输入源已恢复，但皮肤输入源未能禁用，请重试还原。")
        }
        if FileManager.default.fileExists(atPath: appliedURL.path) { try FileManager.default.removeItem(at: appliedURL) }
        if FileManager.default.fileExists(atPath: pendingURL.path) { try FileManager.default.removeItem(at: pendingURL) }
        cloneRunning = false
        observedSource = currentSourceID()
        applied = nil
        recoveryRequired = false
        startupMessage = "已切回官方微信输入法并停止皮肤副本。"
    }

    private func startPetIfEnabled() throws {
        // A UI harness also hosts MacEditor. Never launch that harness as a
        // pet; only the real app executable implements the --pet entry point.
        guard Bundle.main.bundleIdentifier == "org.wetypeskinstudio.editor",
              current.petEnabled, petProcess?.isRunning != true,
              InstanceLock.runningPID(directory: store.directory, role: "pet") == nil else { return }
        guard let executable = StudioBundle.helperExecutable,
              FileManager.default.isExecutableFile(atPath: executable.path) else {
            throw StudioError.invalid("应用包缺少独立桌宠组件，请重新安装。")
        }
        let process = Process()
        process.executableURL = executable
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        petProcess = process
        petError = nil
    }

    private func syncPet(_ theme: SkinTheme) throws {
        if theme.petEnabled { try startPetIfEnabled() }
        else if let pid = InstanceLock.runningPID(directory: store.directory, role: "pet") {
            NSRunningApplication(processIdentifier: pid)?.terminate()
            self.petProcess = nil
        }
    }

    private func chooseFile(id: String, imageTheme: SkinTheme?) {
        guard let window else { return }
        panelBusy = true
        let panel = NSOpenPanel()
        panel.title = imageTheme == nil ? "导入皮肤" : "选择背景图片"
        panel.allowedContentTypes = imageTheme == nil ? [.json] : [.png, .jpeg, .bmp, .gif]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.beginSheetModal(for: window) { [weak self] result in
            guard let self else { return }
            self.panelBusy = false
            guard result == .OK, let url = panel.url else { self.reply(id, ok: true, cancelled: true); return }
            do {
                var theme: SkinTheme
                if let imageTheme {
                    theme = imageTheme
                    try theme.setImage(url)
                } else { theme = try self.store.load(url) }
                try self.persist(theme)
                try self.syncPet(theme)
                self.reply(id, ok: true)
            } catch { self.reply(id, ok: false, error: error.localizedDescription) }
        }
    }

    private func export(id: String, theme: SkinTheme) {
        guard let window else { return }
        panelBusy = true
        let panel = NSSavePanel()
        panel.title = "导出皮肤"
        panel.allowedContentTypes = [.json]
        let invalid = CharacterSet(charactersIn: "/:\\").union(.controlCharacters)
        panel.nameFieldStringValue = theme.name.components(separatedBy: invalid).joined(separator: "_") + ".wtskin.json"
        panel.beginSheetModal(for: window) { [weak self] result in
            guard let self else { return }
            self.panelBusy = false
            guard result == .OK, let url = panel.url else { self.reply(id, ok: true, cancelled: true); return }
            do {
                try self.store.save(theme, to: url)
                self.reply(id, ok: true)
            } catch { self.reply(id, ok: false, error: error.localizedDescription) }
        }
    }

    private func reply(_ id: String, ok: Bool, cancelled: Bool = false, error: String? = nil) {
        let state = EditorState(current: current, nativeSupported: nativeAvailable,
                                applied: applied, compatible: nativeAvailable,
                                recoveryRequired: recoveryRequired, operationError: operationError,
                                pet: currentPetState(),
                                version: "Mac 原生副本", cloneRunning: cloneRunning,
                                message: startupMessage, cancelled: cancelled)
        let response = BridgeReply(id: id, ok: ok, result: state, error: error)
        guard let bytes = try? JSONEncoder().encode(response),
              let object = try? JSONSerialization.jsonObject(with: bytes) else { return }
        sendToPage(object)
    }

    private func currentPetState() -> PetState {
        let petPID = InstanceLock.runningPID(directory: store.directory, role: "pet")
        let petStatus = PetRuntimeStatus.load(in: store)
        let runningStatus = petStatus?.pid == petPID ? petStatus : nil
        let launchError = petPID == nil && petStatus?.state == "error" ? petStatus?.error : nil
        return PetState(state: petPID == nil ? "stopped" : runningStatus?.state ?? "starting",
                                              motion: runningStatus?.motion ?? "idle", enabled: current.petEnabled,
                                              error: petPID == nil ? petError ?? launchError : runningStatus?.error)
    }

    private func sendPetState() {
        // The heartbeat must not encode and resend a multi-megabyte wallpaper.
        guard let bytes = try? JSONEncoder().encode(currentPetState()),
              let object = try? JSONSerialization.jsonObject(with: bytes) else { return }
        sendToPage(["id": "pet-event", "pet": object])
    }

    private func sendProgress(_ id: String, action: String, step: Int, message: String) {
        let response = ProgressReply(id: id, action: action, step: step, total: 5,
                                     progressMessage: message)
        guard let bytes = try? JSONEncoder().encode(response),
              let object = try? JSONSerialization.jsonObject(with: bytes) else { return }
        sendToPage(object)
    }

    private func sendToPage(_ object: Any) {
        webView?.callAsyncJavaScript("window.studioHost.receive(response)", arguments: ["response": object],
                                     in: nil, in: .page) { result in
            if case .failure(let error) = result { NSLog("编辑器回复失败：%@", error.localizedDescription) }
        }
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        let isEditor = navigationAction.request.url?.standardizedFileURL == indexURL
        decisionHandler(isEditor && navigationAction.targetFrame?.isMainFrame == true ? .allow : .cancel)
    }

    func webView(_ webView: WKWebView, runOpenPanelWith parameters: WKOpenPanelParameters,
                 initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping ([URL]?) -> Void) {
        // File inputs remain a fallback. Normal Mac buttons use the typed
        // native panels above so skins and images get different filters.
        guard let window, !panelBusy, frame.isMainFrame else { completionHandler(nil); return }
        panelBusy = true
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.json, .png, .jpeg, .bmp, .gif]
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = parameters.allowsMultipleSelection
        panel.beginSheetModal(for: window) { [weak self] result in
            self?.panelBusy = false
            completionHandler(result == .OK ? panel.urls : nil)
        }
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        showError("编辑器页面加载失败", error)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        showError("编辑器页面加载失败", error)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        NSApp.terminate(nil)
        return false
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard !closing, !panelBusy, !nativeBusy else { return .terminateCancel }
        guard ready, let webView else { return .terminateNow }
        closing = true
        terminationPending = true
        webView.evaluateJavaScript("window.studioPrepareClose()") { [weak self] value, error in
            guard let self, self.terminationPending else { return }
            do {
                if let error { throw error }
                guard let value, !(value is NSNull) else {
                    throw StudioError.invalid("名称或颜色无效，请修正后再关闭。")
                }
                let bytes = try JSONSerialization.data(withJSONObject: value)
                let theme = try ThemeStore.decode(bytes)
                // Do not overwrite a corrupt saved file merely by opening and closing.
                if theme != self.current { try self.persist(theme) }
                self.terminationPending = false
                NSApp.reply(toApplicationShouldTerminate: true)
            } catch { self.cancelTermination(error) }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 8) { [weak self] in
            guard let self, self.terminationPending else { return }
            self.cancelTermination(StudioError.invalid("保存页面响应超时，编辑器保持打开。"))
        }
        return .terminateLater
    }

    private func cancelTermination(_ error: Error) {
        terminationPending = false
        closing = false
        webView?.evaluateJavaScript("window.studioCancelClose()")
        NSApp.reply(toApplicationShouldTerminate: false)
        showError("保存未完成", error)
    }

    private func showError(_ title: String, _ error: Error) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = error.localizedDescription
        alert.alertStyle = .warning
        alert.addButton(withTitle: "好")
        if let window { alert.beginSheetModal(for: window) }
        else { alert.runModal() }
    }

    private func bridgeScript(frontend: URL) throws -> String {
        let manifest = try JSONDecoder().decode(PetManifest.self,
                        from: ThemeStore.readLimited(frontend.appendingPathComponent("pet/clips.json")))
        var clips: [String: [String: Any]] = [:]
        for clip in manifest.clips {
            guard URL(fileURLWithPath: clip.file).lastPathComponent == clip.file,
                  clip.columns > 0, clip.frames > 0, clip.fps > 0 else {
                throw StudioError.invalid("桌宠动画清单无效。")
            }
            clips[clip.name] = ["frames": clip.frames, "columns": clip.columns, "size": clip.frameSize,
                               "fps": clip.fps, "url": "pet/" + clip.file]
        }
        let data = try JSONSerialization.data(withJSONObject: clips, options: [.sortedKeys])
        guard let json = String(data: data, encoding: .utf8) else { throw StudioError.invalid("动画清单编码失败。") }
        return """
        (() => {
          const listeners = [];
          window.studioPlatform = { platform: 'macOS', nativeSupported: true, petSupported: true };
          window.studioHost = {
            postMessage: (message) => window.webkit.messageHandlers.wetype.postMessage(message),
            addEventListener: (type, listener) => { if (type === 'message') listeners.push(listener); },
            receive: (data) => listeners.slice().forEach((listener) => listener({ data }))
          };
          window.petAssets = \(json);
        })();
        """
    }

    private func installMenu() {
        let menu = NSMenu()
        let applicationItem = NSMenuItem()
        let applicationMenu = NSMenu()
        applicationMenu.addItem(withTitle: "退出皮肤工作室", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        applicationItem.submenu = applicationMenu
        menu.addItem(applicationItem)
        let editItem = NSMenuItem()
        let editMenu = NSMenu(title: "编辑")
        for (title, action, key) in [("撤销", "undo:", "z"), ("剪切", "cut:", "x"),
                                    ("复制", "copy:", "c"), ("粘贴", "paste:", "v"), ("全选", "selectAll:", "a")] {
            editMenu.addItem(withTitle: title, action: Selector(action), keyEquivalent: key)
        }
        editItem.submenu = editMenu
        menu.addItem(editItem)
        NSApp.mainMenu = menu
    }
}
