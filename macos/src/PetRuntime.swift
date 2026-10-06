import AppKit

// The helper has its own Launch Services identity. Shared assets remain in
// the parent bundle so installing the DMG never depends on a source checkout.
enum StudioBundle {
    static let helperIdentifier = "org.wetypeskinstudio.pet"
    static var editor: Bundle {
        guard Bundle.main.bundleIdentifier == helperIdentifier else { return Bundle.main }
        let parent = Bundle.main.bundleURL.deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
        return Bundle(url: parent) ?? Bundle.main
    }
    static var helperExecutable: URL? {
        let url = editor.bundleURL.appendingPathComponent("Contents/Helpers/WeType Skin Pet.app")
        return Bundle(url: url)?.executableURL
    }
}

struct PetRuntimeStatus: Codable, Equatable {
    let pid: Int32
    let state: String
    let motion: String
    let frame: [Double]?
    let anchor: [Double]?
    let layer: Int?
    let error: String?

    static func url(in store: ThemeStore) -> URL {
        store.directory.appendingPathComponent("pet-status.json")
    }
    func save(in store: ThemeStore) {
        if let data = try? JSONEncoder().encode(self) {
            try? data.write(to: Self.url(in: store), options: .atomic)
        }
    }
    static func load(in store: ThemeStore) -> Self? {
        guard let data = try? ThemeStore.readLimited(url(in: store), maximum: 8192) else { return nil }
        return try? JSONDecoder().decode(Self.self, from: data)
    }
}
