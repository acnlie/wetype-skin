import Foundation
import AppKit

@main
enum ThemeChecks {
    static func require(_ condition: Bool, _ message: String) throws {
        if !condition { throw StudioError.invalid(message) }
    }

    static func rejects(_ body: () throws -> Void) -> Bool {
        do { try body(); return false } catch { return true }
    }

    static func main() {
        do {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("wetype-theme-check-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: directory) }
            let store = ThemeStore(directory: directory)
            for theme in SkinTheme.presets {
                try store.save(theme)
                try require(store.load() == theme, "预设保存往返不一致。")
            }

            var theme = SkinTheme()
            let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 48, pixelsHigh: 32,
                                          bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                          isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
            let blue = NSColor(deviceRed: 0.2, green: 0.5, blue: 0.9, alpha: 1)
            for x in 0..<48 { for y in 0..<32 { bitmap.setColor(blue, atX: x, y: y) } }
            let png = bitmap.representation(using: .png, properties: [:])!
            let imageURL = directory.appendingPathComponent("图片.png")
            try png.write(to: imageURL)
            try theme.setImage(imageURL)
            theme.imagePositionX = 0.17
            theme.imagePositionY = 0.83
            theme.imageZoom = 2.4
            theme.opacity = 0.47
            theme.petStartWithWindows = true
            try store.save(theme)
            try store.save(theme)
            try require(store.load() == theme, "图片、透明度、构图或 Windows 字段丢失。")
            let exportURL = directory.appendingPathComponent("导出.wtskin.json")
            try store.save(theme, to: exportURL)
            try require(store.load(exportURL) == theme, "导出往返不一致。")
            var legacy = try JSONSerialization.jsonObject(with: JSONEncoder().encode(theme)) as! [String: Any]
            for key in ["imagePositionX", "imagePositionY", "imageZoom", "petEnabled", "petCandidate",
                        "petToolbar", "petAnimate", "petSize"] { legacy.removeValue(forKey: key) }
            legacy["petBackground"] = false
            let migrated = try ThemeStore.decode(JSONSerialization.data(withJSONObject: legacy))
            try require(migrated.imagePositionX == 0.5 && migrated.imageZoom == 1 && migrated.petEnabled &&
                        migrated.petSize == 88 && migrated.petBackground, "旧文件默认字段迁移不一致。")

            var invalid = theme
            invalid.background = "#xyz"
            try require(rejects { try store.save(invalid) }, "未拒绝错误颜色。")
            try require(store.load() == theme, "拒绝错误文件后覆盖了有效皮肤。")
            invalid = theme; invalid.opacity = 0.2
            try require(rejects { try invalid.validate() }, "未拒绝错误透明度。")
            invalid = theme; invalid.formatVersion = 2
            try require(rejects { try invalid.validate() }, "未拒绝未知格式版本。")
            invalid = theme; invalid.backgroundImage = "not base64"
            try require(rejects { try invalid.validate() }, "未拒绝错误图片。")
            try require(rejects { try SkinTheme.validateImage(Data("hello".utf8)) }, "未拒绝伪图片。")
            try require(rejects { try SkinTheme.validateImage(Data(repeating: 0, count: 8 * 1024 * 1024 + 1)) }, "未拒绝过大图片。")
            let oversized = directory.appendingPathComponent("oversized.json")
            try Data(repeating: 32, count: ThemeStore.maximumFileSize + 1).write(to: oversized)
            try require(rejects { _ = try store.load(oversized) }, "未拒绝过大皮肤。")
            let corrupt = Data("{broken".utf8)
            try corrupt.write(to: store.currentURL)
            let preserved = try store.preserveInvalidCurrent()
            try require(Data(contentsOf: preserved) == corrupt, "损坏文件未原样保留。")
            print("PASS: presets, atomic replacement, image/composition roundtrip, legacy defaults, invalid files, preserved recovery copy")
        } catch {
            fputs("FAIL: \(error.localizedDescription)\n", stderr)
            exit(1)
        }
    }
}
