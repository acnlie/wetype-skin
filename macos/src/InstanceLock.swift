import Foundation
import Darwin

// Editor and pet share an app bundle but have independent lifetimes. A
// per-role advisory lock distinguishes them without matching process names.
final class InstanceLock {
    private var descriptor: Int32 = -1
    private let url: URL

    init(directory: URL, role: String) {
        url = directory.appendingPathComponent("\(role).lock")
    }

    func acquire() throws -> Bool {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let fd = Darwin.open(url.path, O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW, S_IRUSR | S_IWUSR)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else {
            let code = errno
            Darwin.close(fd)
            if code == EWOULDBLOCK { return false }
            throw POSIXError(POSIXErrorCode(rawValue: code) ?? .EIO)
        }
        descriptor = fd
        let pid = Array("\(getpid())\n".utf8)
        _ = ftruncate(fd, 0)
        _ = pid.withUnsafeBytes { Darwin.write(fd, $0.baseAddress, $0.count) }
        return true
    }

    static func runningPID(directory: URL, role: String) -> pid_t? {
        let url = directory.appendingPathComponent("\(role).lock")
        let fd = Darwin.open(url.path, O_RDWR | O_CLOEXEC | O_NOFOLLOW)
        guard fd >= 0 else { return nil }
        defer { Darwin.close(fd) }
        if flock(fd, LOCK_EX | LOCK_NB) == 0 {
            _ = flock(fd, LOCK_UN)
            return nil // A stale PID file is not a running instance.
        }
        guard errno == EWOULDBLOCK,
              let text = try? String(contentsOf: url, encoding: .utf8),
              let pid = pid_t(text.trimmingCharacters(in: .whitespacesAndNewlines)),
              pid > 0, kill(pid, 0) == 0 else { return nil }
        return pid
    }

    deinit {
        if descriptor >= 0 { Darwin.close(descriptor) }
    }
}
