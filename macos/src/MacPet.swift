import AppKit
import CoreGraphics
import Carbon

@MainActor
private final class PetCanvas: NSView {
    private var image: NSImage
    private var clip: PetManifest.Clip
    private var motion = "idle"
    private var animationFrame = 0
    private var timer: Timer?

    override var isOpaque: Bool { false }

    init(image: NSImage, clip: PetManifest.Clip, animated: Bool) {
        self.image = image
        self.clip = clip
        super.init(frame: NSRect(x: 0, y: 0, width: 128, height: 128))
        autoresizingMask = [.width, .height]
        wantsLayer = true
        layer?.backgroundColor = NSColor.clear.cgColor
        layer?.isOpaque = false
        setAnimated(animated)
    }

    required init?(coder: NSCoder) { fatalError("PetCanvas does not support storyboards") }

    deinit { timer?.invalidate() }

    func play(_ name: String, directory: URL, manifest: PetManifest, animated: Bool) {
        guard motion != name, let next = manifest.clips.first(where: { $0.name == name }),
              let sheet = NSImage(contentsOf: directory.appendingPathComponent(next.file)) else { return }
        setAnimated(false)
        image = sheet; clip = next; motion = name; animationFrame = 0
        setAnimated(animated)
        needsDisplay = true
    }

    func setAnimated(_ animated: Bool) {
        guard animated != (timer != nil) else { return }
        timer?.invalidate()
        timer = nil
        if animated {
            let tick = Timer(timeInterval: 1.0 / Double(clip.fps), repeats: true) { [weak self] _ in
                Task { @MainActor in
                    guard let self else { return }
                    self.animationFrame = (self.animationFrame + 1) % self.clip.frames
                    self.needsDisplay = true
                }
            }
            timer = tick
            RunLoop.main.add(tick, forMode: .common)
        }
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        // Transparent sprite frames must replace the previous backing pixels.
        NSColor.clear.setFill()
        dirtyRect.fill(using: .copy)
        let size = CGFloat(clip.frameSize)
        let source = NSRect(x: CGFloat(animationFrame % clip.columns) * size,
                            y: image.size.height - CGFloat(animationFrame / clip.columns + 1) * size,
                            width: size, height: size)
        image.draw(in: bounds, from: source, operation: .sourceOver, fraction: 1,
                   respectFlipped: true, hints: [.interpolation: NSImageInterpolation.none])
    }
}

@MainActor
final class MacPet: NSObject, NSApplicationDelegate {
    private var window: NSPanel?
    private var statusItem: NSStatusItem?
    private var monitor: Timer?
    private var lastTheme: SkinTheme?
    private var themeDate: Date?
    private let store = ThemeStore.standard
    private let tracker = InputWindowTracker()
    private var manifest: PetManifest?
    private var petDirectory: URL?
    private var lastThemeCheck = Date.distantPast
    private var candidateSince = Date()
    private var lastCandidate = false
    private var lastSource: String?
    private var feedbackUntil = Date.distantPast
    private var wasVisible = false
    private var keyMonitor: Any?
    private var lastInput = Date.distantPast
    private var keyMotion = "typing"
    private var lastAnchorSeen = Date.distantPast
    private var lastStatus: PetRuntimeStatus?

    private func currentThemeDate() -> Date? {
        (try? store.currentURL.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        do { try show() }
        catch {
            PetRuntimeStatus(pid: getpid(), state: "error", motion: "idle", frame: nil,
                             anchor: nil, layer: nil, error: error.localizedDescription).save(in: store)
            NSLog("桌宠启动失败：%@", error.localizedDescription)
            NSApp.terminate(nil)
        }
    }

    private func show() throws {
        let theme = (try? store.load()) ?? SkinTheme()
        guard theme.petEnabled else { NSApp.terminate(nil); return }
        lastTheme = theme
        themeDate = currentThemeDate()
        guard let resources = StudioBundle.editor.resourceURL else { throw StudioError.invalid("桌宠资源目录不存在。") }
        let petDirectory = resources.appendingPathComponent("frontend/pet")
        let manifest = try JSONDecoder().decode(PetManifest.self,
                        from: ThemeStore.readLimited(petDirectory.appendingPathComponent("clips.json")))
        self.manifest = manifest
        self.petDirectory = petDirectory
        guard let clip = manifest.clips.first(where: { $0.name == "idle" }),
              clip.frames > 0, clip.columns > 0, clip.frameSize > 0, clip.fps > 0,
              clip.file == URL(fileURLWithPath: clip.file).lastPathComponent,
              let image = NSImage(contentsOf: petDirectory.appendingPathComponent(clip.file)) else {
            throw StudioError.invalid("桌宠待机动画无法加载。")
        }
        let size = CGFloat(theme.petSize)
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: size, height: size),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.isFloatingPanel = true
        panel.contentView = PetCanvas(image: image, clip: clip, animated: theme.petAnimate)
        window = panel
        updateAnchor()

        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.title = "鲸"
        let menu = NSMenu()
        menu.addItem(withTitle: "打开皮肤工作室", action: #selector(openEditor), keyEquivalent: "")
        menu.addItem(withTitle: "显示桌宠", action: #selector(showPet), keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(withTitle: "退出桌宠", action: #selector(terminatePet), keyEquivalent: "q")
        statusItem = item
        item.menu = menu
        keyMonitor = NSEvent.addGlobalMonitorForEvents(matching: .keyDown) { [weak self] event in
            Task { @MainActor in
                guard let self, self.lastCandidate, self.wasVisible else { return }
                // Key codes classify actions; never access characters or log
                // typed content. Permission denial leaves window following.
                self.lastInput = Date()
                switch event.keyCode {
                case 123, 124, 125, 126, 116, 121, 24, 27: self.keyMotion = "navigating"
                case 36, 49: self.keyMotion = "committed"
                case 53: self.keyMotion = "cancelled"
                default: self.keyMotion = "typing"
                }
            }
        }
        let timer = Timer(timeInterval: 0.12, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
        monitor = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    private func refresh() {
        if Date() >= lastThemeCheck {
            lastThemeCheck = Date().addingTimeInterval(1)
            let date = currentThemeDate()
            // ThemeStore validates embedded images; only reread a changed file so
            // an 8 MB wallpaper is not decoded every second in this small helper.
            if date != themeDate, let theme = try? store.load() {
                themeDate = date
                lastTheme = theme
                if !theme.petEnabled { terminatePet(); return }
                (window?.contentView as? PetCanvas)?.setAnimated(theme.petAnimate)
                window?.setContentSize(NSSize(width: CGFloat(theme.petSize), height: CGFloat(theme.petSize)))
            }
        }
        updateAnchor()
    }

    private func updateAnchor() {
        guard let window else { return }
        let theme = lastTheme ?? SkinTheme()
        let anchors = tracker.visibleAnchors()
        guard let anchor = anchors.first(where: { !$0.toolbar && theme.petCandidate })
            ?? anchors.first(where: { $0.toolbar && theme.petToolbar }) else {
            // Candidate pagination and app switching may briefly remove a
            // window. Keep its last position for one short transition.
            if wasVisible && Date().timeIntervalSince(lastAnchorSeen) < 0.24 { return }
            if wasVisible { window.orderOut(nil); wasVisible = false }
            (window.contentView as? PetCanvas)?.setAnimated(false)
            lastCandidate = false
            publishStatus(state: "hidden", motion: "idle", anchor: nil)
            return
        }
        lastAnchorSeen = Date()
        let candidate = !anchor.toolbar
        if candidate && !lastCandidate { candidateSince = Date() }
        lastCandidate = candidate
        let elapsed = Date().timeIntervalSince(candidateSince)
        var motion = candidate ? (elapsed < 1.5 ? "typing" : elapsed < 8 ? "thinking" : "rice") : "idle"
        if candidate && Date().timeIntervalSince(lastInput) < 1 { motion = keyMotion }
        if let source = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(),
           let raw = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) {
            let id = Unmanaged<CFString>.fromOpaque(raw).takeUnretainedValue() as String
            if let previous = lastSource, previous != id {
                motion = id.contains("wetype") || id.contains("pinyin") ? "mode-chinese" : "mode-english"
                feedbackUntil = Date().addingTimeInterval(2)
            } else if Date() < feedbackUntil { motion = "" }
            lastSource = id
        }
        if let canvas = window.contentView as? PetCanvas, let manifest, let petDirectory {
            if !motion.isEmpty { canvas.play(motion, directory: petDirectory, manifest: manifest, animated: theme.petAnimate) }
            canvas.setAnimated(theme.petAnimate)
        }
        let screen = NSScreen.screens.first(where: { $0.frame.intersects(anchor.frame) }) ?? NSScreen.main
        let visible = screen?.visibleFrame ?? .zero
        let point = InputWindowTracker.petOrigin(for: anchor, size: window.frame.size, visible: visible)
        let level = InputWindowTracker.petLevel(for: anchor)
        let moved = window.frame.origin != point || window.level != level
        window.level = level
        if window.frame.origin != point { window.setFrameOrigin(point) }
        if !wasVisible || moved { window.orderFrontRegardless(); wasVisible = true }
        publishStatus(state: "visible", motion: motion.isEmpty ? lastStatus?.motion ?? "idle" : motion, anchor: anchor)
    }

    private func publishStatus(state: String, motion: String, anchor: InputWindowTracker.Anchor?) {
        func rect(_ value: NSRect) -> [Double] {
            [Double(value.origin.x), Double(value.origin.y), Double(value.width), Double(value.height)]
        }
        let status = PetRuntimeStatus(pid: getpid(), state: state, motion: motion,
                                      frame: window.map { rect($0.frame) }, anchor: anchor.map { rect($0.frame) },
                                      layer: window?.level.rawValue, error: nil)
        if status != lastStatus { status.save(in: store); lastStatus = status }
    }

    @objc private func showPet() { updateAnchor() }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        openEditor()
        return false
    }

    @objc private func openEditor() {
        if let pid = InstanceLock.runningPID(directory: store.directory, role: "editor") {
            NSRunningApplication(processIdentifier: pid)?.activate(options: [])
            return
        }
        guard let executable = StudioBundle.editor.executableURL else { return }
        do {
            let editor = Process()
            editor.executableURL = executable
            editor.standardInput = FileHandle.nullDevice
            editor.standardOutput = FileHandle.nullDevice
            editor.standardError = FileHandle.nullDevice
            try editor.run()
        } catch { NSLog("编辑器启动失败：%@", error.localizedDescription) }
    }

    @objc private func terminatePet() { NSApp.terminate(nil) }
}
