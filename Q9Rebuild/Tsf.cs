using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

[ComImport, Guid("aa80e7f7-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfTextInputProcessor
{
    [PreserveSig] int Activate(ITfThreadMgr manager, uint clientId);
    [PreserveSig] int Deactivate();
}

[ComImport, Guid("aa80e801-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfThreadMgr
{
    [PreserveSig] int Activate(out uint clientId);
    [PreserveSig] int Deactivate();
    [PreserveSig] int CreateDocumentMgr(out ITfDocumentMgr document);
    [PreserveSig] int EnumDocumentMgrs(out IntPtr enumerator);
    [PreserveSig] int GetFocus(out ITfDocumentMgr document);
}

[ComImport, Guid("aa80e7f4-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfDocumentMgr
{
    [PreserveSig] int CreateContext(uint clientId, uint flags, IntPtr textStore, out ITfContext context, out uint cookie);
    [PreserveSig] int Push(ITfContext context);
    [PreserveSig] int Pop(uint flags);
    [PreserveSig] int GetTop(out ITfContext context);
}

[ComImport, Guid("aa80e7fd-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfContext
{
    [PreserveSig] int RequestEditSession(uint clientId, ITfEditSession session, uint flags, out int sessionResult);
}

[ComImport, Guid("aa80e803-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfEditSession
{
    [PreserveSig] int DoEditSession(uint cookie);
}

[ComImport, Guid("55ce16ba-3014-41c1-9ceb-fade1446ac6c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfInsertAtSelection
{
    [PreserveSig] int InsertTextAtSelection(uint cookie, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string text, int length, IntPtr range);
}

[ComImport, Guid("aa80e7f0-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfKeystrokeMgr
{
    [PreserveSig] int AdviseKeyEventSink(uint clientId, ITfKeyEventSink sink, [MarshalAs(UnmanagedType.Bool)] bool foreground);
    [PreserveSig] int UnadviseKeyEventSink(uint clientId);
}

[ComImport, Guid("aa80e7f5-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfKeyEventSink
{
    [PreserveSig] int OnSetFocus([MarshalAs(UnmanagedType.Bool)] bool foreground);
    [PreserveSig] int OnTestKeyDown(ITfContext context, IntPtr key, IntPtr flags, [MarshalAs(UnmanagedType.Bool)] out bool eaten);
    [PreserveSig] int OnTestKeyUp(ITfContext context, IntPtr key, IntPtr flags, [MarshalAs(UnmanagedType.Bool)] out bool eaten);
    [PreserveSig] int OnKeyDown(ITfContext context, IntPtr key, IntPtr flags, [MarshalAs(UnmanagedType.Bool)] out bool eaten);
    [PreserveSig] int OnKeyUp(ITfContext context, IntPtr key, IntPtr flags, [MarshalAs(UnmanagedType.Bool)] out bool eaten);
    [PreserveSig] int OnPreservedKey(ITfContext context, ref Guid key, [MarshalAs(UnmanagedType.Bool)] out bool eaten);
}

[ComImport, Guid("4ea48a35-60ae-446f-8fd6-e6a8d82459f7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfSource
{
    [PreserveSig] int AdviseSink(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] object sink, out uint cookie);
    [PreserveSig] int UnadviseSink(uint cookie);
}

[ComImport, Guid("c0f1db0c-3a20-405c-a303-96b6010a885f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfThreadFocusSink
{
    [PreserveSig] int OnSetThreadFocus();
    [PreserveSig] int OnKillThreadFocus();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
[Guid(TsfIdentity.ClassId)]
public sealed class TsfInput : ITfTextInputProcessor, ITfKeyEventSink, ITfThreadFocusSink
{
    private ITfThreadMgr manager;
    private ITfKeystrokeMgr keys;
    private ITfSource source;
    private uint clientId, focusCookie;
    private bool keyAdvised, focusAdvised, selected;
    private DateTime nextLaunch;
    private Timer focusRetry;
    private Target target;
    private StringBuilder keyOutput;

    public int Activate(ITfThreadMgr threadManager, uint id)
    {
        try
        {
            // The standalone panel must remain focused on the application's own input context.
            if (string.Equals(Process.GetCurrentProcess().ProcessName, "Q9Rebuild", StringComparison.OrdinalIgnoreCase))
                return 0;
            manager = threadManager;
            clientId = id;
            target = new Target(this);
            keys = (ITfKeystrokeMgr)manager;
            source = (ITfSource)manager;
            Guid iid = typeof(ITfThreadFocusSink).GUID;
            Marshal.ThrowExceptionForHR(source.AdviseSink(ref iid, this, out focusCookie));
            focusAdvised = true;
            Marshal.ThrowExceptionForHR(keys.AdviseKeyEventSink(clientId, this, true));
            keyAdvised = true;
            return 0;
        }
        catch (Exception ex)
        {
            Deactivate();
            return Marshal.GetHRForException(ex);
        }
    }

    public int Deactivate()
    {
        if (target != null) Notify(TsfMessage.Blur);
        if (keyAdvised) keys.UnadviseKeyEventSink(clientId);
        if (focusAdvised) source.UnadviseSink(focusCookie);
        keyAdvised = focusAdvised = selected = false;
        StopRetry();
        nextLaunch = DateTime.MinValue;
        if (target != null) target.DestroyHandle();
        target = null;
        manager = null;
        keys = null;
        source = null;
        clientId = 0;
        return 0;
    }

    public int OnSetFocus(bool foreground)
    {
        selected = foreground;
        return foreground ? OnSetThreadFocus() : OnKillThreadFocus();
    }

    public int OnSetThreadFocus()
    {
        if (target != null && selected && TsfProtocol.IsForeground(target.Handle))
        {
            EnsureHost();
            if (!Notify(TsfMessage.Focus)) RetryFocus();
        }
        return 0;
    }

    public int OnKillThreadFocus()
    {
        StopRetry();
        if (target != null) Notify(TsfMessage.Blur);
        return 0;
    }

    private void EnsureHost()
    {
        if (DateTime.UtcNow < nextLaunch || TsfProtocol.FindHost() != IntPtr.Zero) return;
        nextLaunch = DateTime.UtcNow.AddSeconds(3);
        try
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return;
            }
            string path = Path.Combine(Path.GetDirectoryName(typeof(TsfInput).Assembly.Location), "Q9Rebuild.exe");
            if (File.Exists(path))
                using (Process.Start(new ProcessStartInfo(path, "/tsf") { UseShellExecute = false })) { }
        }
        catch (Win32Exception) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void RetryFocus()
    {
        if (focusRetry != null) return;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        focusRetry = new Timer { Interval = 100 };
        focusRetry.Tick += delegate
        {
            if (target == null || !selected || !TsfProtocol.IsForeground(target.Handle) ||
                DateTime.UtcNow >= deadline || Notify(TsfMessage.Focus)) StopRetry();
        };
        focusRetry.Start();
    }

    private void StopRetry()
    {
        if (focusRetry == null) return;
        focusRetry.Dispose();
        focusRetry = null;
    }

    private bool Notify(TsfMessage command, string payload = "")
    {
        return TsfProtocol.Send(TsfProtocol.FindHost(), target.Handle, command, payload);
    }

    private bool Key(TsfMessage command, IntPtr key, IntPtr flags)
    {
        if (target == null || !selected || !TsfProtocol.IsForeground(target.Handle)) return false;
        EnsureHost();
        string payload = new string(new[] { (char)key.ToInt64(), (char)((flags.ToInt64() >> 24) & 1) });
        return Notify(command, payload);
    }

    public int OnTestKeyDown(ITfContext context, IntPtr key, IntPtr flags, out bool eaten)
    {
        eaten = context != null && Key(TsfMessage.TestDown, key, flags);
        return 0;
    }

    public int OnTestKeyUp(ITfContext context, IntPtr key, IntPtr flags, out bool eaten)
    {
        eaten = context != null && Key(TsfMessage.TestUp, key, flags);
        return 0;
    }

    public int OnKeyDown(ITfContext context, IntPtr key, IntPtr flags, out bool eaten)
    {
        eaten = false;
        if (context == null) return 0;
        var output = new StringBuilder();
        keyOutput = output;
        try { eaten = Key(TsfMessage.Down, key, flags); }
        finally { keyOutput = null; }
        if (eaten && output.Length != 0 && !Insert(context, output.ToString()))
            Notify(TsfMessage.CommitFailed);
        return 0;
    }

    public int OnKeyUp(ITfContext context, IntPtr key, IntPtr flags, out bool eaten)
    {
        eaten = Key(TsfMessage.Up, key, flags);
        return 0;
    }

    public int OnPreservedKey(ITfContext context, ref Guid key, out bool eaten)
    {
        eaten = false;
        return 0;
    }

    private bool Receive(string text)
    {
        if (!selected || !TsfProtocol.IsForeground(target.Handle)) return false;
        if (keyOutput != null)
        {
            keyOutput.Append(text);
            return true;
        }
        ITfDocumentMgr document;
        ITfContext context;
        return manager.GetFocus(out document) >= 0 && document != null &&
            document.GetTop(out context) >= 0 && context != null && Insert(context, text);
    }

    private bool Insert(ITfContext context, string text)
    {
        const uint readWrite = 0x6;
        int result;
        var session = new InsertSession(context, text);
        bool accepted = context.RequestEditSession(clientId, session, readWrite, out result) >= 0 && result >= 0;
        if (accepted) session.Failed = delegate { if (target != null) Notify(TsfMessage.CommitFailed); };
        return accepted;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class InsertSession : ITfEditSession
    {
        private readonly ITfContext context;
        private readonly string text;
        internal Action Failed;
        internal InsertSession(ITfContext context, string text) { this.context = context; this.text = text; }
        public int DoEditSession(uint cookie)
        {
            const uint noQuery = 0x1;
            var insert = context as ITfInsertAtSelection;
            int hr = insert == null ? unchecked((int)0x80004002) :
                insert.InsertTextAtSelection(cookie, noQuery, text, text.Length, IntPtr.Zero);
            if (hr < 0 && Failed != null) Failed();
            return hr;
        }
    }

    private sealed class Target : NativeWindow
    {
        private readonly TsfInput owner;
        internal Target(TsfInput owner)
        {
            this.owner = owner;
            CreateHandle(new CreateParams { Parent = TsfProtocol.MessageOnly });
        }

        protected override void WndProc(ref Message message)
        {
            TsfMessage command;
            string payload;
            if (TsfProtocol.Read(message, out command, out payload) &&
                message.WParam == TsfProtocol.FindHost())
            {
                if (command == TsfMessage.Ping)
                {
                    message.Result = owner.selected && TsfProtocol.IsForeground(Handle) ? (IntPtr)1 : IntPtr.Zero;
                    return;
                }
                if (command == TsfMessage.Commit)
                {
                    message.Result = owner.Receive(payload) ? (IntPtr)1 : IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref message);
        }
    }
}
