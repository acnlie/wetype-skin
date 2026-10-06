import AppKit
import WebKit

@MainActor
final class EditorChecks: NSObject, NSApplicationDelegate {
    private var editor: MacEditor?
    private let arguments = CommandLine.arguments
    private let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("wetype-editor-check-\(UUID().uuidString)")

    func applicationDidFinishLaunching(_ notification: Notification) {
        Task { @MainActor in
            do { try await run(); finish(0) }
            catch { fputs("FAIL UI: \(error.localizedDescription)\n", stderr); finish(1) }
        }
    }

    private func run() async throws {
        guard arguments.count == 3 else { throw StudioError.invalid("Expected Resources and output paths.") }
        let resources = URL(fileURLWithPath: arguments[1])
        let output = URL(fileURLWithPath: arguments[2])
        let store = ThemeStore(directory: temporary)
        let first = MacEditor(store: store, resources: resources)
        editor = first
        try first.start()
        guard let view = first.webView else { throw StudioError.invalid("WKWebView 未创建。") }
        try await waitForEditor(view)
        let initial = try await script(view, """
        const q = (selector) => document.querySelector(selector);
        return {
          name: window.studioSnapshot().name,
          applyDisabled: q('#apply').disabled, restoreDisabled: q('#restore').disabled,
          platform: document.body.dataset.platform,
          previewCaption: q('#canvas-caption').textContent,
          nativeNotice: q('#native-state').textContent,
          startupHidden: q('#pet-startup').closest('label').hidden,
          presets: document.querySelectorAll('.preset').length
        };
        """) as? [String: Any]
        guard initial?["applyDisabled"] as? Bool == false, initial?["restoreDisabled"] as? Bool == false,
              initial?["platform"] as? String == "macOS", initial?["startupHidden"] as? Bool == true,
              initial?["presets"] as? Int == 6 else { throw StudioError.invalid("Mac 功能边界或初始化错误。") }

        let fixture = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 96, pixelsHigh: 32,
                                     bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                     isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        let blue = NSColor(deviceRed: 0.2, green: 0.5, blue: 0.9, alpha: 1)
        let pink = NSColor(deviceRed: 0.9, green: 0.3, blue: 0.6, alpha: 1)
        for x in 0..<96 { for y in 0..<32 { fixture.setColor(x < 48 ? blue : pink, atX: x, y: y) } }
        let png = fixture.representation(using: .png, properties: [:])!
        _ = try await script(view, """
        const input = document.querySelector('#theme-name');
        input.value = 'Mac 集成检查'; input.dispatchEvent(new Event('input', { bubbles: true }));
        const data = Uint8Array.from(atob(png), (character) => character.charCodeAt(0));
        const transfer = new DataTransfer();
        transfer.items.add(new File([data], 'fixture.png', { type: 'image/png' }));
        document.querySelector('#preview-region').dispatchEvent(new DragEvent('drop', { bubbles: true, dataTransfer: transfer }));
        return true;
        """, arguments: ["png": png.base64EncodedString()])
        try await waitUntil(view, expression: "!!window.studioSnapshot()?.backgroundImage")
        _ = try await script(view, """
        const zoom = document.querySelector('#zoom-number'); zoom.value = '240';
        zoom.dispatchEvent(new Event('input', { bubbles: true }));
        const opacity = document.querySelector('#opacity'); opacity.value = '47';
        opacity.dispatchEvent(new Event('input', { bubbles: true }));
        document.querySelector('#candidate').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }));
        return true;
        """)
        try await waitUntil(view, expression: "document.querySelector('#save-state').textContent === '已自动保存'")
        let saved = try store.load()
        guard saved.name == "Mac 集成检查", saved.imageZoom == 2.4,
              saved.imagePositionX == 0.49, saved.opacity == 0.47,
              saved.backgroundImage == png.base64EncodedString() else {
            throw StudioError.invalid("自动保存遗漏图片、位置或透明度。")
        }
        let applyResult = (try await script(view, """
        return await new Promise((resolve, reject) => {
          const timer = setTimeout(() => reject(new Error('native apply timeout')), 90000);
          window.studioHost.addEventListener('message', ({ data }) => {
            if (data.id === 'native-apply-check' && !data.progress) {
              clearTimeout(timer);
              resolve({ ok: data.ok === true, error: String(data.error || ''), result: data.result || null });
            }
          });
          window.studioHost.postMessage({ id: 'native-apply-check', action: 'apply', theme: window.studioSnapshot() });
        });
        """) as? [String: Any]) ?? [:]
        if applyResult["ok"] as? Bool == true {
            guard let state = applyResult["result"] as? [String: Any],
                  state["cloneRunning"] as? Bool == true,
                  state["applied"] is [String: Any] else {
                throw StudioError.invalid("Mac 输入法副本报告应用成功，但运行状态不完整。")
            }
        } else {
            guard String(applyResult["error"] as? String ?? "").contains("输入源") else {
                throw StudioError.invalid("Mac 输入法副本失败原因不明确。")
            }
        }
        let restored = try await script(view, """
        return await new Promise((resolve, reject) => {
          const timer = setTimeout(() => reject(new Error('native restore timeout')), 30000);
          window.studioHost.addEventListener('message', ({ data }) => {
            if (data.id === 'native-restore-check' && !data.progress) { clearTimeout(timer); resolve(data.ok === true && data.result.cloneRunning === false); }
          });
          window.studioHost.postMessage({ id: 'native-restore-check', action: 'restore' });
        });
        """) as? Bool
        guard restored == true else { throw StudioError.invalid("Mac 原生副本未能停止并恢复官方状态。") }

        first.window?.orderOut(nil)
        view.configuration.userContentController.removeScriptMessageHandler(forName: "wetype")
        let reopened = MacEditor(store: store, resources: resources)
        editor = reopened
        try reopened.start()
        guard let second = reopened.webView else { throw StudioError.invalid("重新打开失败。") }
        try await waitForEditor(second)
        let reopenedJSON = try await script(second, "return JSON.stringify(window.studioSnapshot());") as? String
        guard let reopenedJSON, try ThemeStore.decode(Data(reopenedJSON.utf8)) == saved else {
            throw StudioError.invalid("重新打开后皮肤状态未恢复。")
        }
        _ = try await script(second, """
        window.previewDraws = 0;
        const context = document.querySelector('#pet-preview').getContext('2d');
        const draw = context.drawImage.bind(context);
        context.drawImage = (...arguments) => { draw(...arguments); window.previewDraws++; };
        document.querySelector('#tab-pet').click();
        return true;
        """)
        // WKWebView gives local file images opaque origins. Reading their
        // canvas pixels can be denied even when the animation draws normally.
        try await waitUntil(second, expression: "window.previewDraws > 1")
        try await saveSnapshot(second, to: output.appendingPathComponent("pet-preview-macos.png"))
        // Resume the colors pane for a screenshot of the actual Mac editor.
        _ = try await script(second, "document.querySelector('#tab-image').click(); return true;")
        try await saveSnapshot(second, to: output.appendingPathComponent("editor-macos.png"))
        try JSONSerialization.data(withJSONObject: initial ?? [:], options: [.prettyPrinted, .sortedKeys])
            .write(to: output.appendingPathComponent("editor-state.json"))
        print("PASS UI: WKWebView bootstrap, Mac native-copy apply/rollback handling, image drop, zoom/position/opacity, autosave, reopen, pet animation preview")
    }

    private func saveSnapshot(_ view: WKWebView, to url: URL) async throws {
        let snapshot = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<NSImage, Error>) in
            view.takeSnapshot(with: nil) { image, error in
                if let image { continuation.resume(returning: image) }
                else { continuation.resume(throwing: error ?? StudioError.invalid("截图失败。")) }
            }
        }
        if let tiff = snapshot.tiffRepresentation,
           let bitmap = NSBitmapImageRep(data: tiff),
           let bytes = bitmap.representation(using: .png, properties: [:]) {
            try bytes.write(to: url)
        }
    }

    private func waitForEditor(_ view: WKWebView) async throws {
        try await waitUntil(view, expression: "typeof window.studioSnapshot === 'function' && !!window.studioSnapshot()")
    }

    private func waitUntil(_ view: WKWebView, expression: String) async throws {
        let deadline = Date().addingTimeInterval(40)
        while Date() < deadline {
            if (try? await script(view, "return \(expression);")) as? Bool == true { return }
            try await Task.sleep(nanoseconds: 200_000_000)
        }
        if let diagnostics = try? await script(view, """
        const probe = new Image();
        const loaded = await new Promise((resolve) => {
          probe.onload = () => resolve('loaded:' + probe.naturalWidth);
          probe.onerror = () => resolve('error');
          probe.src = window.petAssets?.idle?.url || 'missing';
          setTimeout(() => resolve('timeout'), 2000);
        });
        return JSON.stringify({
          url: location.href, visibility: document.visibilityState,
          petHidden: document.querySelector('#pet-panel')?.hidden,
          clip: window.petAssets?.idle, image: loaded,
          name: document.querySelector('#pet-preview-name')?.textContent,
          status: document.querySelector('#status-message')?.textContent
        });
        """) { print("UI diagnostics: \(diagnostics)") }
        throw StudioError.invalid("等待编辑器超时：\(expression)")
    }

    private func script(_ view: WKWebView, _ script: String, arguments: [String: Any] = [:]) async throws -> Any {
        try await withCheckedThrowingContinuation { continuation in
            view.callAsyncJavaScript(script, arguments: arguments, in: nil, in: .page) { result in
                continuation.resume(with: result)
            }
        }
    }

    private func finish(_ code: Int32) {
        editor?.window?.orderOut(nil)
        editor?.webView?.configuration.userContentController.removeScriptMessageHandler(forName: "wetype")
        try? FileManager.default.removeItem(at: temporary)
        exit(code)
    }
}

@main
enum EditorChecksMain {
    @MainActor static func main() {
        let application = NSApplication.shared
        let checks = EditorChecks()
        application.delegate = checks
        application.setActivationPolicy(.regular)
        application.run()
    }
}
