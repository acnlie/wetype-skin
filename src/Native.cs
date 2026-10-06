using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WeTypeSkinStudio
{
    internal static class Native
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const uint TokenAssignPrimary = 0x0001;
        private const uint TokenDuplicate = 0x0002;
        private const uint TokenAdjustDefault = 0x0080;
        private const uint TokenAdjustSessionId = 0x0100;
        private const int TokenElevation = 20;
        private const int TokenPrimary = 1;
        private const int SecurityImpersonation = 2;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int StartfUseShowWindow = 0x00000001;
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr buffer, int length, out int returnLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(IntPtr file, StringBuilder path, uint size, uint flags);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
        [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUser(
            IntPtr token, string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string reserved;
            public string desktop;
            public string title;
            public int x;
            public int y;
            public int xSize;
            public int ySize;
            public int xCountChars;
            public int yCountChars;
            public int fillAttribute;
            public int flags;
            public short showWindow;
            public short reserved2;
            public IntPtr reserved3;
            public IntPtr standardInput;
            public IntPtr standardOutput;
            public IntPtr standardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr process;
            public IntPtr thread;
            public int processId;
            public int threadId;
        }

        public static bool IsElevated()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前 Windows 权限状态。");
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                int returned;
                if (!GetTokenInformation(token, TokenElevation, buffer, sizeof(int), out returned))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前 Windows 权限状态。");
                return Marshal.ReadInt32(buffer) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                CloseHandle(token);
            }
        }

        // Start through the user's Explorer token so an accidental "Run as
        // administrator" launch returns to the normal token before the editor
        // restarts the input method. COM ShellExecute is unreliable when Explorer
        // has restarted or runs at a different integrity level.
        public static void StartUnelevated(string applicationPath, string arguments, string workingDirectory)
        {
            StartUnelevatedCore(applicationPath, arguments, workingDirectory, 0);
        }

        public static int StartUnelevatedAndWait(string applicationPath, string arguments, string workingDirectory, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            return StartUnelevatedCore(applicationPath, arguments, workingDirectory, timeoutMilliseconds);
        }

        private static int StartUnelevatedCore(string applicationPath, string arguments, string workingDirectory, int timeoutMilliseconds)
        {
            int session = Process.GetCurrentProcess().SessionId;
            Process explorer = null;
            foreach (Process candidate in Process.GetProcessesByName("explorer"))
            {
                if (candidate.SessionId == session) { explorer = candidate; break; }
                candidate.Dispose();
            }
            if (explorer == null) throw new InvalidOperationException("当前会话没有可用的 Windows shell，无法切换到普通权限。");
            IntPtr process = IntPtr.Zero, sourceToken = IntPtr.Zero, token = IntPtr.Zero, environment = IntPtr.Zero;
            try
            {
                process = OpenProcess(ProcessQueryLimitedInformation, false, explorer.Id);
                if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows shell 权限。");
                uint access = TokenQuery | TokenDuplicate | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId;
                if (!OpenProcessToken(process, access, out sourceToken))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows shell 权限。");
                if (!DuplicateTokenEx(sourceToken, 0x000F01FF, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out token))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建普通权限启动令牌。");
                if (!CreateEnvironmentBlock(out environment, token, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建普通权限环境。");
                string directory = string.IsNullOrEmpty(workingDirectory) ? Path.GetDirectoryName(applicationPath) : workingDirectory;
                var command = new StringBuilder(Quote(applicationPath) + (string.IsNullOrEmpty(arguments) ? string.Empty : " " + arguments));
                var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), flags = StartfUseShowWindow, showWindow = (short)(timeoutMilliseconds > 0 ? 0 : 1) };
                ProcessInformation information;
                if (!CreateProcessAsUser(token, applicationPath, command, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment,
                    environment, directory, ref startup, out information))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "普通权限程序启动失败。");
                try
                {
                    if (timeoutMilliseconds == 0) return 0;
                    uint wait = WaitForSingleObject(information.process, (uint)timeoutMilliseconds);
                    if (wait == 0x102) throw new TimeoutException("普通权限恢复助手未及时完成。");
                    if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法等待普通权限恢复助手。");
                    uint exitCode;
                    if (!GetExitCodeProcess(information.process, out exitCode))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取普通权限恢复结果。");
                    return (int)exitCode;
                }
                finally { CloseHandle(information.thread); CloseHandle(information.process); }
            }
            finally
            {
                if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (sourceToken != IntPtr.Zero) CloseHandle(sourceToken);
                if (process != IntPtr.Zero) CloseHandle(process);
                explorer.Dispose();
            }
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append(new string('\\', slashes * 2 + 1)); result.Append('"'); slashes = 0; continue; }
                if (slashes > 0) { result.Append(new string('\\', slashes)); slashes = 0; }
                result.Append(c);
            }
            if (slashes > 0) result.Append(new string('\\', slashes * 2));
            result.Append('"'); return result.ToString();
        }

        public static string ProcessPath(int id)
        {
            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, id);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 87) throw new InvalidOperationException("进程已退出。");
                throw new Win32Exception(error, "无法核对输入法进程路径。");
            }
            try
            {
                var path = new StringBuilder(32768); int size = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return path.ToString();
            }
            finally { CloseHandle(handle); }
        }
        internal static string FinalFilePath(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var resolved = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(file.SafeFileHandle.DangerousGetHandle(), resolved, (uint)resolved.Capacity, 0);
                if (length == 0 || length >= resolved.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
                string value = resolved.ToString();
                if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return "\\\\" + value.Substring(8);
                return value.StartsWith("\\\\?\\", StringComparison.Ordinal) ? value.Substring(4) : value;
            }
        }
    }
}
