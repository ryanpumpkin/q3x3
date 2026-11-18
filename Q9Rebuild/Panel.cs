// 九方 W10 重建版 - 工作視窗、鍵盤攔截、設定選單
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal sealed class Settings
{
    private readonly string path;
    private readonly Dictionary<string, int> values = new Dictionary<string, int>
    {
        { "Mode", 1 }, { "Lang", 0 }, { "Convert", 0 }, { "Size", 300 }, { "Opacity", 100 }, { "KeyMode", 0 },
        { "RelMode", 2 }, { "Ver", 0 }, { "DirectTopLeft", 1 }, { "HalfPunct", 0 }, { "Radical", 0 }, { "History", 0 }, { "UpdateOrder", 1 },
        { "Top", 0 }, { "ShiftToggle", 1 }, { "ModeNumber", 1 }, { "ModeEmoji", 1 },
        { "On1", 1 }, { "On2", 1 }, { "On3", 1 }, { "On4", 1 }, { "On5", 1 }
    };

    internal Settings(string dir)
    {
        path = Path.Combine(dir, "settings.ini");
        if (!File.Exists(path)) return;
        foreach (string line in File.ReadAllLines(path))
        {
            int eq = line.IndexOf('='), v;
            if (eq > 0 && values.ContainsKey(line.Substring(0, eq)) && int.TryParse(line.Substring(eq + 1), out v))
                values[line.Substring(0, eq)] = v;
        }
    }

    internal int this[string key]
    {
        get { return values[key]; }
        set
        {
            values[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, values.Select(p => p.Key + "=" + p.Value));
        }
    }
}

internal sealed class Skin : IDisposable
{
    private readonly Dictionary<string, Image> images = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float> values = new Dictionary<string, float>();
    private readonly Original files;
    private readonly string dir;
    internal readonly float Width, Height;

    internal Skin(Original files, int size)
    {
        this.files = files;
        dir = @"SKINready\default\" + size + @"\normal";
        if (!files.Exists(Path.Combine(dir, "q9.skin"))) dir = @"SKINready\default\1000\normal";
        string section = "";
        foreach (string input in files.ReadAllLines(Path.Combine(dir, "q9.skin")))
        {
            string line = input.Trim();
            if (line.StartsWith("[") && line.EndsWith("]")) section = line.Substring(1, line.Length - 2);
            int eq = line.IndexOf('=');
            float number;
            if (eq > 0 && float.TryParse(line.Substring(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                values[section + "." + line.Substring(0, eq)] = number;
        }
        Width = values["UI.SkinWidth"];
        Height = values["UI.SkinHeight"];
    }

    internal float Value(string key, float fallback) { float v; return values.TryGetValue(key, out v) ? v : fallback; }

    internal RectangleF Area(string name)
    {
        return RectangleF.FromLTRB(values[name + ".X1"], values[name + ".Y1"], values[name + ".X2"], values[name + ".Y2"]);
    }

    internal Image Get(string name)
    {
        Image image;
        if (!images.TryGetValue(name, out image))
        {
            string file = Path.Combine(dir, name);
            if (files.Exists(file))
                using (Stream stream = files.Open(file))
                using (var source = Image.FromStream(stream)) image = new Bitmap(source);
            images[name] = image;
        }
        return image;
    }

    public void Dispose() { foreach (Image image in images.Values) if (image != null) image.Dispose(); }
}

internal sealed class Q9Panel : Form
{
    private readonly Q9Data data;
    private readonly Engine engine;
    private readonly Settings settings;
    private Skin skin;
    private readonly ContextMenuStrip menu = new ContextMenuStrip();
    private readonly Native.HookProc hookProc;
    private IntPtr hook;
    private bool paused, dragging, shiftAlone, hovering;
    private Point dragOrigin;
    private DateTime shiftDown;
    internal readonly List<string> Sent = new List<string>();
    internal bool Capture_;                      // 測試時只記錄輸出，不送鍵
    internal bool AcceptInjected;                // 測試鍵盤攔截時接受模擬按鍵

    private static readonly int[] Sizes = { 200, 300, 400, 500, 600, 700, 800, 900, 1000 };

    internal Q9Panel(Q9Data data, Engine engine, Settings settings)
    {
        this.data = data; this.engine = engine; this.settings = settings;
        Text = "九方 W10 重建版";
        FormBorderStyle = FormBorderStyle.None;
        TransparencyKey = ColorKey;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        if (settings["Ver"] < 2) { settings["RelMode"] = 2; settings["Ver"] = 2; }   // 舊設定改用原版預設嘅動態關聯字
        engine.SetLang(settings["Lang"]);
        engine.SetMode(engine.SupportsMode(settings["Mode"]) ? settings["Mode"] : EnabledChineseModes().First());
        engine.OutputConvert = settings["Convert"] != 0; engine.RelMode = settings["RelMode"];
        engine.HalfPunct = settings["HalfPunct"] != 0; engine.RadicalFirst = settings["Radical"] != 0;
        engine.UpdateOrder = settings["UpdateOrder"] != 0;
        engine.Output = Deliver;
        engine.Beep = delegate { if (!Capture_) Native.MessageBeep(0); };
        engine.ResetQuiet();
        ApplySize();
        hookProc = HookCallback;
    }

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x8; return p; }   // NOACTIVATE | TOOLWINDOW | TOPMOST
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x21) { m.Result = new IntPtr(3); return; }   // WM_MOUSEACTIVATE → MA_NOACTIVATE
        base.WndProc(ref m);
    }

    internal void StartHook()
    {
        hook = Native.SetWindowsHookEx(13, hookProc, Native.GetModuleHandle(null), 0);
        // 每 30 秒將學到嘅關聯字同最近用字寫入 state.txt，程式死咗最多只會冇咗半分鐘
        saveTimer.Tick += delegate { SaveIfDirty(); };
        saveTimer.Start();
    }

    private readonly Timer saveTimer = new Timer { Interval = 30000 };

    private void SaveIfDirty()
    {
        if (!engine.StateDirty) return;
        try { engine.SaveState(); }
        catch (IOException) { }                  // 寫唔到就下次再試
        catch (UnauthorizedAccessException) { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
        saveTimer.Dispose();
        engine.SaveState();
        base.OnFormClosed(e);
    }

    // ---------- 版面 (座標全部用皮膚檔設計單位 × 視窗闊度 / SkinWidth) ----------
    private float S { get { return ClientSize.Width / skin.Width; } }

    private void ApplySize()
    {
        if (skin != null) skin.Dispose();
        int size = settings["Size"];
        skin = new Skin(data.Files, size);
        int width = size - 1;                                   // 原版 300 設定實際係 299×481
        Image bg = skin.Get("q9.png");
        ClientSize = new Size(width, (int)Math.Round(width * (double)bg.Height / bg.Width));
        Dock_(settings["Top"] != 0);
        Opacity = hovering ? 1.0 : settings["Opacity"] / 100.0;
        Invalidate();
    }

    private void Dock_(bool top)
    {
        var area = Screen.FromPoint(Location).WorkingArea;
        Location = new Point(area.Right - Width - 12, top ? area.Top + 12 : area.Bottom - Height - 12);
    }

    private RectangleF Px(RectangleF design) { float s = S; return new RectangleF(design.X * s, design.Y * s, design.Width * s, design.Height * s); }
    private bool ShowingRelated { get { return engine.State == St.Idle && engine.HasRelated; } }
    private RectangleF Input
    {
        get
        {
            // 靜止時 (原版畫 Button09) 格線同底圖用皮膚圖片原大、整數原點；同原版截圖逐像素對過。
            Image gridLines = skin.Get("Button09Transparent.png");
            if (gridLines == null) return Px(skin.Area("Q9_INPUT_AREA"));
            float scale = (float)ClientSize.Width / skin.Get("q9.png").Width;
            return new RectangleF(X0, Y0, gridLines.Width * scale, gridLines.Height * scale);
        }
    }

    // Button09Transparent 嘅格線位置：三行 + 0 行
    private static readonly float[] RowEdges = { 0f, 0.2519f, 0.5037f, 0.759f, 1f };

    // 原版 (FUN_1800b0700) 用整數格仔：格仔 = round(CellSize × 比例)，原點 = 輸入區左上角
    private int CellPx { get { return (int)Math.Round(skin.Value("UI.CellSize", 150) * S); } }
    private int X0 { get { return (int)(skin.Area("Q9_INPUT_AREA").X * S) - 1; } }
    private int Y0 { get { return (int)Math.Round(skin.Area("Q9_INPUT_AREA").Y * S); } }

    private RectangleF Cell(int key)   // key 1-9，按數字鍵盤排位
    {
        int row = 2 - (key - 1) / 3, col = (key - 1) % 3, c = CellPx;
        return new RectangleF(X0 + col * c, Y0 + row * c, c, c);
    }

    private RectangleF ZeroKey { get { int c = CellPx; return RectangleF.FromLTRB(X0, Y0 + 3 * c, X0 + 2 * c, Input.Bottom); } }
    // FUN_1800b0700 格 1-9：格邊內縮 GridLineLeft/Top，右下減 GridLineRight/Bottom (Polygon 唔包右下邊)
    private Rectangle GrayCell(int key)
    {
        float s = S;
        RectangleF r = Cell(key);
        int l = (int)(skin.Value("UI.GridLineLeft", 4) * s), t = (int)(skin.Value("UI.GridLineTop", 5) * s);
        int rr = (int)(skin.Value("UI.GridLineRight", 3) * s), bb = (int)(skin.Value("UI.GridLineBottom", 0) * s);
        int c = CellPx;
        return new Rectangle((int)r.X + l, (int)r.Y + t, c - l - rr, c - t - bb);
    }

    // FUN_1800b0700 格 10：0 行「.」格加 KeyReset* 皮膚值調整 (換算方法照原版截圖揀)
    private Rectangle DotBox
    {
        get
        {
            float s = S;
            Func<string, float, float> v = (k, d) => skin.Value("UI." + k, d) * s;
            int c = CellPx;
            int x0 = X0 + 2 * c + (int)v("KeyResetAdjustH", 12) + (int)v("GridLineLeft", 4);
            int y0 = Y0 + 3 * c + (int)v("KeyResetAdjustV", 30) + (int)v("GridLineTop", 5);
            // FUN_18010dd90 用 Polygon 填；闊高按原版截圖 (大細 300：x 157-209、y 285-326)
            int w = c + (int)v("KeyResetWidthAdjust", -30) - (int)v("GridLineRight", 3) - (int)Math.Round(v("GridLineLeft", 4)) + 1;
            int h = c + (int)v("KeyResetHeightAdjust", -54) - (int)v("GridLineBottom", 0) - (int)v("GridLineTop", 5);
            return new Rectangle(x0, y0, w, h);
        }
    }

    private RectangleF DotKey { get { int c = CellPx; return RectangleF.FromLTRB(X0 + 2 * c, Y0 + 3 * c, Input.Right, Input.Bottom); } }

    private static string ModeImage(int mode)
    {
        switch (mode)
        {
            case Engine.Glyph: return "button02_glyph.png";
            case Engine.Pinyin: return "Button02_pinyin.png";
            case Engine.Zhuyin: return "button02_zhuyin.png";
            case Engine.Canton: return "button02_canton.png";
            case Engine.Number: return "Button02_num.png";
            case Engine.Emoji: return "button02_emoji.png";
            default: return "button02_stroke.png";
        }
    }

    // 原版輸入法掣顯示「下一個」輸入法：筆劃 → 字形 → 拼音 → 注音 → 粵拼 → 數字 → 表情符號
    private List<int> ModeCycle()
    {
        var order = EnabledChineseModes();
        if (settings["ModeNumber"] != 0) order.Add(Engine.Number);
        if (settings["ModeEmoji"] != 0) order.Add(Engine.Emoji);
        return order;
    }

    private int NextModeValue { get { var order = ModeCycle(); return order[(order.IndexOf(engine.Mode) + 1) % order.Count]; } }


    // AlphaBlend (預乘 alpha) 同樣嘅整數混色：dst = src×a/255 + dst×(255−a)/255，兩項都截斷
    // (同原版截圖對比：截斷 0 個像素唔同，四捨五入有 3.7% 差 3-4 級)
    private static bool BlendExact(Graphics g, Bitmap target, Bitmap src, int x, int y)
    {
        if (target.PixelFormat != System.Drawing.Imaging.PixelFormat.Format32bppArgb
            && target.PixelFormat != System.Drawing.Imaging.PixelFormat.Format32bppRgb) return false;
        var area = Rectangle.Intersect(new Rectangle(x, y, src.Width, src.Height), new Rectangle(Point.Empty, target.Size));
        if (area.IsEmpty) return true;
        g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        var dl = target.LockBits(area, System.Drawing.Imaging.ImageLockMode.ReadWrite, target.PixelFormat);
        var sl = src.LockBits(new Rectangle(area.X - x, area.Y - y, area.Width, area.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var d = new byte[dl.Stride * area.Height];
            var s = new byte[sl.Stride * area.Height];
            System.Runtime.InteropServices.Marshal.Copy(dl.Scan0, d, 0, d.Length);
            System.Runtime.InteropServices.Marshal.Copy(sl.Scan0, s, 0, s.Length);
            const int r = 0;
            for (int row = 0; row < area.Height; row++)
                for (int col = 0; col < area.Width; col++)
                {
                    int si = row * sl.Stride + col * 4, di = row * dl.Stride + col * 4, a = s[si + 3];
                    if (a == 0) continue;
                    for (int c = 0; c < 3; c++)
                        d[di + c] = (byte)((s[si + c] * a + r) / 255 + (d[di + c] * (255 - a) + r) / 255);
                }
            System.Runtime.InteropServices.Marshal.Copy(d, 0, dl.Scan0, d.Length);
        }
        finally { target.UnlockBits(dl); src.UnlockBits(sl); }
        return true;
    }

    private void Draw(Graphics g, string image, RectangleF area)
    {
        Image img = skin.Get(image);
        if (img == null) return;
        // 原版 (FUN_18010d2e0) 用 BitBlt 將皮膚圖原大畫喺整數位置；圖嘅大細同範圍差唔多就唔好縮放
        if (Math.Abs(img.Width - area.Width) <= 1.5f && Math.Abs(img.Height - area.Height) <= 1.5f)
        {
            // 位置 = floor(設計座標 × a)：同原版截圖逐個掣對過，a 要喺 [0.45641, 0.45691]
            // (九方掣、↵、简、−、+、底部收起掣嘅 x / y 全部吻合)，用 (闊度+1)/(SkinWidth−5) = 300/657
            float s = S, s2 = (ClientSize.Width + 1) / (skin.Width - 5);
            int x = (int)(area.X / s * s2), y = (int)(area.Y / s * s2);
            if (frame != null && BlendExact(g, frame.Bitmap, (Bitmap)img, x, y)) return;
            g.DrawImage(img, new Rectangle(x, y, img.Width, img.Height), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel);
            return;
        }
        g.DrawImage(img, area);
    }

    // 「取消」用原版 Button09.png 入面嘅字 (淨係攞字，唔要白底)
    private Bitmap cancelText;
    private Bitmap CancelText
    {
        get
        {
            if (cancelText != null) return cancelText;
            var src = (Bitmap)skin.Get("Button09.png");
            var b = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            for (int y = src.Height * 3 / 4; y < src.Height; y++)
                for (int x = src.Width * 2 / 3; x < src.Width; x++)
                {
                    Color c = src.GetPixel(x, y);
                    int dark = 255 - Math.Min(c.R, Math.Min(c.G, c.B));
                    if (dark > 8) b.SetPixel(x, y, Color.FromArgb(Math.Min(255, dark * 255 / 222), 35, 24, 21));
                }
            return cancelText = b;
        }
    }

    private static readonly Color ColorKey = Color.FromArgb(192, 192, 192);  // 原版 SetLayeredWindowAttributes 0xC0C0C0
    private static readonly Color Fill = Color.FromArgb(223, 204, 205);      // kFillBlankColor 13487327 = COLORREF 0xCDCCDF
    private static readonly Color RelatedFill = Color.FromArgb(203, 255, 255, 255);
    private static readonly Color Empty = Color.FromArgb(193, 192, 192);     // 0xC0C0C1
    private static readonly Color RelColor = Color.FromArgb(47, 47, 47);
    private static readonly Color PreviewColor = Color.FromArgb(128, 40, 213);   // 0xd52880 (FUN_1800b0970)

    private string FontFace { get { return engine.Lang == 0 ? "MingLiU_HKSCS" : "SimSun"; } }
    private int Pt(float design) { return (int)Math.Round(design * S); }

    // 香港字 / 外字 (Big5 標準範圍以外) 用紅色
    private bool Extended(string s)
    {
        if (engine.Lang != 0) return false;
        byte[] b = Dbcs.Encode(0, s);
        if (b == null) return false;          // 表情符號等唔係 Big5 嘅字唔算
        int code = b[0] << 8 | b[1];
        return code < 0xA140 || (code >= 0xC6A1 && code < 0xC940) || code >= 0xF9D6;
    }

    // FUN_1800b7eb0：拼音有聲母、揀咗單韻母之後，冇複韻母嘅格用灰色 (0xC0C0C1) 填
    private bool PinyinDisabled(int key)
    {
        if (engine.Mode != Engine.Pinyin || engine.State != St.Compose || engine.Buf.Length != 2 || engine.Buf[1] == '0') return false;
        switch (engine.Buf[1])
        {
            // 原版逐格呼叫 FUN_1800b0700：o → 4 5 6；e → 1 2 3 5 6；a → 1 2 5 6 (4 = er / ao 唔會灰)
            case '4': return key >= 4 && key <= 6;
            case '5': return key <= 3 || key == 5 || key == 6;
            case '8': return key == 1 || key == 2 || key == 5 || key == 6;
        }
        return false;
    }

    // 四角三角形 (FUN_1800b55c0)：格內縮 GridLine*，邊長 CornerSize；回傳聲調鍵 7 9 1 3，唔喺角就 '\0'
    private char CornerOf(RectangleF cell, PointF p)
    {
        float s = S, l = skin.Value("UI.GridLineLeft", 4) * s, t = skin.Value("UI.GridLineTop", 5) * s;
        float r = skin.Value("UI.GridLineRight", 3) * s, b = skin.Value("UI.GridLineBottom", 0) * s;
        float c = skin.Value("UI.CornerSize", 40) * s;
        float x = p.X - cell.X, y = p.Y - cell.Y, w = cell.Width;
        if (x >= l && y >= t && (x - l) + (y - t) <= c) return '7';
        if (x <= w - r && y >= t && (w - r - x) + (y - t) <= c) return '9';
        if (x >= l && y <= w - b && (x - l) + (w - b - y) <= c) return '1';
        if (x <= w - r && y <= w - b && (w - r - x) + (w - b - y) <= c) return '3';
        return '\0';
    }

    // 「點擊左上角的字直接上字」範圍 (碼 0x1100)：靜止時係格內縮後嘅左上半格三角形 (FUN_1800b55c0)；
    // 筆劃、粵拼取碼時係成格嘅左上半三角形 (FUN_1800bf150 / FUN_1800b90f0)
    private bool InTopLeftHalf(RectangleF cell, PointF p, bool compose)
    {
        float s = S, x = p.X - cell.X, y = p.Y - cell.Y, w = cell.Width;
        float l = 0, t = 0, r = 0, b = 0;
        if (!compose)
        {
            l = skin.Value("UI.GridLineLeft", 4) * s; t = skin.Value("UI.GridLineTop", 5) * s;
            r = skin.Value("UI.GridLineRight", 3) * s; b = skin.Value("UI.GridLineBottom", 0) * s;
        }
        float spanX = w - r - l, spanY = w - b - t;
        return x >= l && y >= t && spanX > 0 && spanY > 0 && (x - l) / spanX + (y - t) / spanY <= 1;
    }

    private void PageLabels(out string key0, out string dot, out string minus, out string plus, out string[] lines)
    {
        bool select = engine.State == St.Select, compose = engine.State == St.Compose;
        bool hasRel = engine.HasRelated;
        int mode = engine.Mode;
        key0 = ""; dot = "取消"; minus = "button10_favorites.png"; plus = "button11_phrase.png"; lines = null;
        string[] browse = { "直接選字", "或按下頁繼續找尋" };
        if (mode == Engine.Number) { key0 = "0"; dot = "."; lines = new[] { "請輸入數字" }; return; }
        if (engine.NoChar)
        {
            key0 = "標點"; minus = "button10_pgup.png"; plus = "button11_pyExtra.png";
            lines = new[] { "沒有中文字供選用", "請輸入其他字", "其他版本可有多字" };
            return;
        }
        if (engine.Kind == "emojicat") { key0 = "標點"; return; }
        // FUN_18000c5e0：筆劃、字形有「補碼」(選單 Pinyin / Shape Search) 時，取碼同選字嘅「+」係補音；
        // 「−」喺數字碼第一頁 (FUN_1800ad920) 係字形檢字，其他 (關聯字、標點、後頁) 係上頁。
        bool extraButtons = mode == Engine.Stroke || mode == Engine.Glyph;
        if (select)
        {
            if (engine.Kind == "lookup-menu") { minus = "button10_pgup.png"; plus = "button11_pyExtra.png"; lines = new[] { "選檢字方法" }; return; }
            // 補碼第一步：字形 ('E') 兩邊都係空白掣，補音 ('e') 左邊係上頁
            if (engine.Kind == "shape-extra") { minus = "button10_blank.png"; plus = "button11_blank.png"; lines = new[] { "選字形結構" }; return; }
            if (engine.Kind == "sound-extra")
            {
                minus = "button10_pgup.png"; plus = "button11_blank.png";
                key0 = "姓氏"; lines = new[] { "請選字的聲母作補音", "或按０選姓氏／外字" }; return;
            }
            key0 = "下頁";
            // 標點頁 ('P' 一碼) 對原版係取碼狀態：筆劃字形顯示上頁 + 補音，其他輸入法顯示常用字句 + 詞語
            if (engine.Kind == "punct")
            {
                key0 = "下頁";     // FUN_1800b0970 'P'：0 鍵標籤「下　頁」(按 0 = 入 PUNDATA 第 9 組)
                if (extraButtons) { minus = "button10_pgup.png"; plus = "button11_pyExtra.png"; }
                return;
            }
            if (engine.Kind == "sym" && engine.LookupSym)
            {
                minus = "button10_pgup.png"; plus = "button11_pyExtra.png"; lines = browse; return;
            }
            if (engine.Kind == "sym")
            {
                minus = "button10_pgup.png"; plus = extraButtons ? "button11_pyExtra.png" : "button11_nextpage.png";
                lines = browse; return;   // FUN_1800b0970 選字狀態通用提示 (原版截圖)
            }
            if (engine.Kind == "radical-stroke") { minus = "button10_pgup.png"; plus = "button11_pyExtra.png"; key0 = ""; return; }
            if (engine.Kind == "stroke-count" || engine.Kind == "radical-count" || engine.Kind == "lookup-result")
            {
                minus = "button10_pgup.png"; plus = "button11_pyExtra.png";   // 檢字子頁同檢字選單一樣 (原版截圖)
                lines = engine.Kind == "stroke-count" ? new[] { "選筆劃數目" }
                    : browse;   // 部首清單原版都係通用提示 (原版截圖)
                return;
            }
            if (engine.Kind == "phrase")
            {
                minus = "button10_pgup.png";
                lines = engine.Last.Length == 0 ? browse : new[] { "按０下頁", "按右邊＋改詞長" };
                return;
            }
            lines = browse;
            if (engine.Kind == "emoji" || mode == Engine.Pinyin || mode == Engine.Zhuyin || mode == Engine.Canton)
            { minus = "button10_pgup.png"; plus = "button11_nextpage.png"; }
            else
            {
                minus = engine.Page > 1 || engine.Kind == "rel" ? "button10_pgup.png" : "button10_shapeExtra.png";
                plus = "button11_pyExtra.png";
            }
            return;
        }
        if (compose)
        {
            int n = engine.Buf.Length;
            switch (mode)
            {
                case Engine.Glyph:
                    minus = "button10_shapeExtra.png"; plus = "button11_pyExtra.png";
                    if (n == 1) { key0 = "姓氏"; lines = new[] { "選部件", "按０選姓氏" }; }
                    else { key0 = "確定"; lines = new[] { "獨體字按０", "分體字選最右上之特徵" }; }
                    return;
                case Engine.Stroke:
                    minus = "button10_shapeExtra.png"; plus = "button11_pyExtra.png";
                    // FUN_1800bf820：有預覽字就係「選字」+ 兩行提示 (唔理第幾碼)；冇預覽字就同靜止時一樣
                    if (engine.HasPreview)
                    {
                        key0 = "選字";
                        lines = new[] { "常用字﹕按０選字", "獨體字﹕左邊第二筆" };
                    }
                    else
                    {
                        key0 = "標點";
                        lines = new[] { "獨體字﹕順序第一二筆", "分體字﹕左邊頭兩筆", "　　　　右邊第一筆" };
                    }
                    return;
                case Engine.Pinyin:
                    // FUN_1800b7eb0：第一碼 0 鍵「韻母組合」(聲母 1 3 5 冇)；冇聲母 e 第二碼「下頁」
                    if (n == 1) { key0 = "135".IndexOf(engine.Buf[0]) < 0 ? "韻母組合" : ""; lines = new[] { "請選擇韻母分類" }; }
                    else if (n == 2)
                    {
                        key0 = engine.Buf[1] != '0' ? "確定" : engine.Buf[0] == '9' ? "下頁" : "";
                        lines = new[] { "請選擇韻母", "按０確定" };
                    }
                    else { key0 = "所有字"; lines = new[] { "請選擇聲調", "按０顯示所有字" }; }
                    return;
                case Engine.Zhuyin:
                    // FUN_1800bac70：只有「冇聲母」(0) 時 0 鍵係檢字
                    if (n == 1 && engine.Buf == "0") { key0 = "檢字"; lines = new[] { "請選擇介音或韻母", "按省略號選其他" }; }
                    else
                    {
                        key0 = "確定";
                        lines = n == 1 ? new[] { "請選擇介音或韻母", "只有聲母按０確定" } : new[] { "請選擇韻母", "如漢字沒韻母按０確定" };
                    }
                    return;
                case Engine.Canton:
                    // FUN_1800b9790：取碼時冇提示行；冇預覽字 0 鍵係「標點」
                    key0 = engine.HasPreview ? "選字" : "標點";
                    return;
            }
            return;
        }
        // 靜止
        // 有關聯字時「.」對調做「0」：注音即係冇聲母 (韻母)，其他輸入法係標點
        if (hasRel) { key0 = "選字"; dot = mode == Engine.Zhuyin ? "韻母" : "標點"; } else key0 = mode == Engine.Zhuyin ? "韻母" : "標點";
        lines = engine.InfoLines();
        if (lines != null) return;
        switch (mode)
        {
            case Engine.Glyph: lines = new[] { "選字形的左上方之特徵", "按０輸入標點／檢字" }; break;
            case Engine.Stroke: lines = new[] { "獨體字﹕順序第一二筆", "分體字﹕左邊頭兩筆", "　　　　右邊第一筆" }; break;
            case Engine.Pinyin: lines = new[] { "請選擇聲母", "如漢字沒聲母選韻母分類" }; break;
            case Engine.Zhuyin: lines = new[] { "請選擇聲母", "如漢字沒聲母按０" }; break;
            case Engine.Canton: lines = new[] { "請順序輸入拼音", "按０確定" }; break;   // FUN_1800b9790
        }
    }

    // 畫面先畫落 GDI DIB section：圖片用 GDI+、文字用 GDI ClearType，兩邊寫同一塊記憶體，效果同原版一樣
    private Gdi.Surface frame;

    protected override void OnPaint(PaintEventArgs e)
    {
        if (frame == null || frame.Width != ClientSize.Width || frame.Height != ClientSize.Height)
        {
            if (frame != null) frame.Dispose();
            frame = new Gdi.Surface(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        }
        using (Graphics fg = Graphics.FromImage(frame.Bitmap)) PaintFrame(fg, frame.Dc);
        e.Graphics.DrawImageUnscaled(frame.Bitmap, 0, 0);
    }

    private void PaintFrame(Graphics g, IntPtr textDc)
    {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float s = S;
        // 原版視窗用 color key 0xC0C0C0 (SetLayeredWindowAttributes …, 3)：先填 key 色，
        // 皮膚圖半透明嘅邊會混灰色，完全透明嘅位就變透明
        g.Clear(ColorKey);
        var skinImage = (Bitmap)skin.Get("q9.png");
        if (!(skinImage.Width == ClientSize.Width && skinImage.Height == ClientSize.Height && frame != null
              && BlendExact(g, frame.Bitmap, skinImage, 0, 0)))
            g.DrawImage(skinImage, 0, 0, ClientSize.Width, ClientSize.Height);

        bool numberLike = engine.Mode == Engine.Number || engine.Mode == Engine.Emoji;
        Draw(g, ModeImage(NextModeValue), Px(skin.Area("CUR_IM_BUT")));
        Draw(g, numberLike ? "enter_chi.png" : "enter.png", Px(skin.Area("ENTER_BUT")));
        Draw(g, engine.Lang == 0 ? (engine.OutputConvert ? "Button06_sim_on.png" : "Button06_sim.png")
            : (engine.OutputConvert ? "Button06_trad_on.png" : "Button06_trad.png"), Px(skin.Area("NEXT_IM_BUT")));

        string key0, dot, minus, plus;
        string[] lines;
        PageLabels(out key0, out dot, out minus, out plus, out lines);
        Draw(g, minus, Px(skin.Area("MINUS_BUT")));
        Draw(g, plus, Px(skin.Area("PLUS_BUT")));

        // 九宮格：格仔底色 → 底圖 → 四角三角 → 格線
        RectangleF input = Input;
        bool select = engine.State == St.Select && engine.Kind != "emojicat" && engine.Kind != "punct" && engine.Kind != "shape-extra";
        bool choosingMethod = engine.Kind == "lookup-menu" || engine.Kind == "stroke-count"
            || engine.Kind == "radical-count" || engine.Kind == "radical-stroke" || engine.Kind == "sound-extra";
        // 靜止時 (有冇關聯字都係) 原版畫 Button09.png (白色 alpha 204，FUN_1800b0970 圖 1 → 資源 514)，
        // 保留皮膚圖案；取碼同選字頁仍用實色。
        bool related = ShowingRelated;
        bool idleGrid = engine.State == St.Idle && engine.Mode != Engine.Number;
        bool useButton09 = !related;   // 數字模式都係 (原版截圖)
        string image = engine.Image;
        using (var fill = new SolidBrush(idleGrid ? RelatedFill : Fill))
        using (var empty = new SolidBrush(Empty))
        {
            // FUN_1800b0970 每個狀態都先畫圖 1 (Button09.png，白色 alpha 204 連「取消」)；
            // 關聯字頁另外處理 (原版用 kFillBlankColor 填 0 行)
            if (useButton09) Draw(g, "Button09.png", input);
            else g.FillRectangle(fill, input);
            for (int key = 1; key <= 9; key++)
            {
                // PY0 / PYREL / PYNONE 本身已經畫咗 7 鍵灰格 (有圓角)，唔使另外填
                bool blank = select && engine.Kind != "radical-stroke" && engine.Cand[key - 1] == null || PinyinDisabled(key);
                if (blank) g.FillRectangle(empty, GrayCell(key));
            }
            if (image != null)
            {
                RectangleF grid = new RectangleF(input.X, input.Y, input.Width, input.Width * 688f / 682f);
                Draw(g, image, grid);
            }
            if (select && !choosingMethod && !engine.LookupSym)
            {
                // FUN_1800b0580 / FUN_1800b55c0：每格四角三角形，格邊內縮 GridLine*，邊長 CornerSize，
                // 用 GDI Polygon (NULL_PEN) 填 0xC0C0C1
                g.Flush(FlushIntention.Sync);
                Func<string, float, int> px = (k, d) => (int)(skin.Value("UI." + k, d) * s);
                int l = px("GridLineLeft", 4), t = px("GridLineTop", 5), rr = px("GridLineRight", 3), bb = px("GridLineBottom", 0);
                int cs = px("CornerSize", 40), w = CellPx;
                for (int key = 1; key <= 9; key++)
                {
                    if (engine.Cand[key - 1] == null) continue;
                    RectangleF r = Cell(key);
                    int x = (int)r.X, y = (int)r.Y;
                    Gdi.Polygon(textDc, Empty, new Point(x + l, y + t), new Point(x + l + cs, y + t), new Point(x + l, y + t + cs));
                    Gdi.Polygon(textDc, Empty, new Point(x + w - rr, y + t), new Point(x + w - rr, y + t + cs), new Point(x + w - rr - cs, y + t));
                    Gdi.Polygon(textDc, Empty, new Point(x + l, y + w - bb), new Point(x + l, y + w - bb - cs), new Point(x + l + cs, y + w - bb));
                    Gdi.Polygon(textDc, Empty, new Point(x + w - rr, y + w - bb), new Point(x + w - rr - cs, y + w - bb), new Point(x + w - rr, y + w - bb - cs));
                }
                Gdi.GdiFlush();
            }
        }
        Draw(g, "Button09Transparent.png", input);
        if (dot == "取消" && !useButton09) g.DrawImage(CancelText, input);

        // 資料視窗下面嘅收起掣
        Draw(g, "Button17.png", Px(skin.Area("MESSAGE_AREA_HIDE")));

        // 文字：用 GDI (同原版一樣 TextOutW + 基線對齊)
        g.Flush(System.Drawing.Drawing2D.FlushIntention.Sync);
        IntPtr hdc = textDc;
        Gdi.GdiFlush();
        {
            float adjH = skin.Value("UI.TextAdjustH", 36), adjV = skin.Value("UI.TextAdjustV", 105);
            int big = Pt(skin.Value("UI.FontSize", 84)), rel = Pt(skin.Value("UI.RelTextFontSize", 72)) - 1;
            string face = FontFace;
            bool emojiList = engine.Kind == "emoji";
            if (select || engine.Mode == Engine.Number)
            {
                for (int key = 1; key <= 9; key++)
                {
                    string t = engine.Mode == Engine.Number ? key.ToString() : engine.Cand[key - 1];
                    if (t == null) continue;
                    RectangleF r = Cell(key);
                    // 半形字當半格計：「()」用全大字 (原版截圖)，「其他」先縮細
                    var parts = Engine.Elements(t);
                    int len = (int)Math.Ceiling(parts.Sum(p => p.Length == 1 && p[0] < 0x100 ? 0.5 : 1.0));
                    int size = len <= 1 ? big : Math.Max(Pt(28), big * 2 / (len + 1));
                    Color color = Extended(t) && engine.Kind != "radical-count" ? Color.Red : Color.Black;   // 部首頁原版一律黑色
                    int x = (int)r.X + (int)(adjH * s);
                    // 全部半形 (數字、筆劃數目等)：一兩隻字喺全形位靠右；三隻 (「>27」) 用全大字喺格入面置中 (原版截圖)
                    bool allHalf = t.Length > 0 && t.All(ch => ch < 0x100);
                    if (allHalf && t.Length <= 2) x += (2 - t.Length) * big / 2;
                    else if (allHalf && t.Length == 3) { size = big; x = (int)r.X + (CellPx - 3 * big / 2) / 2 + 2; }
                    // 部首第 0x51 個 (阝 右耳) 原版向右移半個字 (FUN_1800b0970)
                    if (engine.RadicalIndexAt(key) == 0x51) x += size / 2;
                    Gdi.Text(hdc, t, emojiList ? "Segoe UI Emoji" : face, size, color, x, (int)r.Y + (int)(adjV * s));
                }
            }
            else if (engine.Kind == "rel" || engine.Kind == "code" || engine.Kind == "pyhint")
            {
                Color color = engine.Kind == "rel" ? RelColor : PreviewColor;
                for (int key = 1; key <= 9; key++)
                {
                    string t = engine.Cand[key - 1];
                    if (t == null) continue;
                    RectangleF r = Cell(key);
                    if (engine.Kind == "pyhint")
                        Gdi.Text(hdc, t, face, big, Color.Black, (int)r.X + (int)(adjH * s), (int)r.Y + (int)(adjV * s));
                    else
                        Gdi.Text(hdc, t, face, rel, Extended(t) ? Color.Red : color,
                            (int)r.X + (int)(skin.Value("UI.TopLeftTextAdjustH", 3) * s) + 1, (int)r.Y + (int)(skin.Value("UI.TopLeftTextAdjustV", 69) * s) + 1);   // 關聯字、預覽字同一位置 (原版截圖)
                }
            }
            else if (engine.Kind == "emojicat")
            {
                // 表情分類圖已喺 Q9_emoji.png
            }

            // 0 行標籤：兩隻字分開放喺 1、2 格位置
            RectangleF z = ZeroKey;
            int baseY = (int)z.Y + (int)(adjV * s);   // 原版截圖：關聯字頁 0 行標籤唔使上移
            var label = Engine.Elements(key0);
            float key0Adj = skin.Value("UI.Key0AdjustH", -20);
            int x0 = (int)z.X + (int)((adjH + key0Adj) * s), x1 = (int)z.X + CellPx + (int)(adjH * s) - 1;
            if (label.Count == 4)
                // 「韻母組合」：原版用 0.6 倍字體一次過畫 (FUN_1800b7eb0)
                Gdi.Text(hdc, key0, face, (int)(big * 0.6), Color.Black, x0, baseY);
            else if (label.Count == 3)
                // 「所有字」：原版同「確　定」一樣由第一隻字位置連續畫，每字一個全形位
                for (int i = 0; i < 3; i++) Gdi.Text(hdc, label[i], face, big, Color.Black, x0 + (x1 - x0) * i / 2, baseY);
            else
            {
                if (label.Count > 0) Gdi.Text(hdc, label[0], face, big, Color.Black, x0 + (engine.Mode == Engine.Number ? big / 2 : 0), baseY);
                if (label.Count > 1) Gdi.Text(hdc, label[1], face, big, Color.Black, x1, baseY);
            }
            if (dot == "標點" || dot == "韻母")
            {
                // 有關聯字：FUN_1800b0700 (格 10) 先用 kFillBlankColor 填細格 (原版截圖逐像素對過)
                if (related) Gdi.Fill(hdc, Fill, DotBox.X, DotBox.Y, DotBox.Width, DotBox.Height);
                // 字高 = 格 ÷ 2.5 (FUN_1800b6380 等：(short)((double)格 / 2.5))
                Gdi.Text(hdc, dot, face, (int)(CellPx / 2.5), Color.Black, (int)DotKey.X + (int)(4 * s) - 1, (int)DotKey.Y + (int)(101 * s));
            }
            else if (dot == ".")
            {
                // 數字模式：「.」格先用 kFillBlankColor 填 (遮住 Button09 嘅「取消」，範圍同關聯字頁細格一樣)
                if (engine.Mode == Engine.Number) Gdi.Fill(hdc, Fill, DotBox.X, DotBox.Y, DotBox.Width, DotBox.Height);
                int dx = engine.Mode == Engine.Number ? -6 : 0, dy = engine.Mode == Engine.Number ? -2 : 0;   // 原版截圖 (大細 300)
                Gdi.Text(hdc, dot, face, big, Color.Black, (int)(DotKey.X + adjH * s) + dx, baseY + dy);
            }

            // 資料視窗：14px 細明體點陣字，白色
            RectangleF m = Px(skin.Area("MESSAGE_AREA"));
            if (paused) lines = new[] { "已暫停（按 Shift 恢復）" };
            else if (engine.Note.Length > 0) lines = new[] { engine.Note };
            else if (engine.Mode == Engine.Canton && engine.State == St.Compose) { DrawCanton(g, hdc, face, m); lines = null; }
            if (lines != null)
                for (int i = 0; i < lines.Length; i++)
                    Gdi.Text(hdc, lines[i], face, Pt(31) + 1, Color.White, (int)(m.X + 9 * s) + 1, (int)(m.Y + (64 + i * 57.5f) * s) + 1);
            Gdi.GdiFlush();
        }
    }

    // 粵拼拼法清單：每頁三行，揀中嗰行淺色底黑字，右下角「+」提示可以轉 (原版畫面)
    private void DrawCanton(Graphics g, IntPtr hdc, string face, RectangleF m)
    {
        float s = S;
        var spell = data.CantonSpellings(engine.CantonKeys);
        int choice = engine.CantonChoice, first = (choice - 1) / 3 * 3;
        for (int row = 0; row < 3 && first + row < spell.Count; row++)
        {
            bool chosen = first + row + 1 == choice;
            int x = (int)(m.X + 20 * s) + 2, baseline = (int)(m.Y + 64 * s) + 1 + (int)Math.Round(row * 58f * s);
            if (chosen)
            {
                Gdi.Fill(hdc, Color.FromArgb(220, 231, 234), (int)(m.X + 17.8f * s), (int)(m.Y + 17.8f * s) + (int)Math.Round(row * 58f * s), (int)(299 * s), (int)(58 * s));
            }
            Gdi.Text(hdc, spell[first + row], face, Pt(31) + 1, chosen ? Color.Black : Color.White, x, baseline);
        }
        // 「+」原版有 ClearType 彩邊，字型未搞清楚；呢個位置最接近 (原版截圖)
        Gdi.Text(hdc, "+", face, Pt(44), Color.FromArgb(140, 140, 140), (int)(m.X + 398 * s) + 1, (int)(m.Y + 184 * s) - 2);
    }

    // ---------- 輸出 ----------
    private void Deliver(string text)
    {
        Sent.Add(text);
        if (settings["History"] != 0)
        {
            string dir = Path.Combine(data.UserDir, "typeHistory");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".txt"), text, Encoding.UTF8);
        }
        if (Capture_) return;
        if (IsHandleCreated) BeginInvoke(new Action(() => Native.Type(text))); else Native.Type(text);
    }

    // ---------- 指令 ----------
    internal void Press(char key)
    {
        if (key == '*') { settings["Top"] = 1 - settings["Top"]; Dock_(settings["Top"] != 0); return; }
        if (key == '/') { NextMode(); return; }
        engine.Key(key);
        Invalidate();
    }

    private void NextMode()
    {
        // 「/」或者輸入法切換鍵：次序同原版一樣 (筆劃 → 字形 → 拼音 → 注音 → 粵拼 → 數字 → 表情符號)
        int next = NextModeValue;
        if (next <= Engine.Canton) settings["Mode"] = next;
        engine.SetMode(next);
        Invalidate();
    }

    private static readonly int[] ChineseModes = { Engine.Stroke, Engine.Glyph, Engine.Pinyin, Engine.Canton, Engine.Zhuyin };
    private static readonly string[] ChineseNames = { "九方筆劃輸入", "九方字形輸入", "九方拼音輸入", "九方粵語拼音輸入", "九方注音輸入" };

    private List<int> EnabledChineseModes()
    {
        var order = new[] { Engine.Stroke, Engine.Glyph, Engine.Pinyin, Engine.Zhuyin, Engine.Canton }
            .Where(mode => engine.SupportsMode(mode) && settings["On" + mode] != 0).ToList();
        if (order.Count == 0) order.Add(Engine.Stroke);
        return order;
    }

    private void SetLanguage(int lang)
    {
        int previousMode = engine.Mode;
        settings["Lang"] = lang;
        engine.SetLang(lang);
        if (!engine.SupportsMode(previousMode)) engine.SetMode(EnabledChineseModes().First());
    }

    private void SetChinese(int mode) { settings["Mode"] = mode; settings["On" + mode] = 1; engine.SetMode(mode); Invalidate(); }

    private void ToggleChinese(int mode)
    {
        bool on = settings["On" + mode] == 0;
        if (!on && EnabledChineseModes().Count == 1) { Native.MessageBeep(0); return; }  // 最少留一個
        settings["On" + mode] = on ? 1 : 0;
        if (on) SetChinese(mode);
        else if (engine.Mode == mode) SetChinese(EnabledChineseModes().First());
    }
    internal void SetModeForPreview(int mode) { engine.SetMode(mode); }

    private void TogglePause()
    {
        paused = !paused;
        if (paused) Hide(); else { Native.ShowWindow(Handle, 4); Invalidate(); }
    }

    // ---------- 鍵盤攔截 ----------
    private static readonly int[] NavPad = { 0x2D, 0x23, 0x28, 0x22, 0x25, 0x0C, 0x27, 0x24, 0x26, 0x21 };

    private char MapKey(int vk, int flags)
    {
        bool busy = engine.State != St.Idle && engine.Kind != "emojicat";
        if (busy && vk == 0x08) return '\b';
        if (busy && vk == 0x1B) return '.';
        int mode = settings["KeyMode"];
        if (mode == 0 || mode == 1)
        {
            if (vk == 0x6B) return '+';
            if (vk == 0x6D) return '-';
            if (vk == 0x6A) return '*';
            if (vk == 0x6F) return '/';
        }
        if (mode == 0)
        {
            if (vk >= 0x60 && vk <= 0x69) return (char)('0' + vk - 0x60);
            if (vk == 0x6E) return '.';
        }
        else if (mode == 1 && (flags & 1) == 0)
        {
            int i = Array.IndexOf(NavPad, vk);
            if (i >= 0) return (char)('0' + i);
            if (vk == 0x2E) return '.';
        }
        else if (mode == 2)
        {
            int i = "M\0\0\0JKLUIO789".IndexOf((char)vk);       // 「7890」「UIOP」「JKL;」「M,./」
            if (i == 0) return '0';
            if (i >= 4 && i <= 12) return (char)('1' + i - 4);
            switch (vk) { case 0x30: return '/'; case 0x50: return '*'; case 0xBA: return '-'; case 0xBC: return '0'; case 0xBE: return '.'; case 0xBF: return '+'; }
        }
        else if (mode == 3)
        {
            if (vk >= 0x30 && vk <= 0x39) return (char)vk;
            switch (vk) { case 0xBE: return '.'; case 0xBB: return '+'; case 0xBD: return '-'; }
        }
        return '\0';
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(lParam), flags = Marshal.ReadInt32(lParam, 8);
            int message = wParam.ToInt32();
            bool down = message == 0x100 || message == 0x104;
            bool shift = vk == 0x10 || vk == 0xA0 || vk == 0xA1;
            if ((flags & 0x10) == 0 || AcceptInjected && vk != 0xE7)       // 忽略注入嘅按鍵 (包括自己送出嘅字)
            {
                if (shift)
                {
                    if (down) { if (!shiftAlone) { shiftAlone = true; shiftDown = DateTime.UtcNow; } }
                    else
                    {
                        if (shiftAlone && settings["ShiftToggle"] != 0 && (DateTime.UtcNow - shiftDown).TotalMilliseconds < 350)
                            BeginInvoke(new Action(TogglePause));
                        shiftAlone = false;
                    }
                }
                else
                {
                    shiftAlone = false;
                    if (!down)
                    {
                        if (heldKeys.Remove(vk)) return (IntPtr)1;     // 食埋已攔截按鍵嘅放開事件
                    }
                    else
                    {
                        bool modifier = Native.Down(0x11) || Native.Down(0x12) || Native.Down(0x5B) || Native.Down(0x5C);
                        bool shifted = Native.Down(0x10);
                        if (!paused && !modifier && !(shifted && settings["KeyMode"] >= 2))
                        {
                            char key = MapKey(vk, flags);
                            if (key != '\0' && HandleKey(key)) { heldKeys.Add(vk); return (IntPtr)1; }
                        }
                    }
                }
            }
        }
        return Native.CallNextHookEx(hook, code, wParam, lParam);
    }

    private readonly HashSet<int> heldKeys = new HashSet<int>();

    private bool HandleKey(char key)
    {
        if (key == '*' || key == '/') { Press(key); return true; }
        if (engine.Mode == Engine.Number) return false;
        bool handled = engine.Key(key);
        Invalidate();
        return handled;
    }

    // ---------- 滑鼠 ----------
    private PointF Unit(Point p) { return new PointF(p.X, p.Y); }

    protected override void OnMouseEnter(EventArgs e) { hovering = true; Opacity = 1.0; base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e)
    {
        hovering = false; Opacity = settings["Opacity"] / 100.0;
        base.OnMouseLeave(e);
    }

    private static readonly string[] Buttons = { "TITLE_BUT", "CUR_IM_BUT", "BACKSPACE_BUT", "ENTER_BUT", "SPACE_BUT",
        "NEXT_IM_BUT", "SWITCH_WIN_BUT", "HELP_BUT", "Q9_INPUT_AREA", "MINUS_BUT", "PLUS_BUT", "PRE_PAGE_BUT", "NEXT_PAGE_BUT" };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        PointF p = Unit(e.Location);
        if (e.Button == MouseButtons.Left && !Buttons.Any(name => Px(skin.Area(name)).Contains(p)))
        {
            dragging = true; dragOrigin = e.Location; Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // 可以拖動嘅位置顯示四向箭咀 (原版 IDC_SIZEALL)，按鈕上面用普通箭咀
        PointF p = Unit(e.Location);
        Cursor want = dragging || !Buttons.Any(name => Px(skin.Area(name)).Contains(p)) ? Cursors.SizeAll : Cursors.Default;
        if (Cursor != want) Cursor = want;
        if (!dragging) return;
        Point screen = PointToScreen(e.Location);
        Location = new Point(screen.X - dragOrigin.X, screen.Y - dragOrigin.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (dragging) { dragging = false; Capture = false; return; }
        if (e.Button == MouseButtons.Right) { OpenMenu(e.Location); return; }
        if (e.Button == MouseButtons.Left) Click_(Unit(e.Location), e.Location);
    }

    internal void Click_(PointF p, Point pixel)
    {
        Func<string, bool> hit = name => Px(skin.Area(name)).Contains(p);
        if (hit("TITLE_BUT")) OpenMenu(pixel);
        else if (hit("HELP_BUT")) OpenQuery(pixel);
        else if (hit("CUR_IM_BUT")) NextMode();
        else if (hit("NEXT_IM_BUT")) { engine.OutputConvert = !engine.OutputConvert; settings["Convert"] = engine.OutputConvert ? 1 : 0; }
        else if (hit("SWITCH_WIN_BUT"))
        {
            settings["Size"] = Sizes[(Array.IndexOf(Sizes, settings["Size"]) + 1) % 5];   // 200-600 輪流切換
            ApplySize();
        }
        else if (hit("BACKSPACE_BUT")) { if (!engine.Key('\b')) Native.Tap(0x08); }
        else if (hit("ENTER_BUT"))
        {
            if (engine.Mode == Engine.Number || engine.Mode == Engine.Emoji)
                engine.SetMode(engine.SupportsMode(settings["Mode"]) ? settings["Mode"] : EnabledChineseModes().First());
            else Native.Tap(0x0D);
        }
        else if (hit("SPACE_BUT")) Native.Tap(0x20);
        else if (hit("MINUS_BUT")) engine.Key('-');
        else if (hit("PLUS_BUT"))
        {
            if (engine.Kind == "phrase" && engine.Last.Length != 0) engine.CyclePhraseLength();
            else engine.Key('+');
        }
        else if (hit("PRE_PAGE_BUT")) { settings["Top"] = 1; Dock_(true); }
        else if (hit("NEXT_PAGE_BUT")) { settings["Top"] = 0; Dock_(false); }
        else if (ZeroKey.Contains(p)) engine.Key('0');
        else if (DotKey.Contains(p)) engine.Key('.');
        else
            for (int key = 1; key <= 9; key++)
            {
                RectangleF cell = Cell(key);
                if (!cell.Contains(p)) continue;
                bool corner = InTopLeftHalf(cell, p, engine.State == St.Compose);
                if (engine.Mode == Engine.Number) Native.Type(key.ToString());
                else if (engine.State == St.Compose && (engine.Mode == Engine.Pinyin || engine.Mode == Engine.Zhuyin))
                {
                    char tone = CornerOf(cell, p);
                    // FUN_1800b4de0：「點擊左上角的字直接上字」關閉時，取碼區碼會 & 0xEEFF
                    // (左上變中間、左下變右上)，同原版一樣照做
                    if (settings["DirectTopLeft"] == 0) tone = tone == '7' ? '\0' : tone == '1' ? '9' : tone;
                    engine.MouseKey((char)('0' + key), tone);
                }
                else if (!(corner && settings["DirectTopLeft"] != 0 && engine.PickSmall(key - 1))) engine.Key((char)('0' + key));
                break;
            }
        Invalidate();
    }

    // ---------- 選單 ----------
    private ToolStripMenuItem Item(ToolStripItemCollection parent, string text, bool check, Action action)
    {
        var item = new ToolStripMenuItem(text) { Checked = check };
        if (action != null) item.Click += delegate { action(); Invalidate(); };
        parent.Add(item);
        return item;
    }

    private void Toggle(ToolStripItemCollection parent, string text, string key, Action after)
    {
        Item(parent, text, settings[key] != 0, delegate { settings[key] = 1 - settings[key]; if (after != null) after(); });
    }

    private void Edit(string name) { Process.Start("notepad.exe", "\"" + data.UserFile(engine.Lang, name, true) + "\""); }

    private void OpenMenu(Point at)
    {
        menu.Items.Clear();
        var root = menu.Items;
        var language = Item(root, "設定輸入文字", false, null).DropDownItems;
        Item(language, "繁體中文", engine.Lang == 0, delegate { SetLanguage(0); });
        Item(language, "簡體中文", engine.Lang == 1, delegate { SetLanguage(1); });
        Item(root, engine.Lang == 0 ? "輸出簡體字" : "輸出繁體字", engine.OutputConvert,
            delegate { engine.OutputConvert = !engine.OutputConvert; settings["Convert"] = engine.OutputConvert ? 1 : 0; });
        var current = Item(root, "目前輸入法", false, null).DropDownItems;
        var modes = Item(root, "開啟／關閉輸入法", false, null).DropDownItems;
        modes.Add(new ToolStripLabel("主要輸入法：可選擇多個，用「/」或輸入法鍵輪流切換") { ForeColor = SystemColors.GrayText });
        for (int i = 0; i < ChineseModes.Length; i++)
        {
            int mode = ChineseModes[i];
            Item(current, ChineseNames[i], engine.Mode == mode, delegate { SetChinese(mode); }).Enabled = engine.SupportsMode(mode);
            Item(modes, ChineseNames[i], settings["On" + mode] != 0, delegate { ToggleChinese(mode); }).Enabled = engine.SupportsMode(mode);
        }
        modes.Add(new ToolStripSeparator());
        Toggle(modes, "數字輸入（用「/」切換）", "ModeNumber", null);
        Toggle(modes, "表情符號輸入（用「/」切換）", "ModeEmoji", null);
        var history = Item(root, "輸入記錄", false, null).DropDownItems;
        Toggle(history, "記錄輸入內容", "History", null);
        Item(history, "顯示今日記錄", false, delegate
        {
            string file = Path.Combine(data.UserDir, @"typeHistory\" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
            if (File.Exists(file)) Process.Start("notepad.exe", "\"" + file + "\"");
        });
        var sizes = Item(root, "視窗大小切換", false, null).DropDownItems;
        foreach (int size in Sizes.Reverse())
        {
            int value = size;
            Item(sizes, value + " Pixel", settings["Size"] == value, delegate { settings["Size"] = value; ApplySize(); });
        }
        var opacity = Item(root, "靜止時視窗的透明度", false, null).DropDownItems;
        foreach (int percent in new[] { 100, 90, 80, 70, 60, 50, 40, 30, 20 })
        {
            int value = percent;
            Item(opacity, value == 100 ? "關閉" : value + " %", settings["Opacity"] == value,
                delegate { settings["Opacity"] = value; Opacity = value / 100.0; });
        }
        var keys = Item(root, "設定鍵盤輸入鍵", false, null).DropDownItems;
        string[] keyNames = { "使用數字鍵盤輸入（開 NumLock）", "使用數字鍵盤輸入（關 NumLock）", "使用文字鍵輸入", "使用上排數字鍵輸入" };
        for (int i = 0; i < keyNames.Length; i++)
        {
            int value = i;
            Item(keys, keyNames[i], settings["KeyMode"] == value, delegate { settings["KeyMode"] = value; });
        }
        keys.Add(new ToolStripSeparator());
        Toggle(keys, "按 Shift 隱藏／重現九方", "ShiftToggle", null);
        Toggle(root, "有部首用部首－不用兼容碼（筆劃輸入）", "Radical", delegate { engine.RadicalFirst = settings["Radical"] != 0; engine.Reset(); });
        Toggle(root, "輸入過的字自動調前", "UpdateOrder", delegate { engine.UpdateOrder = settings["UpdateOrder"] != 0; });
        var related = Item(root, "設定關聯字", false, null).DropDownItems;
        string[] relNames = { "固定關聯字", "固定＋自定關聯字", "動態關聯字", "關閉關聯字" };
        for (int i = 0; i < relNames.Length; i++)
        {
            int value = i;
            Item(related, relNames[i], engine.RelMode == value, delegate { settings["RelMode"] = value; engine.RelMode = value; engine.Reset(); });
            if (i == 1) Item(related, "編輯自定關聯字…", false, delegate { Edit("Q9UserRelChar.TXT"); });
        }
        Toggle(root, "點擊左上角的字直接上字", "DirectTopLeft", null);
        Toggle(root, "輸出半形常用符號", "HalfPunct", delegate { engine.HalfPunct = settings["HalfPunct"] != 0; });
        root.Add(new ToolStripSeparator());
        Item(root, "編輯詞語／自定短句…", false, delegate { Edit("Q9PHRASE.TXT"); });
        Item(root, "編輯自定常用字句…", false, delegate { Edit("yourAlwaysUse.txt"); });
        root.Add(new ToolStripSeparator());
        Item(root, "說明", false, OpenHelp);
        Item(root, "關於", false, delegate
        {
            MessageBox.Show("九方 W10 重建版\n\n按原裝九方 W10 專業版 1.1.0.0 嘅碼表同行為重寫，需要本機已安裝原版。\n筆劃、字形、拼音、粵拼、注音、關聯字、標點、表情符號已重建。",
                "關於", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        Item(root, "結束", false, Close);
        menu.Show(this, at);
    }

    private void OpenQuery(Point at)
    {
        menu.Items.Clear();
        Item(menu.Items, "九方說明", false, OpenHelp);
        Item(menu.Items, "開啟自定資料夾", false, delegate { Directory.CreateDirectory(data.UserDir); Process.Start(data.UserDir); });
        menu.Show(this, at);
    }

    private void OpenHelp()
    {
        if (!data.Files.Exists("q9help.chm")) return;
        Process.Start("hh.exe", "\"" + data.Files.FilePath("q9help.chm") + "\"");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { menu.Dispose(); if (skin != null) skin.Dispose(); }
        base.Dispose(disposing);
    }
}

// GDI+ 唔會自動換字體，正黑體冇嘅 CJK 擴展 B 區同香港字會變方格。
// 用 WPF GlyphTypeface 查邊隻字體有齊所有字，揀第一隻有嘅。
internal static class Glyphs
{
    private static readonly string[] Chain = { "MingLiU_HKSCS", "MingLiU_HKSCS-ExtB", "SimSun-ExtB", "SimSun-ExtG", "Segoe UI Symbol", "Segoe UI Emoji" };
    private static readonly Dictionary<string, System.Windows.Media.GlyphTypeface> faces = new Dictionary<string, System.Windows.Media.GlyphTypeface>();
    private static readonly Dictionary<string, string> picked = new Dictionary<string, string>();

    private static System.Windows.Media.GlyphTypeface Face(string family)
    {
        System.Windows.Media.GlyphTypeface face;
        if (faces.TryGetValue(family, out face)) return face;
        try
        {
            var typeface = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily(family),
                System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
            // 冇呢隻字體時 WPF 會靜靜雞換第二隻，所以要核對名
            if (!typeface.TryGetGlyphTypeface(out face) ||
                !face.FamilyNames.Values.Concat(face.Win32FamilyNames.Values).Any(n => n.Equals(family, StringComparison.OrdinalIgnoreCase)))
                face = null;
        }
        catch { face = null; }
        faces[family] = face;
        return face;
    }

    internal static bool Covers(string family, string text)
    {
        var face = Face(family);
        if (face == null) return false;
        for (int i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) cp = char.ConvertToUtf32(text[i], text[++i]);
            if (cp < 0x80 || cp == 0x3000) continue;
            if (!face.CharacterToGlyphMap.ContainsKey(cp)) return false;
        }
        return true;
    }

    // 回傳需要換嘅字體 (呼叫者負責 Dispose)；原字體已經有齊就回傳 null
    internal static Font Fit(Font font, string text)
    {
        string key = font.Name + "\u0001" + text, family;
        if (!picked.TryGetValue(key, out family))
        {
            family = null;
            if (!Covers(font.Name, text))
                foreach (string candidate in Chain)
                    if (Covers(candidate, text)) { family = candidate; break; }
            if (picked.Count > 20000) picked.Clear();
            picked[key] = family;
        }
        return family == null ? null : new Font(family, font.Size, font.Style, font.Unit);
    }
}

// GDI 文字：同原版一樣用 TextOutW、基線對齊，字體連結 (例如細明體_HKSCS → ExtB) 交俾系統處理
internal static class Gdi
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic,
        uint underline, uint strike, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, int color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextAlign(IntPtr dc, uint align);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool TextOutW(IntPtr dc, int x, int y, string text, int length);
    [DllImport("gdi32.dll")] internal static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    // 32 位元 DIB section：GDI 用 Dc 畫字，GDI+ 用 Bitmap 畫圖，共用同一塊像素
    internal sealed class Surface : IDisposable
    {
        internal readonly IntPtr Dc;
        internal readonly Bitmap Bitmap;
        internal readonly int Width, Height;
        private readonly IntPtr dib, old;

        internal Surface(int width, int height)
        {
            Width = width; Height = height;
            var info = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            IntPtr bits;
            Dc = CreateCompatibleDC(IntPtr.Zero);
            dib = CreateDIBSection(Dc, ref info, 0, out bits, IntPtr.Zero, 0);
            old = SelectObject(Dc, dib);
            Bitmap = new Bitmap(width, height, width * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, bits);
        }

        public void Dispose()
        {
            Bitmap.Dispose();
            SelectObject(Dc, old);
            DeleteObject(dib);
            DeleteDC(Dc);
        }
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    [DllImport("gdi32.dll")] private static extern bool Polygon(IntPtr dc, POINT[] points, int count);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int obj);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    // FUN_18010dd90：實心畫刷 + NULL_PEN 嘅 Polygon (右同下邊唔填)
    internal static void Polygon(IntPtr dc, Color color, params Point[] points)
    {
        IntPtr brush = CreateSolidBrush(color.R | color.G << 8 | color.B << 16);
        IntPtr oldBrush = SelectObject(dc, brush), oldPen = SelectObject(dc, GetStockObject(8));
        Polygon(dc, points.Select(p => new POINT { X = p.X, Y = p.Y }).ToArray(), points.Length);
        SelectObject(dc, oldPen); SelectObject(dc, oldBrush);
        DeleteObject(brush);
    }

    internal static void Fill(IntPtr dc, Color color, int x, int y, int w, int h)
    {
        var r = new RECT { L = x, T = y, R = x + w, B = y + h };
        IntPtr brush = CreateSolidBrush(color.R | color.G << 8 | color.B << 16);
        FillRect(dc, ref r, brush);
        DeleteObject(brush);
    }

    internal static void Text(IntPtr dc, string text, string face, int size, Color color, int x, int baseline)
    {
        if (string.IsNullOrEmpty(text) || size <= 0) return;
        uint charset = face == "Segoe UI Emoji" ? 1u : face == "SimSun" ? 0x86u : 0x88u;
        // 同原版 FUN_18010ef00 一樣：正數高度、OUT_TT_ONLY_PRECIS、CLEARTYPE_NATURAL_QUALITY
        IntPtr font = CreateFontW(size, 0, 0, 0, 400, 0, 0, 0, charset, 7, 0, 6, 0, face);
        IntPtr old = SelectObject(dc, font);
        SetBkMode(dc, 1);                                  // TRANSPARENT
        SetTextAlign(dc, 24);                              // TA_BASELINE | TA_LEFT
        SetTextColor(dc, color.R | color.G << 8 | color.B << 16);
        TextOutW(dc, x, baseline, text, text.Length);
        SelectObject(dc, old);
        DeleteObject(font);
    }
}

internal static class Native
{
    internal delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public Keyboard Keyboard;
        [FieldOffset(0)] public Mouse Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool MessageBeep(uint type);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    internal static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

    internal static void Activate(IntPtr window)
    {
        uint current = GetCurrentThreadId();
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        bool attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        try { SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }

    internal static bool Type(string text)
    {
        var inputs = new Input[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2].Type = inputs[i * 2 + 1].Type = 1;
            inputs[i * 2].Data.Keyboard = new Keyboard { Scan = text[i], Flags = 4 };
            inputs[i * 2 + 1].Data.Keyboard = new Keyboard { Scan = text[i], Flags = 6 };
        }
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) == inputs.Length;
    }

    internal static void Tap(ushort key)
    {
        var inputs = new Input[2];
        inputs[0].Type = inputs[1].Type = 1;
        inputs[0].Data.Keyboard = new Keyboard { Key = key };
        inputs[1].Data.Keyboard = new Keyboard { Key = key, Flags = 2 };
        SendInput(2, inputs, Marshal.SizeOf(typeof(Input)));
    }
}

internal static class Program
{
    private static void Check(bool condition, string what)
    {
        if (!condition) throw new InvalidDataException("自我測試失敗：" + what);
    }

    private static void Keys(Engine engine, string keys) { foreach (char k in keys) engine.Key(k == 'b' ? '\b' : k); }

    // 引擎自我測試：用說明書同反編譯結果入面嘅字例核對
    private static void SelfTest(Q9Data data)
    {
        var engine = new Engine(data);
        var sent = new List<string>();
        engine.Output = sent.Add;
        engine.RelMode = 0;
        engine.UpdateOrder = false;      // 唔好俾調前改咗後面測試用嘅次序
        // 自動調前 (FUN_1800bc630)：筆劃揀第 13 個字，搬去第 9 格，第 9 至 12 個順次後移
        {
            var copy = new Tbl((byte[])data.Table(0, "STIMDATA").Bytes.Clone());
            var before = copy.Get(55);
            Check(copy.Promote(55, before[12], 8), "自動調前");
            var after = copy.Get(55);
            Check(after[8] == before[12] && after.Skip(9).Take(4).SequenceEqual(before.Skip(8).Take(4))
                && after.Take(8).SequenceEqual(before.Take(8)) && after.Skip(13).SequenceEqual(before.Skip(13)), "自動調前次序");
            Check(!copy.Promote(55, after[3], 8), "第一頁嘅字唔調");
        }
        // 原版筆劃上文預測唔會將頭六個常規候選移到預測字區。
        engine.Last = "十";
        Keys(engine, "940");
        Check(engine.Cand.Take(6).SequenceEqual(new[] { "人", "會", "為", "愛", "分", "全" }),
            "筆劃 十接94：分留喺第5格");
        engine.Last = "";
        engine.ResetQuiet();
        // 筆劃：志 = 橫(7) 土(1)；碼 = 橫(7) 撇(9) 橫(7)；入 = 撇(9) 點(4)
        Keys(engine, "710"); Check(engine.State == St.Select && engine.Cand.Contains("志"), "筆劃 志");
        Keys(engine, ".797"); Check(engine.Cand.Contains("碼"), "筆劃 碼");
        Keys(engine, ".940"); Check(engine.Cand.Contains("入"), "筆劃 入");
        Keys(engine, ".7"); Check(engine.Image == "Q9STIM21.png" && engine.State == St.Compose, "筆劃第二碼底圖");
        Keys(engine, "b"); Check(engine.State == St.Idle && engine.Image == (engine.HasRelated ? "Q9STIM1.png" : "Q9STIM0.png"), "後退");
        // 上屏後出關聯字，「0」選字
        Keys(engine, "710"); engine.Key((char)('1' + Array.IndexOf(engine.Cand, "志")));
        Check(sent.Last() == "志" && engine.State == St.Idle && engine.Kind == "rel" && engine.Cand.Count(c => c != null) == 9, "關聯字");
        string top = engine.Cand[6];
        // 關聯字頁揭到尾再按「0」返第一頁 (FUN_1800be2e0 參數 3)，唔會入標點
        Keys(engine, "0");
        string[] firstRel = (string[])engine.Cand.Clone();
        for (int i = 0; i < engine.Pages; i++) engine.Key('0');
        Check(engine.Kind == "rel" && engine.State == St.Select && engine.Page == 1 && engine.Cand.SequenceEqual(firstRel), "關聯字揭完返第一頁");
        Keys(engine, ".07"); Check(sent.Last() == top, "選關聯字");
        // 預測字：第一頁最多 3 個，第 4 個起放第二頁最前 (FUN_1800c0650)
        {
            var code = Enumerable.Range(0, 20).Select(i => "c" + i).ToList();
            var pred = Enumerable.Range(0, 5).Select(i => "p" + i).ToList();
            var m = typeof(Engine).GetMethod("WithPrediction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var r = (List<string>)m.Invoke(null, new object[] { code, pred });
            Check(r.Take(9).SequenceEqual(code.Take(6).Concat(pred.Take(3))) && r.Skip(9).Take(2).SequenceEqual(pred.Skip(3))
                && r.Skip(11).SequenceEqual(code.Skip(6)), "預測字第二頁");
            r = (List<string>)m.Invoke(null, new object[] { code.Take(5).ToList(), pred });
            Check(r.SequenceEqual(code.Take(5).Concat(pred.Take(3))), "預測字常規候選唔夠一頁");
        }
        // 標點：冇關聯字時「0」直接入標點頁
        engine.RelMode = 3; engine.Reset();
        Keys(engine, "0"); Check(engine.Kind == "punct" && engine.Image == "Q9_P.png", "標點頁");
        Keys(engine, "5"); Check(sent.Last() == "，", "逗號");
        Keys(engine, "09"); Check(engine.Kind == "lookup-menu", "其他開啟選檢字方法");
        Keys(engine, "4"); Check(engine.Kind == "sym" && engine.Cand[0] == "ㄅ", "原版注音符號分類");
        Keys(engine, ".00"); Check(engine.Kind == "sym" && engine.Pages > 10, "完整標點符號表");
        Keys(engine, ".0912"); Check(engine.Kind == "lookup-result" && engine.Cand.Contains("人"), "按總筆劃檢字");
        Keys(engine, ".");
        // 字形：九 270、快 892、中 810
        engine.SetMode(Engine.Glyph);
        Keys(engine, "10"); Check(engine.Cand.Take(6).SequenceEqual(new[] { "都", "華", "車", "相", "東", "林" }), "字形姓氏");
        Keys(engine, "-"); Check(engine.Image == "ShapeExtra.png", "字形檢字入口");
        Keys(engine, "4"); Check(engine.State == St.Select && engine.Kind == "code" && engine.Cand[0] == "都", "原版字形檢字分組");
        Keys(engine, ".");
        Keys(engine, "10+"); Check(engine.Kind == "sound-extra", "補音／姓氏入口");
        Keys(engine, "0"); Check(engine.Kind == "code" && engine.Cand.Contains("林"), "原版補音／姓氏分組");
        Keys(engine, ".");
        Keys(engine, "270"); Check(engine.Cand.Contains("九"), "字形 九");
        Keys(engine, ".892"); Check(engine.Cand[0] == "快", "字形 快");
        Keys(engine, ".810"); Check(engine.Cand.Contains("中"), "字形 中");
        Keys(engine, ".");
        // 普通話拼音：雙 = pqrs u uang；時 = pqrs i 確定 第二聲；安 = 無聲母 a an；啊 = a 本身；( = 四角符號
        engine.SetMode(Engine.Pinyin);
        Keys(engine, "165"); Check(engine.State == St.Select && engine.Cand.Contains("雙"), "拼音 雙");
        Keys(engine, ".12"); Check(engine.Image == "PY3I.png", "拼音 i 複韻母圖");
        Keys(engine, "0"); Check(engine.Image == "PY40.png" && engine.State == St.Compose, "拼音聲調");
        Keys(engine, "9"); Check(engine.Cand.Contains("時") && !engine.Cand.Contains("是"), "拼音 時");
        Keys(engine, ".362"); Check(engine.State == St.Compose && engine.Image == "PY40.png", "拼音 362 等候聲調");
        Keys(engine, "7"); Check(engine.State == St.Select && engine.Buf == "3624", "拼音 362 第一聲");
        // 滑鼠：第二碼點中間 = 所有聲調，唔會停低問聲調；點左上角 = 第一聲
        Keys(engine, ".36"); engine.MouseKey('2', '\0'); Check(engine.State == St.Select && engine.Buf == "3625", "拼音滑鼠中間唔問聲調");
        Keys(engine, ".36"); engine.MouseKey('2', '7'); Check(engine.State == St.Select && engine.Buf == "3624", "拼音滑鼠左上第一聲");
        Keys(engine, ".1"); engine.MouseKey('8', '9'); Check(engine.Buf == "1808", "拼音滑鼠單韻母角落");
        Keys(engine, ".808"); Check(engine.Cand.Contains("安"), "拼音 安");
        Keys(engine, ".80"); Check(engine.Kind == "pyhint" && engine.Cand[4] == "啊", "拼音 a 單字");
        Keys(engine, "5"); Check(sent.Last() == "啊", "拼音 上 啊");
        engine.Reset(); Keys(engine, "87"); Check(engine.Cand.Contains("（"), "拼音 符號");
        Keys(engine, "."); engine.Reset();
        Keys(engine, "7"); Check(engine.State == St.Idle, "拼音 7 鍵無效");
        // 注音：西 = ㄒ ㄧ 確定；找 = ㄓ ㄠ
        engine.SetMode(Engine.Zhuyin);
        Keys(engine, "370"); Check(engine.Cand.Contains("西"), "注音 西");
        Keys(engine, ".11"); Check(engine.Cand.Contains("找"), "注音 找");
        Keys(engine, ".1"); engine.MouseKey('1', '3'); Check(engine.State == St.Select && engine.Buf == "1016", "注音滑鼠右下第三聲");
        engine.SetMode(Engine.Zhuyin);
        Keys(engine, "00"); Check(engine.Kind == "lookup-menu", "注音 00 開啟檢字方法");
        Keys(engine, ".");
        Keys(engine, "01"); Check(engine.State == St.Select, "注音無聲母直接揀韻母");
        Keys(engine, "b"); Check(engine.State == St.Compose && engine.Buf == "0", "注音無聲母退回韻母鍵盤");
        Keys(engine, "7"); Check(engine.State == St.Compose && engine.Buf == "07", "注音退格後可以揀介音");
        Keys(engine, ".");
        // 粵拼：陳 = c h a n，揀第 3 個拼法 chan
        engine.SetMode(Engine.Canton);
        Keys(engine, "8486"); Check(engine.CodeText.StartsWith("[全部]"), "粵拼拼法清單");
        Keys(engine, "-"); Check(engine.CantonChoice == 1, "粵拼首個拼法按減號");
        Keys(engine, "++"); Check(engine.CodeText.EndsWith("[chan]"), "粵拼揀拼法");
        Keys(engine, "0"); Check(engine.Cand.Contains("陳") && !engine.Cand.Contains("參"), "粵拼 陳");
        engine.SetMode(Engine.Canton);
        Keys(engine, "33"); Check(engine.CodeText == "[zy]", "粵拼單一拼法 zy");
        Keys(engine, "0"); Check(engine.Cand.SequenceEqual(new[] { "渚", "拄", "箸", null, null, null, null, null, null }), "粵拼 zy 候選次序");
        Keys(engine, "."); engine.SetLang(1);
        Check(!engine.SupportsMode(Engine.Canton) && !engine.SupportsMode(Engine.Zhuyin) && engine.Mode == Engine.Stroke, "原版簡體停用粵拼同注音");
        engine.SetMode(Engine.Zhuyin); Check(engine.Mode == Engine.Stroke, "簡體唔接受注音模式切換");
        Check(data.Phrases(0).Take(3).SequenceEqual(new[] { "一直", "一定", "一起" })
            && data.Phrases(1).Contains("人体"), "原版繁簡內置詞庫");
        engine.SetLang(0);
        engine.Last = "人";
        Keys(engine, "+"); Check(engine.Kind == "phrase" && engine.Cand.Contains("人體"), "詞輸入讀取原版內置詞庫");
        engine.CyclePhraseLength();
        Check(engine.Cand.Any(s => s != null) && engine.Cand.Where(s => s != null).All(s => Engine.Elements(s).Count == 3),
            "詞輸入切換三字詞");
        Keys(engine, ".");
        engine.SetLang(1); engine.SetMode(Engine.Pinyin);
        Keys(engine, "165"); Check(engine.Cand.Contains("双"), "簡體拼音");
        Keys(engine, "."); engine.SetMode(Engine.Glyph);
        Keys(engine, "892"); Check(engine.Cand.Contains("快"), "簡體字形");
        Keys(engine, "."); engine.SetLang(0); engine.SetMode(Engine.Canton);
        // 繁→簡輸出
        engine.OutputConvert = true; engine.SetMode(Engine.Stroke);
        Keys(engine, "710"); engine.Key((char)('1' + Array.IndexOf(engine.Cand, "臺")));
        Check(sent.Last() == "台", "輸出簡體");
        engine.OutputConvert = false;
        // 簡體碼表
        engine.SetLang(1);
        Keys(engine, "710"); Check(engine.Cand.Contains("志"), "簡體筆劃");
        Keys(engine, "."); engine.SetLang(0);
        // 表情符號
        engine.SetMode(Engine.Emoji);
        Keys(engine, "11"); Check(sent.Last() == "😃" && engine.Kind == "emojicat", "表情符號");
    }

    private static void TestInput(Q9Panel panel, bool viaHook)
    {
        if (viaHook) { panel.AcceptInjected = true; panel.StartHook(); }
        using (var host = new Form { Text = "九方輸入測試", ClientSize = new Size(320, 70) })
        using (var field = new TextBox { Left = 12, Top = 15, Width = 270 })
        using (var timer = new Timer { Interval = 250 })
        {
            int phase = 0;
            string received = "";
            host.Controls.Add(field);
            host.Shown += delegate { panel.Show(); Native.Activate(host.Handle); field.Focus(); timer.Start(); };
            timer.Tick += delegate
            {
                phase++;
                if (phase == 1 && Native.GetForegroundWindow() == host.Handle)
                {
                    // 橫 土 選字 → 第 5 格「走」；經鍵盤攔截時送數字鍵盤虛擬鍵
                    foreach (char k in "7105") if (viaHook) Native.Tap((ushort)(0x60 + k - '0')); else panel.Press(k);
                    return;
                }
                if (phase < 4) return;
                timer.Stop(); received = field.Text; panel.Hide(); host.Close();
            };
            Application.Run(host);
            if (received != "走") throw new InvalidDataException("未能將選字送到測試文字框，收到「" + received + "」");
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string install = @"C:\Program Files\Q9W10B5";
        string userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Q9Rebuild");
        bool diagnostic = args.Length > 0 && args[0].StartsWith("--");
        if (diagnostic) userDir = Path.Combine(Path.GetTempPath(), "Q9Rebuild-test");
        try
        {
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Dbcs.LoadHkscs(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "big5hkscs.map"));
            var data = new Q9Data(install, userDir);
            if (args.Length > 0 && args[0] == "--self-test") { SelfTest(data); return 0; }
            var settings = new Settings(userDir);
            using (var panel = new Q9Panel(data, new Engine(data), settings))
            {
                if (args.Length >= 2 && args[0] == "--preview")
                {
                    panel.Capture_ = true;
                    // 預覽按鍵：S G P Z C 揀輸入法，b = 後退
                    if (args.Length > 2)
                        foreach (char k in args[2])
                        {
                            int mode = "SGPZC".IndexOf(k) + 1;
                            if (mode > 0) panel.SetModeForPreview(mode); else panel.Press(k == 'b' ? '\b' : k);
                        }
                    using (var bitmap = new Bitmap(panel.Width, panel.Height))
                    {
                        panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                    }
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--test-input") { TestInput(panel, false); return 0; }
                if (args.Length > 0 && args[0] == "--test-hook") { TestInput(panel, true); return 0; }
                panel.StartHook();
                Application.Run(panel);
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (diagnostic) File.WriteAllText(Path.Combine(Path.GetTempPath(), "q9-rebuild-diagnostic.txt"), ex.ToString());
            else MessageBox.Show(ex.Message, "九方 W10 重建版", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
