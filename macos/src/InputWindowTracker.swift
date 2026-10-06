import AppKit
import ApplicationServices

// Only window geometry and input-source identifiers are observed. Text in
// the focused application and candidate strings are never read or persisted.
@MainActor
final class InputWindowTracker {
    struct Anchor {
        let frame: NSRect
        let toolbar: Bool
        let layer: Int
        let windowNumber: Int
    }

    private var owners = Set<pid_t>()
    private var nextOwnerRefresh = Date.distantPast

    func visibleAnchors() -> [Anchor] {
        if Date() >= nextOwnerRefresh {
            owners = Set(NSWorkspace.shared.runningApplications.filter {
                ["com.tencent.inputmethod.wetype", "org.wetypeskinstudio.inputmethod.wetype"].contains($0.bundleIdentifier ?? "")
            }.map(\.processIdentifier))
            nextOwnerRefresh = Date().addingTimeInterval(0.5)
        }
        guard !owners.isEmpty,
              let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return [] }
        let desktopTop = CGDisplayBounds(CGMainDisplayID()).height
        return windows.compactMap { entry in
            guard let pid = entry[kCGWindowOwnerPID as String] as? Int32, owners.contains(pid),
                  let layer = entry[kCGWindowLayer as String] as? Int, layer >= 0,
                  let opacity = entry[kCGWindowAlpha as String] as? Double, opacity > 0,
                  let bounds = entry[kCGWindowBounds as String] as? [String: CGFloat],
                  let x = bounds["X"], let y = bounds["Y"], let width = bounds["Width"], let height = bounds["Height"],
                  width >= 70, width <= 1200, height >= 24, height <= 400 else { return nil }
            // Reject full settings/dialog windows and menu-bar surfaces. A
            // compact WeType floating window is an experimental anchor; its
            // size can vary with horizontal/vertical candidate preferences.
            let frame = NSRect(x: x, y: desktopTop - y - height, width: width, height: height)
            return Anchor(frame: frame, toolbar: width < 230 && height < 48,
                          layer: layer, windowNumber: entry[kCGWindowNumber as String] as? Int ?? 0)
        }
    }

    static func petOrigin(for anchor: Anchor, size: NSSize, visible: NSRect) -> NSPoint {
        var point = NSPoint(x: anchor.frame.maxX - size.width, y: anchor.frame.maxY + 2)
        if point.y + size.height > visible.maxY { point.y = anchor.frame.minY - size.height - 2 }
        point.x = max(visible.minX, min(point.x, visible.maxX - size.width))
        point.y = max(visible.minY, min(point.y, visible.maxY - size.height))
        return point
    }

    static func petLevel(for anchor: Anchor) -> NSWindow.Level {
        NSWindow.Level(rawValue: max(Int(CGWindowLevelForKey(.statusWindow)), anchor.layer + 1))
    }
}
