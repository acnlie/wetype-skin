import Foundation
import CryptoKit

private struct CloneFailure: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

@discardableResult
private func run(_ executable: String, _ arguments: [String], required: Bool = true) throws -> Int32 {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: executable)
    process.arguments = arguments
    process.standardInput = FileHandle.nullDevice
    try process.run()
    process.waitUntilExit()
    if required && process.terminationStatus != 0 { throw CloneFailure(message: "副本准备失败：\(URL(fileURLWithPath: executable).lastPathComponent)（\(process.terminationStatus)）") }
    return process.terminationStatus
}

private func plist(_ url: URL) throws -> [String: Any] {
    guard let value = try PropertyListSerialization.propertyList(from: Data(contentsOf: url), format: nil) as? [String: Any] else {
        throw CloneFailure(message: "输入法属性表格式无效。")
    }
    return value
}
private func writePlist(_ value: [String: Any], to url: URL) throws {
    try PropertyListSerialization.data(fromPropertyList: value, format: .xml, options: 0).write(to: url, options: .atomic)
}
private func renamed(_ value: Any) -> Any {
    if let text = value as? String { return text.replacingOccurrences(of: "com.tencent.inputmethod.wetype", with: "org.wetypeskinstudio.inputmethod.wetype") }
    if let array = value as? [Any] { return array.map(renamed) }
    if let dictionary = value as? [String:Any] {
        return Dictionary(uniqueKeysWithValues: dictionary.map { (renamed($0.key) as! String, renamed($0.value)) })
    }
    return value
}

private func intelExecutable(from data: Data) throws -> Data {
    let bytes = Array(data)
    func bigEndian(_ offset: Int, _ length: Int) throws -> UInt64 {
        guard offset >= 0, offset + length <= bytes.count else { throw CloneFailure(message: "通用二进制架构表越界。") }
        return bytes[offset..<offset + length].reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
    }
    let magic = try bigEndian(0, 4)
    if magic == 0xCFFAEDFE { return data }
    guard magic == 0xCAFEBABE || magic == 0xCAFEBABF else { throw CloneFailure(message: "微信输入法二进制格式未核对。") }
    let count = try bigEndian(4, 4)
    guard count <= 64 else { throw CloneFailure(message: "通用二进制架构数量无效。") }
    let wide = magic == 0xCAFEBABF
    for i in 0..<Int(count) {
        let entry = 8 + i * (wide ? 32 : 20)
        if try bigEndian(entry, 4) != 0x01000007 { continue }
        let offset = try bigEndian(entry + 8, wide ? 8 : 4)
        let length = try bigEndian(entry + (wide ? 16 : 12), wide ? 8 : 4)
        guard offset <= UInt64(bytes.count), length <= UInt64(bytes.count) - offset else {
            throw CloneFailure(message: "Intel 二进制范围无效。")
        }
        return data.subdata(in: Int(offset)..<Int(offset + length))
    }
    throw CloneFailure(message: "未找到已核对的 Intel 架构。")
}

private func prepare() throws {
    #if !arch(x86_64)
    throw CloneFailure(message: "原生副本目前仅核对 Intel 微信输入法 2.2.3（657）。")
    #endif
    let fm = FileManager.default
    let official = URL(fileURLWithPath: "/Library/Input Methods/WeType.app")
    let officialBinary = official.appendingPathComponent("Contents/MacOS/WeType")
    guard fm.fileExists(atPath: officialBinary.path) else { throw CloneFailure(message: "未找到官方微信输入法。") }
    let officialData = try Data(contentsOf: officialBinary)
    let hash = SHA256.hash(data: officialData).map { String(format: "%02x", $0) }.joined()
    guard hash == "a68a92300cc38e5941f59832c516a37eb098c10c6bbe47ab3dcbe812705cf5ee" else {
        throw CloneFailure(message: "微信输入法版本未核对，未创建或修改副本。")
    }
    let intelData = try intelExecutable(from: officialData)
    guard intelData.count > 0x61D0D,
          intelData[0x61D07..<0x61D0D] == Data([0x0f, 0x84, 0xab, 0, 0, 0]) else {
        throw CloneFailure(message: "官方 Intel 二进制的路径检查字节未核对。")
    }
    let root = fm.homeDirectoryForCurrentUser.appendingPathComponent("Library/Input Methods")
    let clone = root.appendingPathComponent("WeTypeSkinStudioInputMethod.app")
    let native = URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL.deletingLastPathComponent()
    let hook = native.appendingPathComponent("libWeTypeSkinHook.dylib")
    guard fm.fileExists(atPath: hook.path) else { throw CloneFailure(message: "应用包缺少原生皮肤组件。") }
    let revision = "657.3"
    if let installed = try? plist(clone.appendingPathComponent("Contents/Info.plist")),
       installed["CFBundleIdentifier"] as? String == "org.wetypeskinstudio.inputmethod.wetype",
       installed["CFBundleVersion"] as? String == revision,
       let binary = try? Data(contentsOf: clone.appendingPathComponent("Contents/MacOS/WeType")),
       binary.count > 0x61D0D, binary[0x61D07..<0x61D0D] == Data(repeating: 0x90, count: 6),
       fm.fileExists(atPath: clone.appendingPathComponent("Contents/Frameworks/libWeTypeSkinHook.dylib").path),
       try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", clone.path], required: false) == 0 {
        print("Reused: \(clone.path)")
        return
    }
    try fm.createDirectory(at: root, withIntermediateDirectories: true)
    let staging = root.appendingPathComponent(".WeTypeSkinStudio-\(UUID().uuidString).app")
    defer { try? fm.removeItem(at: staging) }
    try fm.copyItem(at: official, to: staging)
    let binaryURL = staging.appendingPathComponent("Contents/MacOS/WeType")
    // /usr/bin/lipo is a developer-tool shim on macOS. Extracting the slice
    // here avoids prompting users to install Command Line Tools at runtime.
    var bytes = Array(intelData)
    guard bytes.count > 0x61D0D, Array(bytes[0x61D07..<0x61D0D]) == [0x0f, 0x84, 0xab, 0, 0, 0] else {
        throw CloneFailure(message: "副本路径检查字节不匹配，未应用补丁。")
    }
    bytes.replaceSubrange(0x61D07..<0x61D0D, with: repeatElement(UInt8(0x90), count: 6))
    func word(_ offset: Int) throws -> UInt32 {
        guard offset >= 0, offset + 4 <= bytes.count else { throw CloneFailure(message: "Mach-O 加载命令越界。") }
        return (0..<4).reduce(UInt32(0)) { $0 | UInt32(bytes[offset + $1]) << ($1 * 8) }
    }
    func put(_ value: UInt32, at offset: Int) {
        for i in 0..<4 { bytes[offset + i] = UInt8(truncatingIfNeeded: value >> (i * 8)) }
    }
    guard try word(0) == 0xFEEDFACF else { throw CloneFailure(message: "副本不是已核对的 Mach-O。") }
    let count = try word(16), commandBytes = try word(20)
    let end = 32 + Int(commandBytes)
    guard end <= bytes.count else { throw CloneFailure(message: "Mach-O 加载命令长度无效。") }
    var cursor = 32, firstSection = bytes.count
    for _ in 0..<count {
        let kind = try word(cursor), length = Int(try word(cursor + 4))
        guard length >= 8, cursor + length <= end else { throw CloneFailure(message: "Mach-O 加载命令无效。") }
        if kind == 0x19 {
            let sections = Int(try word(cursor + 64))
            guard 72 + sections * 80 <= length else { throw CloneFailure(message: "Mach-O 节表越界。") }
            for i in 0..<sections {
                let offset = Int(try word(cursor + 72 + i * 80 + 48))
                if offset > 0 { firstSection = min(firstSection, offset) }
            }
        }
        cursor += length
    }
    let loadPath = Array("@executable_path/../Frameworks/libWeTypeSkinHook.dylib\0".utf8)
    let commandSize = (24 + loadPath.count + 7) & ~7
    guard end + commandSize <= firstSection, end + commandSize <= bytes.count,
          bytes[end..<end + commandSize].allSatisfy({ $0 == 0 }) else {
        throw CloneFailure(message: "副本 Mach-O 没有加载命令空间。")
    }
    put(0xC, at: end); put(UInt32(commandSize), at: end + 4); put(24, at: end + 8)
    bytes.replaceSubrange(end + 24..<end + 24 + loadPath.count, with: loadPath)
    put(count + 1, at: 16); put(commandBytes + UInt32(commandSize), at: 20)
    try Data(bytes).write(to: binaryURL)
    let frameworks = staging.appendingPathComponent("Contents/Frameworks")
    try fm.createDirectory(at: frameworks, withIntermediateDirectories: true)
    try fm.copyItem(at: hook, to: frameworks.appendingPathComponent("libWeTypeSkinHook.dylib"))
    let infoURL = staging.appendingPathComponent("Contents/Info.plist")
    var info = renamed(try plist(infoURL)) as! [String:Any]
    info["CFBundleIdentifier"] = "org.wetypeskinstudio.inputmethod.wetype"
    info["CFBundleName"] = "WeType Skin"
    info["CFBundleDisplayName"] = "WeType Skin"
    info["CFBundleVersion"] = revision
    info["InputMethodConnectionName"] = "WeTypeSkinStudio_Connection"
    info.removeValue(forKey: "CFBundleURLTypes")
    try writePlist(info, to: infoURL)
    let resources = staging.appendingPathComponent("Contents/Resources")
    let localized = [resources.appendingPathComponent("InfoPlist.strings")]
        + (try fm.contentsOfDirectory(at: resources, includingPropertiesForKeys: nil))
            .filter { $0.pathExtension == "lproj" }.map { $0.appendingPathComponent("InfoPlist.strings") }
    for file in localized where fm.fileExists(atPath: file.path) {
        try run("/usr/bin/plutil", ["-convert", "xml1", file.path])
        var strings = renamed(try plist(file)) as! [String:Any]
        for key in ["CFBundleDisplayName", "CFBundleName", "org.wetypeskinstudio.inputmethod.wetype.pinyin"] { strings[key] = "WeType Skin" }
        try writePlist(strings, to: file)
    }
    try fm.setAttributes([.modificationDate: Date()], ofItemAtPath: staging.path)
    try run("/usr/bin/codesign", ["--force", "--deep", "--sign", "-", "--entitlements", native.appendingPathComponent("clone.entitlements").path, staging.path])
    try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", staging.path])
    // Only replace the installed copy after the new bundle is complete.
    if fm.fileExists(atPath: clone.path) {
        try run("/usr/bin/pkill", ["-f", NSRegularExpression.escapedPattern(for: clone.path + "/Contents/MacOS/WeType")], required: false)
        try fm.removeItem(at: clone)
    }
    try fm.moveItem(at: staging, to: clone)
    print("Prepared: \(clone.path)")
}

do { try prepare() }
catch { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
