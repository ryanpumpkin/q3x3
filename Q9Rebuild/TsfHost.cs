using System;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

internal sealed class TsfHost : NativeWindow, IDisposable
{
    private readonly Q9Panel panel;
    private readonly Timer focusCheck = new Timer { Interval = 200 };
    private IntPtr client, editor;
    private bool blurred;
    private string deferredText;
    private DateTime deferredUntil;

    private TsfHost(Q9Panel panel)
    {
        this.panel = panel;
        CreateHandle(new CreateParams { Caption = TsfProtocol.HostName, Parent = TsfProtocol.MessageOnly });
        panel.TextSink = Commit;
        panel.PausedChanged += SendMode;
        focusCheck.Tick += delegate { RefreshFocus(); };
        focusCheck.Start();
    }

    internal static void Run(Q9Panel panel)
    {
        bool first;
        using (var mutex = new Mutex(true, @"Local\" + TsfProtocol.HostName, out first))
        {
            if (!first) return;
            // Run the message pump while leaving the panel hidden until TSF gives it focus.
            using (var host = new TsfHost(panel))
            using (var context = new ApplicationContext())
            {
                panel.FormClosed += delegate { context.ExitThread(); };
                panel.StartSaving();
                Application.Run(context);
            }
        }
    }

    private bool SetClient(IntPtr window)
    {
        IntPtr foreground = window == IntPtr.Zero ? IntPtr.Zero : TsfProtocol.Foreground;
        if (client == window && editor == foreground && !blurred) return client != IntPtr.Zero;
        if (window != IntPtr.Zero && !TsfProtocol.Send(window, Handle, TsfMessage.Ping, "")) return false;
        if (client != window || editor != foreground)
        {
            deferredText = null;
            panel.SetTsfFocus(false);
        }
        client = window;
        editor = foreground;
        blurred = false;
        if (client != IntPtr.Zero) panel.SetTsfFocus(true);
        FlushDeferred();
        return client != IntPtr.Zero;
    }

    private void RefreshFocus()
    {
        if (deferredText != null && DateTime.UtcNow >= deferredUntil)
        {
            deferredText = null;
            panel.ReportTsfDeliveryFailure();
        }
        if (client == IntPtr.Zero || TsfProtocol.IsForeground(Handle)) return;
        if (blurred || TsfProtocol.Foreground != editor)
        {
            // Keep the destination while a lookup window owns foreground, and allow
            // a deferred mouse selection to be delivered when the same editor returns.
            if (deferredText != null && TsfProtocol.Foreground == editor)
            {
                SetClient(client);
                return;
            }
            // Focus returned to another window of the same document thread (e.g. a dialog closed).
            // TSF reported the document focus before the window change, so confirm with the client
            // instead of hiding the panel until the next key.
            IntPtr foreground = TsfProtocol.Foreground;
            if (!blurred && foreground != IntPtr.Zero && TsfProtocol.Send(client, Handle, TsfMessage.Ping, ""))
            {
                editor = foreground;
                return;
            }
            SetClient(IntPtr.Zero);
        }
    }

    private void Commit(string text)
    {
        TsfProtocol.Log("host commit [" + text + "] client=" + (client != IntPtr.Zero) + " blurred=" + blurred +
            " editorFg=" + (TsfProtocol.Foreground == editor));
        if (client != IntPtr.Zero && !blurred && TsfProtocol.Foreground == editor)
        {
            SendText(text);
            return;
        }
        if (client != IntPtr.Zero && (TsfProtocol.Foreground == editor || TsfProtocol.IsForeground(Handle)))
        {
            deferredText = (deferredText ?? "") + text;
            deferredUntil = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        panel.ReportTsfDeliveryFailure();
    }

    private void FlushDeferred()
    {
        if (deferredText == null) return;
        string text = deferredText;
        deferredText = null;
        if (DateTime.UtcNow < deferredUntil) SendText(text);
        else panel.ReportTsfDeliveryFailure();
    }

    // Tell the focused TSF client whether to show 中 (input on) or 英 (paused: keys pass through).
    private void SendMode()
    {
        if (client != IntPtr.Zero) TsfProtocol.Send(client, Handle, TsfMessage.Mode, panel.Paused ? "en" : "zh");
    }

    private void SendText(string text)
    {
        bool sent = TsfProtocol.Send(client, Handle, TsfMessage.Commit, text);
        TsfProtocol.Log("host send [" + text + "] -> " + sent);
        if (!sent) panel.ReportTsfDeliveryFailure();
    }

    protected override void WndProc(ref Message message)
    {
        TsfMessage command;
        string payload;
        if (!TsfProtocol.Read(message, out command, out payload))
        {
            base.WndProc(ref message);
            return;
        }
        message.Result = IntPtr.Zero;
        var sender = message.WParam;
        if (command == TsfMessage.Log)
        {
            TsfProtocol.WriteLog(payload);
            message.Result = (IntPtr)1;
            return;
        }
        if (command != TsfMessage.TestUp && command != TsfMessage.Up)
            TsfProtocol.Log("host got " + command + " fg=" + TsfProtocol.IsForeground(sender) + " isClient=" + (sender == client));
        if (command == TsfMessage.Blur)
        {
            if (sender == client) blurred = true;
            return;
        }
        if (command == TsfMessage.ToggleMode)
        {
            // Clicking the taskbar button may briefly put the taskbar in front, so the current
            // client is accepted without the foreground check.
            if (sender == client || TsfProtocol.IsForeground(sender) && SetClient(sender))
            {
                panel.TogglePause();   // raises PausedChanged, which sends the new mode back
                message.Result = (IntPtr)1;
            }
            return;
        }
        if (!TsfProtocol.IsForeground(sender)) return;
        if (command == TsfMessage.Focus)
        {
            if (SetClient(sender))
            {
                message.Result = (IntPtr)1;
                SendMode();
            }
            return;
        }
        if (command == TsfMessage.CommitFailed)
        {
            if (sender == client) panel.ReportTsfDeliveryFailure();
            return;
        }
        if (command < TsfMessage.TestDown || command > TsfMessage.Up || payload.Length != 2) return;
        bool down = command == TsfMessage.TestDown || command == TsfMessage.Down;
        int vk = payload[0], flags = payload[1];
        if (vk == 0 || vk > 255 || flags > 1) return;
        if (!SetClient(sender)) return;
        bool test = command == TsfMessage.TestDown || command == TsfMessage.TestUp;
        bool eaten;
        if (test)
        {
            panel.ObserveVirtualKey(vk, down);
            eaten = panel.TestVirtualKey(vk, flags, down);
        }
        else
            eaten = panel.ProcessVirtualKey(vk, flags, down);
        if (eaten) message.Result = (IntPtr)1;
    }

    public void Dispose()
    {
        focusCheck.Dispose();
        panel.TextSink = null;
        DestroyHandle();
    }
}
