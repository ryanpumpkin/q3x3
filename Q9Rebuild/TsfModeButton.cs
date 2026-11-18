using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// The 中/英 input-mode button in the taskbar input indicator (GUID_LBI_INPUTMODE), like the original's.
// 中 = input on; 英 = paused, keys pass straight to the application. The icons are drawn here,
// not copied from the original.

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct TfLangBarItemInfo
{
    public Guid ClassId;
    public Guid ItemId;
    public uint Style;
    public uint Sort;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Description;
}

[StructLayout(LayoutKind.Sequential)]
public struct TfPoint { public int X, Y; }

[ComImport, Guid("73540d69-edeb-4ee9-96c9-23aa30b25916"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfLangBarItem
{
    [PreserveSig] int GetInfo(out TfLangBarItemInfo info);
    [PreserveSig] int GetStatus(out uint status);
    [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool show);
    [PreserveSig] int GetTooltipString([MarshalAs(UnmanagedType.BStr)] out string tooltip);
}

// COM interop needs the inherited ITfLangBarItem methods repeated, in order.
[ComImport, Guid("28c7f1d0-de25-11d2-afdd-00105a2799b5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfLangBarItemButton
{
    [PreserveSig] int GetInfo(out TfLangBarItemInfo info);
    [PreserveSig] int GetStatus(out uint status);
    [PreserveSig] int Show([MarshalAs(UnmanagedType.Bool)] bool show);
    [PreserveSig] int GetTooltipString([MarshalAs(UnmanagedType.BStr)] out string tooltip);
    [PreserveSig] int OnClick(int click, TfPoint point, IntPtr area);
    [PreserveSig] int InitMenu(IntPtr menu);
    [PreserveSig] int OnMenuSelect(uint id);
    [PreserveSig] int GetIcon(out IntPtr icon);
    [PreserveSig] int GetText([MarshalAs(UnmanagedType.BStr)] out string text);
}

[ComImport, Guid("57dbe1a0-de25-11d2-afdd-00105a2799b5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfLangBarItemSink
{
    [PreserveSig] int OnUpdate(uint flags);
}

[ComImport, Guid("ba468c55-9956-4fb1-a59d-52a7dd7cc6aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfLangBarItemMgr
{
    [PreserveSig] int EnumItems(out IntPtr enumerator);
    [PreserveSig] int GetItem(ref Guid item, out IntPtr result);
    [PreserveSig] int AddItem(ITfLangBarItem item);
    [PreserveSig] int RemoveItem(ITfLangBarItem item);
}

[ComImport, Guid("7dcf57ac-18ad-438b-824d-979bffb74b7c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfCompartmentMgr
{
    [PreserveSig] int GetCompartment(ref Guid id, out ITfCompartment compartment);
}

[ComImport, Guid("bb08f7a9-607a-4384-8623-056892b64371"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfCompartment
{
    [PreserveSig] int SetValue(uint clientId, [MarshalAs(UnmanagedType.Struct)] ref object value);
}

// The taskbar input indicator treats an input method as off (and shows the language label, e.g. 繁體,
// instead of its icon) until the keyboard open/close compartment says open. The original sets it,
// and keeps the input-mode conversion compartment in step with 中/英.
internal static class TsfCompartments
{
    private static readonly Guid OpenClose = new Guid("58273aad-01bb-4164-95c6-755ba0b5162d");    // GUID_COMPARTMENT_KEYBOARD_OPENCLOSE
    private static readonly Guid Conversion = new Guid("ccf05dd8-4a87-11d7-a6e2-00065b84435c");   // GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION

    internal static void Apply(object threadManager, uint clientId, bool chinese)
    {
        var manager = threadManager as ITfCompartmentMgr;
        if (manager == null) return;
        Set(manager, OpenClose, clientId, 1);
        Set(manager, Conversion, clientId, chinese ? 1 : 0);   // TF_CONVERSIONMODE_NATIVE or alphanumeric
    }

    private static void Set(ITfCompartmentMgr manager, Guid id, uint clientId, int value)
    {
        ITfCompartment compartment;
        if (manager.GetCompartment(ref id, out compartment) < 0 || compartment == null) return;
        object boxed = value;   // VT_I4
        int hr = compartment.SetValue(clientId, ref boxed);
        if (hr < 0) TsfProtocol.Log("compartment " + id + " failed " + hr.ToString("X"));
        Marshal.ReleaseComObject(compartment);
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class TsfModeButton : ITfLangBarItemButton, ITfLangBarItem, ITfSource
{
    private static readonly Guid InputModeItem = new Guid("2c77a81e-41cc-4178-a3a7-5f8a987568e6");   // GUID_LBI_INPUTMODE
    private const uint ButtonStyle = 0x00010000 | 0x00000002;   // TF_LBI_STYLE_BTN_BUTTON | TF_LBI_STYLE_SHOWNINTRAY
    private const uint UpdateAll = 0x1 | 0x2 | 0x4;             // TF_LBI_ICON | TF_LBI_TEXT | TF_LBI_TOOLTIP
    private const int LeftClick = 2;                            // TF_LBI_CLK_LEFT

    private readonly Action clicked;
    private ITfLangBarItemSink sink;
    private bool chinese = true;

    internal TsfModeButton(Action clicked) { this.clicked = clicked; }

    internal void SetChinese(bool value)
    {
        if (chinese == value) return;
        chinese = value;
        if (sink != null) sink.OnUpdate(UpdateAll);
    }

    public int GetInfo(out TfLangBarItemInfo info)
    {
        info = new TfLangBarItemInfo
        {
            ClassId = new Guid(TsfIdentity.ClassId),
            ItemId = InputModeItem,
            Style = ButtonStyle,
            Sort = 0,
            Description = "九方 中／英"
        };
        return 0;
    }

    public int GetStatus(out uint status) { status = 0; return 0; }
    public int Show(bool show) { return 0; }

    public int GetTooltipString(out string tooltip)
    {
        tooltip = chinese ? "中文輸入（按 Shift 轉英文）" : "英文（按 Shift 轉中文）";
        return 0;
    }

    public int OnClick(int click, TfPoint point, IntPtr area)
    {
        if (click == LeftClick && clicked != null) clicked();
        return 0;
    }

    public int InitMenu(IntPtr menu) { return 0; }
    public int OnMenuSelect(uint id) { return 0; }

    public int GetIcon(out IntPtr icon)
    {
        // TSF owns and destroys the returned icon, so make a new one each time.
        icon = DrawIcon(chinese ? "中" : "英");
        return icon == IntPtr.Zero ? unchecked((int)0x80004005) : 0;
    }

    public int GetText(out string text)
    {
        text = chinese ? "中" : "英";
        return 0;
    }

    // ITfSource: TSF subscribes its ITfLangBarItemSink here to hear about icon changes.
    public int AdviseSink(ref Guid interfaceId, object value, out uint cookie)
    {
        cookie = 0;
        if (interfaceId != typeof(ITfLangBarItemSink).GUID) return unchecked((int)0x80040202);   // CONNECT_E_CANNOTCONNECT
        if (sink != null) return unchecked((int)0x80040201);                                     // CONNECT_E_ADVISELIMIT
        sink = value as ITfLangBarItemSink;
        if (sink == null) return unchecked((int)0x80004002);                                     // E_NOINTERFACE
        cookie = 1;
        return 0;
    }

    public int UnadviseSink(uint cookie)
    {
        if (cookie != 1 || sink == null) return unchecked((int)0x80040200);                      // CONNECT_E_NOCONNECTION
        sink = null;
        return 0;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private static IntPtr DrawIcon(string text)
    {
        try
        {
            int size = Math.Max(16, GetSystemMetrics(49));   // SM_CXSMICON
            object light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0);
            Color color = light is int && (int)light == 1 ? Color.Black : Color.White;
            using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = new Font("Microsoft JhengHei UI", size * 0.8f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(text, font, brush, new RectangleF(0, 0, size, size), format);
                return bitmap.GetHicon();
            }
        }
        catch (Exception) { return IntPtr.Zero; }
    }
}
