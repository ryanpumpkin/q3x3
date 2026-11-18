using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal enum TsfMessage { Focus = 1, Blur, TestDown, TestUp, Down, Up, Commit, Ping, CommitFailed, Log }

internal static class TsfProtocol
{
    internal const int CopyData = 0x004A;
    internal const string HostName = "Q9Rebuild.TSF.85e6bb8f";
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

    internal static IntPtr FindHost() { return FindWindowEx(MessageOnly, IntPtr.Zero, null, HostName); }
    internal static IntPtr Foreground { get { return GetForegroundWindow(); } }

    internal static bool IsForeground(IntPtr window)
    {
        // Compare processes, not threads: Windows 11 Notepad runs TSF on a different thread
        // from the window that owns foreground.
        uint process, foreground;
        if (GetWindowThreadProcessId(window, out process) == 0 || process == 0) return false;
        GetWindowThreadProcessId(GetForegroundWindow(), out foreground);
        return process == foreground;
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
