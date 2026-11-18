using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal enum TsfMessage { Focus = 1, Blur, TestDown, TestUp, Down, Up, Commit, Ping, CommitFailed, Log }

internal static class TsfProtocol
{
    internal const int CopyData = 0x004A;
    // An elevated application cannot receive messages from a normal host (UIPI), so elevated
    // processes use their own elevated host under a separate name.
    internal static readonly bool Elevated = IsElevated(System.Diagnostics.Process.GetCurrentProcess().Handle);
    internal static readonly string HostName = "Q9Rebuild.TSF.85e6bb8f" + (Elevated ? ".admin" : "");
    internal static readonly IntPtr MessageOnly = new IntPtr(-3);

    [StructLayout(LayoutKind.Sequential)]
    private struct Data
    {
        public IntPtr Command;
        public int ByteCount;
        public IntPtr Bytes;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string className, string windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, int message, IntPtr sender, ref Data data,
        uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // Diagnostic trace: written only while %LOCALAPPDATA%\Q9Rebuild\tsf-debug.log already exists.
    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Q9Rebuild\tsf-debug.log");
    private static readonly string ProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

    internal static void Log(string line)
    {
        try { if (!System.IO.File.Exists(LogPath)) return; }
        catch (Exception) { return; }
        line = ProcessName + " " + line;
        // Inside applications the file may be unwritable (packaged apps virtualize AppData),
        // so the TSF side hands its trace to the host, which writes the real file.
        if (!string.Equals(ProcessName, "Q9Rebuild", StringComparison.OrdinalIgnoreCase))
        {
            IntPtr host = FindHost();
            if (host != IntPtr.Zero && Send(host, new IntPtr(1), TsfMessage.Log, line)) return;
        }
        WriteLog(line);
    }

    internal static void WriteLog(string line)
    {
        try
        {
            if (!System.IO.File.Exists(LogPath)) return;
            // Packaged apps (e.g. Windows 11 Notepad) write AppData through a per-package copy
            // that lacks this folder; create it so their trace lands in the package's LocalCache.
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath));
            System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
        }
        catch (Exception) { }
    }

    internal static string DescribeForeground(IntPtr window)
    {
        uint process, foregroundProcess;
        uint thread = GetWindowThreadProcessId(window, out process);
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out foregroundProcess);
        return "thread=" + thread + " fgThread=" + foregroundThread + " fgProcess=" + foregroundProcess;
    }

    internal static IntPtr FindHost()
    {
        IntPtr host = FindWindowEx(MessageOnly, IntPtr.Zero, null, HostName);
        // Inside an elevated application, accept only an elevated host: a normal process could
        // otherwise register this window name and receive the keys typed in administrator windows.
        if (host != IntPtr.Zero && Elevated && !WindowIsElevated(host)) return IntPtr.Zero;
        return host;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);

    private static bool WindowIsElevated(IntPtr window)
    {
        uint process;
        if (GetWindowThreadProcessId(window, out process) == 0) return false;
        IntPtr handle = OpenProcess(0x1000, false, process);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return false;
        try { return IsElevated(handle); }
        finally { CloseHandle(handle); }
    }

    private static bool IsElevated(IntPtr process)
    {
        IntPtr token;
        if (!OpenProcessToken(process, 0x0008, out token)) return false;   // TOKEN_QUERY
        try
        {
            int elevated, returned;
            return GetTokenInformation(token, 20, out elevated, 4, out returned) && elevated != 0;   // TokenElevation
        }
        finally { CloseHandle(token); }
    }
    internal static IntPtr Foreground { get { return GetForegroundWindow(); } }

    internal static bool IsForeground(IntPtr window)
    {
        // Compare processes, not threads: Windows 11 Notepad runs TSF on a different thread
        // from the window that owns foreground.
        uint process, foreground;
        if (GetWindowThreadProcessId(window, out process) == 0 || process == 0) return false;
        IntPtr foregroundWindow = GetForegroundWindow();
        GetWindowThreadProcessId(foregroundWindow, out foreground);
        if (process == foreground) return true;
        // To other processes a console window reports the program attached to it (e.g.
        // powershell.exe), not the conhost.exe that owns it and runs TSF. Those two are always
        // parent and child, in one direction or the other.
        return foreground != 0 && IsConsoleWindow(foregroundWindow) &&
            (ParentOf(process) == foreground || ParentOf(foreground) == process);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);

    private static bool IsConsoleWindow(IntPtr window)
    {
        var name = new System.Text.StringBuilder(32);
        return GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "ConsoleWindowClass";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, ParentProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref BasicInformation info,
        int length, out int returned);

    private static uint ParentOf(uint processId)
    {
        IntPtr handle = OpenProcess(0x1000, false, processId);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return 0;
        try
        {
            var info = new BasicInformation();
            int returned;
            return NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf(info), out returned) == 0
                ? (uint)info.ParentProcessId.ToInt64() : 0;
        }
        finally { CloseHandle(handle); }
    }

    internal static bool Send(IntPtr target, IntPtr sender, TsfMessage command, string payload)
    {
        if (target == IntPtr.Zero) return false;
        IntPtr bytes = payload.Length == 0 ? IntPtr.Zero : Marshal.StringToHGlobalUni(payload);
        try
        {
            var data = new Data { Command = (IntPtr)(int)command, Bytes = bytes, ByteCount = payload.Length * 2 };
            IntPtr result;
            return SendMessageTimeout(target, CopyData, sender, ref data, 2, 200, out result) != IntPtr.Zero &&
                result != IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(bytes); }
    }

    internal static bool Read(Message message, out TsfMessage command, out string payload)
    {
        command = 0;
        payload = "";
        if (message.Msg != CopyData || message.WParam == IntPtr.Zero || message.LParam == IntPtr.Zero) return false;
        var data = (Data)Marshal.PtrToStructure(message.LParam, typeof(Data));
        if (data.ByteCount < 0 || data.ByteCount > 16384 || (data.ByteCount & 1) != 0) return false;
        if (data.ByteCount != 0)
        {
            if (data.Bytes == IntPtr.Zero) return false;
            payload = Marshal.PtrToStringUni(data.Bytes, data.ByteCount / 2);
        }
        command = (TsfMessage)data.Command.ToInt64();
        return true;
    }
}
