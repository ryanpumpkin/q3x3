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
    [PreserveSig] int InWriteSession(uint clientId, [MarshalAs(UnmanagedType.Bool)] out bool writing);
    [PreserveSig] int GetSelection(uint cookie, uint index, uint count, IntPtr selections, out uint fetched);
    [PreserveSig] int SetSelection(uint cookie, uint count, ref TfSelection selection);
}

[StructLayout(LayoutKind.Sequential)]
public struct TfSelection
{
    public IntPtr Range;        // ITfRange*
    public int ActiveEnd;       // TF_AE_NONE
    public int InterimChar;
}

[ComImport, Guid("aa80e7ff-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfRange
{
    [PreserveSig] int GetText();
    [PreserveSig] int SetText();
    [PreserveSig] int GetFormattedText();
    [PreserveSig] int GetEmbedded();
    [PreserveSig] int InsertEmbedded();
    [PreserveSig] int ShiftStart();
    [PreserveSig] int ShiftEnd();
    [PreserveSig] int ShiftStartToRange();
    [PreserveSig] int ShiftEndToRange();
    [PreserveSig] int ShiftStartRegion();
    [PreserveSig] int ShiftEndRegion();
    [PreserveSig] int IsEmpty();
    [PreserveSig] int Collapse(uint cookie, int anchor);
}

[ComImport, Guid("aa80e803-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfEditSession
{
    [PreserveSig] int DoEditSession(uint cookie);
}

[ComImport, Guid("55ce16ba-3014-41c1-9ceb-fade1446ac6c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfInsertAtSelection
{
    [PreserveSig] int InsertTextAtSelection(uint cookie, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string text, int length, out ITfRange range);
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

[ComImport, Guid("aa80e80e-2021-11d2-93e0-0060b067b86e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfThreadMgrEventSink
{
    [PreserveSig] int OnInitDocumentMgr(IntPtr document);
    [PreserveSig] int OnUninitDocumentMgr(IntPtr document);
    [PreserveSig] int OnSetFocus(IntPtr focus, IntPtr previous);
    [PreserveSig] int OnPushContext(IntPtr context);
    [PreserveSig] int OnPopContext(IntPtr context);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
[Guid(TsfIdentity.ClassId)]
public sealed class TsfInput : ITfTextInputProcessor, ITfKeyEventSink, ITfThreadFocusSink, ITfThreadMgrEventSink
{
    private ITfThreadMgr manager;
    private ITfKeystrokeMgr keys;
    private ITfSource source;
    private uint clientId, focusCookie, documentCookie;
    private bool keyAdvised, focusAdvised, documentAdvised, selected;
    private DateTime nextLaunch;
    private Timer focusRetry;
    private Target target;
    private StringBuilder keyOutput;

    public int Activate(ITfThreadMgr threadManager, uint id)
    {
        TsfProtocol.Log("activate start thread=" + GetCurrentThreadId());
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
            // Document focus changes inside an already focused thread (a new window or dialog on the
            // same UI thread) raise no thread focus event, so watch them as well.
            Guid documentIid = typeof(ITfThreadMgrEventSink).GUID;
            Marshal.ThrowExceptionForHR(source.AdviseSink(ref documentIid, this, out documentCookie));
            documentAdvised = true;
            Marshal.ThrowExceptionForHR(keys.AdviseKeyEventSink(clientId, this, true));
            keyAdvised = true;
            TsfProtocol.Log("activate ok");
            return 0;
        }
        catch (Exception ex)
        {
            TsfProtocol.Log("activate failed " + ex);
            Deactivate();
            return Marshal.GetHRForException(ex);
        }
    }

    public int Deactivate()
    {
        if (target != null) Notify(TsfMessage.Blur);
        if (keyAdvised) keys.UnadviseKeyEventSink(clientId);
        if (focusAdvised) source.UnadviseSink(focusCookie);
        if (documentAdvised) source.UnadviseSink(documentCookie);
        keyAdvised = focusAdvised = documentAdvised = selected = false;
        pending.Length = 0;
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
        TsfProtocol.Log("OnSetFocus " + foreground);
        return foreground ? OnSetThreadFocus() : OnKillThreadFocus();
    }

    public int OnSetThreadFocus()
    {
        TsfProtocol.Log("OnSetThreadFocus selected=" + selected + " target=" + (target != null) +
            (target != null ? " " + TsfProtocol.DescribeForeground(target.Handle) : "") + " host=" + TsfProtocol.FindHost());
        ClaimFocus();
        return 0;
    }

    // ITfThreadMgrEventSink
    public int OnInitDocumentMgr(IntPtr document) { return 0; }
    public int OnUninitDocumentMgr(IntPtr document) { return 0; }
    public int OnPushContext(IntPtr context) { return 0; }
    public int OnPopContext(IntPtr context) { return 0; }

    int ITfThreadMgrEventSink.OnSetFocus(IntPtr focus, IntPtr previous)
    {
        TsfProtocol.Log("document focus " + (focus != IntPtr.Zero));
        if (focus != IntPtr.Zero) ClaimFocus();
        return 0;
    }

    // The focus check compares processes, so also require that this thread owns a focused document.
    private bool HasDocumentFocus()
    {
        ITfDocumentMgr document;
        if (manager == null || manager.GetFocus(out document) < 0 || document == null) return false;
        Marshal.ReleaseComObject(document);
        return true;
    }

    // Tell the host this thread's document has focus. A newly created window often is not
    // foreground yet when TSF reports focus, so keep retrying briefly instead of giving up.
    private void ClaimFocus()
    {
        if (target == null || !selected) return;
        if (TsfProtocol.IsForeground(target.Handle))
        {
            EnsureHost();
            if (Notify(TsfMessage.Focus)) { StopRetry(); return; }
        }
        RetryFocus();
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
            if (target == null || !selected || DateTime.UtcNow >= deadline) { StopRetry(); return; }
            // Wait for the window to come to the foreground, then hand focus to the host.
            if (!TsfProtocol.IsForeground(target.Handle)) return;
            EnsureHost();
            if (Notify(TsfMessage.Focus)) StopRetry();
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
        bool eaten = Notify(command, payload);
        TsfProtocol.Log(command + " vk=" + key.ToInt64().ToString("X") + " eaten=" + eaten);
        return eaten;
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
        if (output.Length != 0) TsfProtocol.Log("keydown output [" + output + "] eaten=" + eaten);
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
        TsfProtocol.Log("receive [" + text + "] selected=" + selected + " fg=" + TsfProtocol.IsForeground(target.Handle) +
            " inKey=" + (keyOutput != null));
        if (!selected || !TsfProtocol.IsForeground(target.Handle)) return false;
        if (keyOutput != null)
        {
            keyOutput.Append(text);
            return true;
        }
        // Outside a key event this runs inside the host's cross-thread SendMessage. Editing the
        // document there re-enters the application's text store, so queue it for the message loop.
        bool first = pending.Length == 0;
        pending.Append(text);
        if (first) PostMessage(target.Handle, FlushMessage, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    internal const int FlushMessage = 0x8000 + 0x51;   // WM_APP + 0x51
    private readonly StringBuilder pending = new StringBuilder();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private void FlushPending()
    {
        string text = pending.ToString();
        pending.Length = 0;
        if (text.Length == 0 || manager == null) return;
        ITfDocumentMgr document;
        ITfContext context;
        bool ok = manager.GetFocus(out document) >= 0 && document != null &&
            document.GetTop(out context) >= 0 && context != null && Insert(context, text, true);
        if (!ok) Notify(TsfMessage.CommitFailed);
    }

    private bool Insert(ITfContext context, string text, bool async = false)
    {
        // TF_ES_READWRITE, plus TF_ES_ASYNC when not inside a TSF key callback.
        uint flags = async ? 0xEu : 0x6u;
        int result;
        var session = new InsertSession(context, text);
        session.Failed = delegate { if (target != null) Notify(TsfMessage.CommitFailed); };
        int hr = context.RequestEditSession(clientId, session, flags, out result);
        bool accepted = hr >= 0 && result >= 0;
        TsfProtocol.Log("RequestEditSession async=" + async + " hr=" + hr.ToString("X") + " result=" + result.ToString("X"));
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
            // Flag 0, as in the Windows SDK samples: TF_IAS_NOQUERY leaves TSF without a range for the
            // inserted text and crashes TextInputFramework.dll when it reports the change.
            var insert = context as ITfInsertAtSelection;
            ITfRange range = null;
            int hr = insert == null ? unchecked((int)0x80004002) :
                insert.InsertTextAtSelection(cookie, 0, text, text.Length, out range);
            int selectHr = 0;
            if (hr >= 0 && range != null)
            {
                // Put the caret after the inserted text.
                range.Collapse(cookie, 1);   // TF_ANCHOR_END
                IntPtr pointer = Marshal.GetComInterfaceForObject(range, typeof(ITfRange));
                try
                {
                    var selection = new TfSelection { Range = pointer };
                    selectHr = context.SetSelection(cookie, 1, ref selection);
                }
                finally { Marshal.Release(pointer); }
            }
            if (range != null) Marshal.ReleaseComObject(range);
            TsfProtocol.Log("InsertTextAtSelection [" + text + "] hr=" + hr.ToString("X") + " select=" + selectHr.ToString("X"));
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
            if (message.Msg == FlushMessage)
            {
                owner.FlushPending();
                return;
            }
            TsfMessage command;
            string payload;
            if (TsfProtocol.Read(message, out command, out payload) &&
                message.WParam == TsfProtocol.FindHost())
            {
                if (command == TsfMessage.Ping)
                {
                    message.Result = owner.selected && TsfProtocol.IsForeground(Handle) && owner.HasDocumentFocus()
                        ? (IntPtr)1 : IntPtr.Zero;
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
