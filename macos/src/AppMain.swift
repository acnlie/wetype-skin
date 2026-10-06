import AppKit

@main
enum SkinStudioApp {
    @MainActor static func main() {
        let application = NSApplication.shared
        let isPet = Bundle.main.bundleIdentifier == StudioBundle.helperIdentifier
            || CommandLine.arguments.contains("--pet")
        let role = isPet ? "pet" : "editor"
        let lock = InstanceLock(directory: ThemeStore.standard.directory, role: role)
        do {
            guard try lock.acquire() else {
                if !isPet, let pid = InstanceLock.runningPID(directory: ThemeStore.standard.directory, role: role) {
                    NSRunningApplication(processIdentifier: pid)?.activate(options: [])
                }
                return
            }
        } catch {
            NSLog("无法启动 %@：%@", role, error.localizedDescription)
            return
        }
        let delegate: NSApplicationDelegate = isPet ? MacPet() : MacEditor()
        application.delegate = delegate
        // The detached pet belongs in the menu bar and must not create a
        // second Dock application while the editor is closed.
        application.setActivationPolicy(isPet ? .accessory : .regular)
        withExtendedLifetime((lock, delegate)) { application.run() }
    }
}
