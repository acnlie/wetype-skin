import AppKit
import WebKit
import Carbon

// One opt-in integration run against the application installed from the DMG.
// It does not read typed text and leaves the user's original skin applied.
@MainActor
final class LifecycleChecks: NSObject, NSApplicationDelegate {
    private var editor: MacEditor?
    private let store = ThemeStore.standard
    private let cloneID = "org.wetypeskinstudio.inputmethod.wetype.pinyin"
    private let officialID = "com.tencent.inputmethod.wetype.pinyin"

    func applicationDidFinishLaunching(_ notification: Notification) {
        Task { @MainActor in
            do { try await run(); print("PASS: installed helper launch, independent exit/reopen, applied-state detection, official-source-first restore, candidate geometry and level"); exit(0) }
            catch { fputs("FAIL: \(error.localizedDescription)\n", stderr); exit(1) }
        }
    }

    private func require(_ condition: @autoclosure () -> Bool, _ message: String) throws {
        if !condition() { throw StudioError.invalid(message) }
    }
    private func wait(_ message: String, until predicate: () -> Bool) async throws {
        let limit = Date().addingTimeInterval(25)
        while Date() < limit {
            if predicate() { return }
            try await Task.sleep(nanoseconds: 200_000_000)
        }
        throw StudioError.invalid(message)
    }
    private func sourceID() -> String? {
        guard let source = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(),
              let raw = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { return nil }
        return Unmanaged<CFString>.fromOpaque(raw).takeUnretainedValue() as String
    }
    private func select(_ id: String) throws {
        guard let list = TISCreateInputSourceList(nil, true)?.takeRetainedValue() else { throw StudioError.invalid("No input sources") }
        for i in 0..<CFArrayGetCount(list) {
            let source = unsafeBitCast(CFArrayGetValueAtIndex(list,i), to: TISInputSource.self)
            if let raw = TISGetInputSourceProperty(source, kTISPropertyInputSourceID),
               Unmanaged<CFString>.fromOpaque(raw).takeUnretainedValue() as String == id {
                _ = TISEnableInputSource(source)
                try require(TISSelectInputSource(source) == noErr && sourceID() == id, "Cannot select \(id)")
                return
            }
        }
        throw StudioError.invalid("Missing source: \(id)")
    }
    private func script(_ view: WKWebView, _ body: String) async throws -> Any {
        try await withCheckedThrowingContinuation { continuation in
            view.callAsyncJavaScript(body, arguments: [:], in: nil, in: .page) { continuation.resume(with: $0) }
        }
    }
    private func openEditor(resources: URL) async throws -> WKWebView {
        editor?.window?.orderOut(nil)
        editor?.webView?.configuration.userContentController.removeScriptMessageHandler(forName: "wetype")
        let next = MacEditor(store: store, resources: resources)
        editor = next
        try next.start()
        guard let view = next.webView else { throw StudioError.invalid("No editor view") }
        let limit = Date().addingTimeInterval(25)
        while Date() < limit {
            if (try? await script(view, "return !!window.studioSnapshot?.();")) as? Bool == true { return view }
            try await Task.sleep(nanoseconds: 200_000_000)
        }
        throw StudioError.invalid("Installed editor bootstrap timed out")
    }
    private func request(_ view: WKWebView, action: String) async throws -> [String: Any] {
        let value = try await script(view, """
        return await new Promise((resolve, reject) => {
          const id = 'lifecycle-\(action)';
          const timeout = setTimeout(() => reject(new Error('operation timeout')), 90000);
          window.studioHost.addEventListener('message', ({data}) => {
            if (data.id === id && !data.progress) { clearTimeout(timeout); resolve(data); }
          });
          window.studioHost.postMessage({id, action:'\(action)', ...('\(action)' === 'apply' ? {theme:window.studioSnapshot()} : {})});
        });
        """)
        guard let response = value as? [String:Any], response["ok"] as? Bool == true,
              let state = response["result"] as? [String:Any] else {
            throw StudioError.invalid("\(action) failed: \(value)")
        }
        return state
    }
    private func run() async throws {
        guard CommandLine.arguments.count == 2,
              let bundle = Bundle(path: CommandLine.arguments[1]), let resources = bundle.resourceURL else {
            throw StudioError.invalid("Pass the app installed from the DMG")
        }
        let helperBundle = bundle.bundleURL.appendingPathComponent("Contents/Helpers/WeType Skin Pet.app")
        guard let helper = Bundle(url: helperBundle), let executable = helper.executableURL else { throw StudioError.invalid("DMG helper missing") }
        try require(helper.bundleIdentifier == StudioBundle.helperIdentifier, "Helper identity not independent")
        let manifest = try JSONDecoder().decode(PetManifest.self, from: ThemeStore.readLimited(resources.appendingPathComponent("frontend/pet/clips.json")))
        for clip in manifest.clips {
            try require(FileManager.default.fileExists(atPath: resources.appendingPathComponent("frontend/pet/" + clip.file).path), "Missing clip \(clip.name)")
        }
        let original = try store.load()
        try require(original.petEnabled, "Enable the pet before this check")
        NSWorkspace.shared.openApplication(at: bundle.bundleURL, configuration: NSWorkspace.OpenConfiguration()) { _, _ in }
        try await wait("Installed editor did not launch") { InstanceLock.runningPID(directory: store.directory, role: "editor") != nil }
        try await wait("Installed helper did not launch") {
            guard let pid = InstanceLock.runningPID(directory: self.store.directory, role: "pet"),
                  let app = NSRunningApplication(processIdentifier: pid) else { return false }
            return app.executableURL?.standardizedFileURL == executable.standardizedFileURL
                && PetRuntimeStatus.load(in: self.store)?.pid == pid
        }
        let petPID = InstanceLock.runningPID(directory: store.directory, role: "pet")!
        let editorPID = InstanceLock.runningPID(directory: store.directory, role: "editor")!
        _ = NSRunningApplication(processIdentifier: editorPID)?.terminate()
        try await wait("Editor did not exit") { InstanceLock.runningPID(directory: self.store.directory, role: "editor") == nil }
        try require(InstanceLock.runningPID(directory: store.directory, role: "pet") == petPID, "Pet exited with editor")
        NSWorkspace.shared.openApplication(at: bundle.bundleURL, configuration: NSWorkspace.OpenConfiguration()) { _, _ in }
        try await wait("Editor did not reopen") { InstanceLock.runningPID(directory: self.store.directory, role: "editor") != nil }
        try require(InstanceLock.runningPID(directory: store.directory, role: "pet") == petPID, "Reopen duplicated pet")
        _ = NSRunningApplication(processIdentifier: InstanceLock.runningPID(directory: store.directory, role: "editor")!)?.terminate()
        try await wait("Reopened editor did not exit") { InstanceLock.runningPID(directory: self.store.directory, role: "editor") == nil }

        // Drive the production message handler and installed native scripts,
        // using the user's existing theme rather than a throwaway wallpaper.
        var view = try await openEditor(resources: resources)
        _ = try await request(view, action: "apply")
        try require(sourceID() == cloneID, "Apply did not select the clone")
        view = try await openEditor(resources: resources)
        try select(cloneID)
        let loaded = try await request(view, action: "bootstrap")
        try require(loaded["cloneRunning"] as? Bool == true && loaded["applied"] != nil, "Reopen lost applied state")
        let label = try await script(view, "return document.querySelector('#native-state').textContent;") as? String ?? ""
        try require(label.contains("已应用"), "UI did not display applied: \(label)")
        let pending = store.directory.appendingPathComponent("applying.wtskin.json")
        try store.save(original, to: pending)
        view = try await openEditor(resources: resources)
        try select(cloneID)
        let interrupted = try await request(view, action: "bootstrap")
        try require(interrupted["recoveryRequired"] as? Bool == true, "Interrupted application was reported as complete")
        _ = try await request(view, action: "restore")
        try require(sourceID() == officialID, "Restore did not select official WeType")
        try require((try? store.load(store.directory.appendingPathComponent("applied.wtskin.json"))) == nil, "Restore left applied record")
        try require(!FileManager.default.fileExists(atPath: pending.path), "Restore left an unfinished transaction")
        try await wait("Clone still running after restore") {
            !NSWorkspace.shared.runningApplications.contains { $0.bundleIdentifier == "org.wetypeskinstudio.inputmethod.wetype" }
        }
        view = try await openEditor(resources: resources)
        let restored = try await request(view, action: "bootstrap")
        try require(restored["applied"] == nil && restored["cloneRunning"] as? Bool == false, "Reopen misreported restored state")
        _ = try await request(view, action: "apply")
        let saved = try store.load()
        try require(saved == original, "Check changed saved skin")
        try require(InstanceLock.runningPID(directory: store.directory, role: "pet") == petPID, "Native restore stopped pet")

        let anchor = InputWindowTracker.Anchor(frame: NSRect(x: 350, y: 400, width: 510, height: 32), toolbar: false, layer: 101, windowNumber: 0)
        try require(InputWindowTracker.petLevel(for: anchor).rawValue > anchor.layer, "Pet is behind candidate window")
        let point = InputWindowTracker.petOrigin(for: anchor, size: NSSize(width: 88, height: 88), visible: NSRect(x: 0, y: 0, width: 1024, height: 768))
        try require(point == NSPoint(x: 772, y: 434), "Pet does not align above candidate right edge")
        editor?.window?.orderOut(nil)
        editor?.webView?.configuration.userContentController.removeScriptMessageHandler(forName: "wetype")
    }
}

@main enum LifecycleChecksMain {
    @MainActor static func main() {
        let application = NSApplication.shared
        let checks = LifecycleChecks()
        application.delegate = checks
        application.setActivationPolicy(.regular)
        withExtendedLifetime(checks) { application.run() }
    }
}
