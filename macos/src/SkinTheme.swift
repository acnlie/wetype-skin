import Foundation
import ImageIO
import UniformTypeIdentifiers

enum StudioError: LocalizedError {
    case invalid(String)
    var errorDescription: String? {
        switch self { case .invalid(let message): return message }
    }
}

// Version 1 is shared with src/Theme.cs. New optional fields must have the
// same defaults here so importing a Windows skin preserves its meaning.
struct SkinTheme: Codable, Equatable {
    var formatVersion = 1
    var name = "青竹"
    var background = "#EFFAF3"
    var foreground = "#163D2C"
    var accent = "#12936A"
    var border = "#98CCB4"
    var opacity = 0.96
    var cornerRadius = 14
    var imageTint = 0.32
    var backgroundImage: String?
    var backgroundImageName: String?
    var imagePositionX = 0.5
    var imagePositionY = 0.5
    var imageZoom = 1.0
    var petEnabled = true
    var petCandidate = true
    var petToolbar = true
    var petAnimate = true
    var petSize = 88
    var petBackground = true
    var petStartWithWindows = false

    enum CodingKeys: String, CodingKey {
        case formatVersion, name, background, foreground, accent, border
        case opacity, cornerRadius, imageTint, backgroundImage, backgroundImageName
        case imagePositionX, imagePositionY, imageZoom
        case petEnabled, petCandidate, petToolbar, petAnimate, petSize
        case petBackground, petStartWithWindows
    }

    init() {}

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        formatVersion = try values.decode(Int.self, forKey: .formatVersion)
        name = try values.decode(String.self, forKey: .name)
        background = try values.decode(String.self, forKey: .background)
        foreground = try values.decode(String.self, forKey: .foreground)
        accent = try values.decode(String.self, forKey: .accent)
        border = try values.decode(String.self, forKey: .border)
        opacity = try values.decode(Double.self, forKey: .opacity)
        cornerRadius = try values.decode(Int.self, forKey: .cornerRadius)
        imageTint = try values.decode(Double.self, forKey: .imageTint)
        backgroundImage = try values.decodeIfPresent(String.self, forKey: .backgroundImage)
        backgroundImageName = try values.decodeIfPresent(String.self, forKey: .backgroundImageName)
        imagePositionX = try values.decodeIfPresent(Double.self, forKey: .imagePositionX) ?? 0.5
        imagePositionY = try values.decodeIfPresent(Double.self, forKey: .imagePositionY) ?? 0.5
        imageZoom = try values.decodeIfPresent(Double.self, forKey: .imageZoom) ?? 1
        petEnabled = try values.decodeIfPresent(Bool.self, forKey: .petEnabled) ?? true
        petCandidate = try values.decodeIfPresent(Bool.self, forKey: .petCandidate) ?? true
        petToolbar = try values.decodeIfPresent(Bool.self, forKey: .petToolbar) ?? true
        petAnimate = try values.decodeIfPresent(Bool.self, forKey: .petAnimate) ?? true
        petSize = try values.decodeIfPresent(Int.self, forKey: .petSize) ?? 88
        // Kept for Windows file compatibility; it does not start a Mac helper.
        petBackground = true
        petStartWithWindows = try values.decodeIfPresent(Bool.self, forKey: .petStartWithWindows) ?? false
    }

    func validate() throws {
        guard formatVersion == 1 else { throw StudioError.invalid("暂不支持此皮肤文件的版本。") }
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, name.utf16.count <= 80 else {
            throw StudioError.invalid("皮肤名称需为 1–80 个字符。")
        }
        for value in [background, foreground, accent, border] {
            guard value.range(of: "^#[0-9a-fA-F]{6}$", options: .regularExpression) != nil else {
                throw StudioError.invalid("颜色必须是 #RRGGBB 格式。")
            }
        }
        guard opacity.isFinite, (0.3...1).contains(opacity),
              imageTint.isFinite, (0...1).contains(imageTint),
              (0...40).contains(cornerRadius), (48...160).contains(petSize),
              imagePositionX.isFinite, (0...1).contains(imagePositionX),
              imagePositionY.isFinite, (0...1).contains(imagePositionY),
              imageZoom.isFinite, (1...4).contains(imageZoom) else {
            throw StudioError.invalid("皮肤数值超出范围，请检查透明度、圆角、图片构图和桌宠大小。")
        }
        if let encoded = backgroundImage, !encoded.isEmpty {
            guard encoded.utf8.count <= 12 * 1024 * 1024, let bytes = Data(base64Encoded: encoded) else {
                throw StudioError.invalid("背景图片数据无效或过大。")
            }
            try Self.validateImage(bytes)
        }
    }

    static func validateImage(_ bytes: Data) throws {
        guard bytes.count <= 8 * 1024 * 1024 else { throw StudioError.invalid("背景图片不能超过 8 MB。") }
        guard let source = CGImageSourceCreateWithData(bytes as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              let sourceType = CGImageSourceGetType(source),
              [UTType.png.identifier, UTType.jpeg.identifier, UTType.bmp.identifier, UTType.gif.identifier].contains(sourceType as String),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              (1...4096).contains(width), (1...4096).contains(height),
              CGImageSourceCreateImageAtIndex(source, 0, [kCGImageSourceShouldCache: false] as CFDictionary) != nil else {
            throw StudioError.invalid("图片无法解码，或宽高超过 4096 像素。支持 PNG、JPEG、BMP 和 GIF。")
        }
    }

    mutating func setImage(_ url: URL) throws {
        let bytes = try ThemeStore.readLimited(url, maximum: 8 * 1024 * 1024)
        try Self.validateImage(bytes)
        backgroundImage = bytes.base64EncodedString()
        backgroundImageName = url.lastPathComponent
        imagePositionX = 0.5
        imagePositionY = 0.5
        imageZoom = 1
    }

    static var presets: [SkinTheme] {
        let settings: [(String, String, String, String, String, Double, Int)] = [
            ("青竹", "#EFFAF3", "#163D2C", "#12936A", "#98CCB4", 0.96, 14),
            ("墨夜", "#19232B", "#EDF5F1", "#61D7AF", "#43574E", 0.94, 14),
            ("樱雾", "#FFF2F6", "#67384B", "#D55382", "#E6B7C8", 0.94, 20),
            ("海盐", "#EEF6FF", "#223F65", "#367BD8", "#ADC7E9", 0.96, 14),
            ("纸笺", "#FFF9EC", "#57442E", "#B57932", "#DDC9A6", 1, 8),
            ("暮紫", "#2B233B", "#F1EBFA", "#BB95F4", "#6A5687", 0.93, 18)
        ]
        return settings.map { settings in
            var theme = SkinTheme()
            (theme.name, theme.background, theme.foreground, theme.accent, theme.border, theme.opacity, theme.cornerRadius) = settings
            return theme
        }
    }
}

struct ThemeStore {
    let directory: URL
    var currentURL: URL { directory.appendingPathComponent("current.wtskin.json") }
    static let maximumFileSize = 13 * 1024 * 1024

    static var standard: ThemeStore {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return ThemeStore(directory: base.appendingPathComponent("WeTypeSkinStudio", isDirectory: true))
    }

    static func readLimited(_ url: URL, maximum: Int = maximumFileSize) throws -> Data {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let data = try handle.read(upToCount: maximum + 1) ?? Data()
        guard data.count <= maximum else { throw StudioError.invalid("文件过大，未导入。") }
        return data
    }

    static func decode(_ data: Data) throws -> SkinTheme {
        guard data.count <= maximumFileSize else { throw StudioError.invalid("皮肤文件过大。") }
        let theme: SkinTheme
        do { theme = try JSONDecoder().decode(SkinTheme.self, from: data) }
        catch { throw StudioError.invalid("皮肤 JSON 格式或字段类型无效。") }
        try theme.validate()
        return theme
    }

    func load(_ url: URL? = nil) throws -> SkinTheme {
        try Self.decode(Self.readLimited(url ?? currentURL))
    }

    func save(_ theme: SkinTheme, to destination: URL? = nil) throws {
        try theme.validate()
        let url = destination ?? currentURL
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(theme)
        guard data.count <= Self.maximumFileSize else { throw StudioError.invalid("皮肤文件过大。") }
        try data.write(to: url, options: .atomic)
    }

    func preserveInvalidCurrent() throws -> URL {
        let backup = directory.appendingPathComponent("invalid-\(UUID().uuidString).wtskin.json")
        try FileManager.default.copyItem(at: currentURL, to: backup)
        return backup
    }
}
