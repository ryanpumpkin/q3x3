// 九方 W10 重建版 - 資料層同輸入引擎
// 行為根據 q9w10b5tsf64.dll (SHA-256 02755c6a...) 嘅 REA/Ghidra 反編譯結果重寫。
// 註解入面嘅 FUN_xxxx 係原版函式位址，方便對照。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

internal static class Pe
{
    // 讀取 PE 具名資源類型 (例如 "B5_STIMDATA")，每個類型取第一個葉節點。
    internal static Dictionary<string, byte[]> NamedResources(byte[] img)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int pe = BitConverter.ToInt32(img, 0x3c);
        int sections = BitConverter.ToUInt16(img, pe + 6);
        int optSize = BitConverter.ToUInt16(img, pe + 20);
        int opt = pe + 24;
        int dirs = opt + (BitConverter.ToUInt16(img, opt) == 0x20b ? 112 : 96);
        uint rsrcRva = BitConverter.ToUInt32(img, dirs + 2 * 8);
        int secTable = opt + optSize;
        Func<uint, int> map = rva =>
        {
            for (int i = 0; i < sections; i++)
            {
                int s = secTable + i * 40;
                uint va = BitConverter.ToUInt32(img, s + 12), raw = BitConverter.ToUInt32(img, s + 20);
                uint size = Math.Max(BitConverter.ToUInt32(img, s + 8), BitConverter.ToUInt32(img, s + 16));
                if (rva >= va && rva < va + size) return (int)(rva - va + raw);
            }
            throw new InvalidDataException("RVA 不在任何區段。");
        };
        int root = map(rsrcRva);
        int named = BitConverter.ToUInt16(img, root + 12);
        for (int i = 0; i < named; i++)
        {
            uint nameRef = BitConverter.ToUInt32(img, root + 16 + i * 8);
            uint offset = BitConverter.ToUInt32(img, root + 20 + i * 8);
            int np = root + (int)(nameRef & 0x7fffffff);
            string name = Encoding.Unicode.GetString(img, np + 2, BitConverter.ToUInt16(img, np) * 2);
            // 向下行到第一個資料項
            while ((offset & 0x80000000) != 0)
                offset = BitConverter.ToUInt32(img, root + (int)(offset & 0x7fffffff) + 20);
            int entry = root + (int)offset;
            int size = BitConverter.ToInt32(img, entry + 4);
            var data = new byte[size];
            Buffer.BlockCopy(img, map(BitConverter.ToUInt32(img, entry)), data, 0, size);
            result[name] = data;
        }
        return result;
    }
}

internal static class Dbcs
{
    private static readonly Encoding Big5 = Encoding.GetEncoding(950);
    private static readonly Encoding Gbk = Encoding.GetEncoding(936);
    private static readonly Dictionary<int, string> Hkscs = new Dictionary<int, string>();
    private static Dictionary<string, int> hkscsReverse;

    // 優先用 exe 旁邊嘅 big5hkscs.map，冇就用編譯時內置嗰份。
    internal static void LoadHkscs(string path)
    {
        IEnumerable<string> lines;
        if (File.Exists(path)) lines = File.ReadAllLines(path);
        else
        {
            Stream embedded = typeof(Dbcs).Assembly.GetManifestResourceStream("big5hkscs.map");
            if (embedded == null) return;
            var list = new List<string>();
            using (var reader = new StreamReader(embedded))
                for (string l; (l = reader.ReadLine()) != null; ) list.Add(l);
            lines = list;
        }
        foreach (string line in lines)
        {
            int sp = line.IndexOf(' ');
            if (sp != 4) continue;
            var sb = new StringBuilder();
            foreach (string cp in line.Substring(5).Split('+'))
                sb.Append(char.ConvertFromUtf32(Convert.ToInt32(cp, 16)));
            Hkscs[Convert.ToInt32(line.Substring(0, 4), 16)] = sb.ToString();
        }
    }

    // 原版 MAPBU 資源：Big5 0x8840 起每行 158 格 (大端 UCS-2)，0 = 原版轉唔到 (畫唔到)
    internal static byte[] MapBu;

    // 原版畫唔到嘅 HKSCS 碼：0x81-0x87 首位元組、或者 MAPBU 係 0 (原版截圖：格有三角形但係空白)
    internal static bool Hidden(int lang, byte b0, byte b1)
    {
        if (lang != 0 || MapBu == null || b0 < 0x81) return false;
        if (b0 < 0x88) return true;
        int code = b0 << 8 | b1;
        if (!Hkscs.ContainsKey(code)) return false;
        int col = b1 < 0x7F ? b1 - 0x40 : b1 - 0xA1 + 63;
        int i = ((b0 - 0x88) * 158 + col) * 2;
        return i >= 0 && i + 1 < MapBu.Length && MapBu[i] == 0 && MapBu[i + 1] == 0;
    }

    internal static string Decode(int lang, byte b0, byte b1)
    {
        if (b0 < 0x80) return b0 < 0x20 ? "" : ((char)b0).ToString();
        if (lang == 0)
        {
            string s;
            // 原版 0x8840 以上一律查 MAPBU；私用區 (原版靠 HKSCS 字型) 先用 HKSCS 表攞真正 Unicode
            if (MapBu != null && b0 >= 0x88 && (b1 >= 0x40 && b1 <= 0x7E || b1 >= 0xA1 && b1 <= 0xFE))
            {
                int col = b1 < 0x7F ? b1 - 0x40 : b1 - 0xA1 + 63;
                int i = ((b0 - 0x88) * 158 + col) * 2;
                if (i + 1 < MapBu.Length)
                {
                    int v = MapBu[i] << 8 | MapBu[i + 1];
                    if (v != 0 && (v < 0xE000 || v > 0xF8FF)) return ((char)v).ToString();
                }
            }
            if (Hkscs.TryGetValue(b0 << 8 | b1, out s)) return s;
            s = Big5.GetString(new[] { b0, b1 });
            return s.Length == 1 && s[0] != '?' && (s[0] < 0xE000 || s[0] > 0xF8FF) ? s : "";
        }
        string g = Gbk.GetString(new[] { b0, b1 });
        return g.Length == 1 && g[0] != '?' ? g : "";
    }

    internal static byte[] Encode(int lang, string s)
    {
        if (lang == 0)
        {
            if (hkscsReverse == null)
            {
                hkscsReverse = new Dictionary<string, int>();
                foreach (var pair in Hkscs) if (!hkscsReverse.ContainsKey(pair.Value)) hkscsReverse[pair.Value] = pair.Key;
            }
            int code;
            if (hkscsReverse.TryGetValue(s, out code)) return new[] { (byte)(code >> 8), (byte)code };
        }
        byte[] b = (lang == 0 ? Big5 : Gbk).GetBytes(s);
        return b.Length == 2 && b[0] >= 0x80 ? b : null;
    }
}

// 通用碼表 (FUN_1800bca70)。word0: 低 4 位元語言、bit4-7 類型、bit8-11 = 4 表示 4 位元組元素、
// bit12-15 * 1000 + 1 = 索引數。索引 k 位於偏移 k*2，資料基址 = 索引數*2。
internal sealed class Tbl
{
    private readonly byte[] d;
    private readonly int n, elem;
    internal readonly int Type, Lang;
    internal const string Blank = "　";
    internal const string Hidden = "​";   // 原版畫唔到嘅字：有格、冇字

    internal Tbl(byte[] data)
    {
        d = data;
        int w = d[0] | d[1] << 8;
        Lang = w & 0xf;
        Type = w >> 4 & 0xf;
        elem = (w >> 8 & 0xf) == 4 ? 4 : 2;
        n = (w >> 12) * 1000 + 1;
    }

    private int Idx(int k) { return d[k * 2] | d[k * 2 + 1] << 8; }
    internal bool IsComponent(string s) { return s.Length == 1 && s[0] >= 0xE000 && s[0] < 0xE020; }
    internal byte[] Bytes { get { return d; } }

    // 輸入過的字自動調前 (FUN_1800bc630)：揀咗位置 > target 嘅字，搬去 target，中間嘅字順次後移一格。
    internal bool Promote(int code, string ch, int target)
    {
        int pos = Get(code).IndexOf(ch);
        if (pos <= target) return false;
        int from = n * 2 + Idx(code) * 2 + target * elem, at = from + (pos - target) * elem;
        var moved = new byte[elem];
        Buffer.BlockCopy(d, at, moved, 0, elem);
        Buffer.BlockCopy(d, from, d, from + elem, at - from);
        Buffer.BlockCopy(moved, 0, d, from, elem);
        return true;
    }

    internal byte[] RawRow(int code)
    {
        if (code < 1 || code >= n - 1) return new byte[0];
        int a = n * 2 + Idx(code) * 2, e = Math.Min(n * 2 + Idx(code + 1) * 2, d.Length);
        var r = new byte[Math.Max(0, e - a)];
        Buffer.BlockCopy(d, a, r, 0, r.Length);
        return r;
    }

    internal List<string> Get(int code)
    {
        var list = new List<string>();
        if (code < 1 || code >= n - 1) return list;
        int a = n * 2 + Idx(code) * 2, e = n * 2 + Idx(code + 1) * 2;
        for (int p = a; p + elem <= e && p + elem <= d.Length; p += elem)
        {
            if (elem == 4)
            {
                string s = Encoding.Unicode.GetString(d, p, 4);
                list.Add(s[1] == ' ' ? s.Substring(0, 1) : s);
                continue;
            }
            byte b0 = d[p], b1 = d[p + 1];
            if (Type == 9 && b0 == 0x20 && b1 >= 0x20 && b1 < 0x40) list.Add(((char)(0xE000 + b1 - 0x20)).ToString());
            else if ((Lang == 0 && b0 == 0xA1 && b1 == 0x40) || (Lang == 1 && b0 == 0xA1 && b1 == 0xA1)) list.Add(Blank);
            else if (Dbcs.Hidden(Lang, b0, b1)) list.Add(Hidden);
            else
            {
                string s = Dbcs.Decode(Lang, b0, b1);
                list.Add(s.Length == 0 ? Blank : s);
            }
        }
        return list;
    }
}

// 關聯字 / 上文預測表 STIMRELDATA (FUN_1800beb80)。
// [0x202] = 每筆記錄嘅筆劃碼數 N；索引為 u32 (k*4-2)，資料基址 0x20d；
// 每個首位元組一個區塊，區塊內為 [u16 長度][前字][(後字 + N 個 u16 筆劃碼)...]。
internal sealed class RelTable
{
    private readonly byte[] d;
    private readonly int n, lang;
    internal struct Rec { public string Ch; public int[] Codes; }

    internal RelTable(byte[] data) { d = data; n = d[0x202]; lang = d[0] & 0xf; }

    internal List<Rec> Get(string ch)
    {
        var list = new List<Rec>();
        byte[] key = Dbcs.Encode(lang, ch);
        if (key == null) return list;
        int k = key[0] - 0x80;
        if (k < 0 || k >= 0x7f) return list;
        int start = (k == 0 ? 0 : BitConverter.ToInt32(d, k * 4 - 2)) + 0x20d;
        int end = BitConverter.ToInt32(d, (k + 1) * 4 - 2) + 0x20d;
        int p = start, size = (n + 1) * 2;
        while (p + 4 <= end && p + 4 <= d.Length)
        {
            int len = d[p] | d[p + 1] << 8;
            if (len < 4) break;
            if (d[p + 2] == key[0] && d[p + 3] == key[1])
            {
                for (int q = p + 4; q + size <= p + len && q + size <= d.Length; q += size)
                {
                    var rec = new Rec { Ch = Dbcs.Decode(lang, d[q], d[q + 1]), Codes = new int[n] };
                    for (int i = 0; i < n; i++) rec.Codes[i] = d[q + 2 + i * 2] | d[q + 3 + i * 2] << 8;
                    if (rec.Ch.Length > 0) list.Add(rec);
                }
                break;
            }
            p += len;
        }
        return list;
    }
}

// 原版檔案來源。build.ps1 -Embed 會將本機原版檔案編譯入 exe，資源名係 "Q9/" + 相對路徑；
// 逐個檔案先搵內置，冇就讀安裝目錄。
internal sealed class Original
{
    private const string Prefix = "Q9/";
    private static readonly System.Reflection.Assembly Self = typeof(Original).Assembly;
    private readonly Dictionary<string, string> embedded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    internal readonly string Install, Cache;

    internal Original(string install, string cache)
    {
        Install = install;
        Cache = cache;
        foreach (string name in Self.GetManifestResourceNames())
            if (name.StartsWith(Prefix, StringComparison.Ordinal)) embedded[name.Substring(Prefix.Length)] = name;
    }

    internal bool HasEmbedded { get { return embedded.Count > 0; } }

    private static string Key(string relative) { return relative.Replace('\\', '/').TrimStart('/'); }

    internal bool Exists(string relative)
    {
        return embedded.ContainsKey(Key(relative)) || File.Exists(Path.Combine(Install, relative));
    }

    internal Stream Open(string relative)
    {
        string name;
        if (embedded.TryGetValue(Key(relative), out name)) return Self.GetManifestResourceStream(name);
        return File.OpenRead(Path.Combine(Install, relative));
    }

    internal byte[] ReadAllBytes(string relative)
    {
        using (Stream s = Open(relative))
        using (var copy = new MemoryStream())
        {
            s.CopyTo(copy);
            return copy.ToArray();
        }
    }

    internal string[] ReadAllLines(string relative)
    {
        var lines = new List<string>();
        using (var reader = new StreamReader(Open(relative), Encoding.UTF8, true))
            for (string line; (line = reader.ReadLine()) != null; ) lines.Add(line);
        return lines.ToArray();
    }

    // 要交俾外部程式 (hh.exe) 或者沿用檔案路徑嘅地方：內置檔案解壓到 Cache 再回傳路徑。
    internal string FilePath(string relative)
    {
        string name;
        if (!embedded.TryGetValue(Key(relative), out name)) return Path.Combine(Install, relative);
        string target = Path.Combine(Cache, relative);
        byte[] bytes = ReadAllBytes(relative);
        if (Same(target, bytes)) return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        // 每個程序用自己嘅暫存檔；同時開兩個 exe 都唔會撞
        string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(target)) File.Replace(temp, target, null); else File.Move(temp, target);
        }
        catch (IOException)
        {
            // 另一個程序啱啱寫好，或者檔案開緊 (例如 hh.exe 開住說明書)：有舊檔照用
            if (!File.Exists(target)) throw;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return target;
    }

    private static bool Same(string path, byte[] bytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != bytes.Length) return false;
        byte[] current = File.ReadAllBytes(path);
        for (int i = 0; i < bytes.Length; i++) if (current[i] != bytes[i]) return false;
        return true;
    }
}

internal sealed class Q9Data
{
    internal readonly string UserDir;
    internal readonly Original Files;
    private readonly Dictionary<string, byte[]> res;
    private readonly Dictionary<string, Tbl> tables = new Dictionary<string, Tbl>();
    private readonly Dictionary<string, RelTable> rels = new Dictionary<string, RelTable>();
    internal const string DllHash = "02755C6AC5DD4D4518734FD7FB7E069B67F4225147555997A1B79A5DA3C3DEE1";

    internal Q9Data(string install, string userDir)
    {
        UserDir = userDir;
        Files = new Original(install, Path.Combine(userDir, "original"));
        if (!Files.Exists("q9w10b5tsf64.dll"))
            throw new FileNotFoundException("搵唔到原版九方檔案：呢個 exe 冇內置原版資料，而且 " + install + " 冇安裝原版。");
        byte[] image = Files.ReadAllBytes("q9w10b5tsf64.dll");
        using (var sha = System.Security.Cryptography.SHA256.Create())
            if (BitConverter.ToString(sha.ComputeHash(image)).Replace("-", "") != DllHash)
                throw new InvalidDataException("原裝 DLL 版本與已分析版本不符。");
        res = Pe.NamedResources(image);
        byte[] mapBu;
        if (res.TryGetValue("MAPBU", out mapBu)) Dbcs.MapBu = mapBu;
        LoadCanton(image);
        LoadExtraGroups(image);
    }

    // ---------- 粵拼音節表 (.rdata，FUN_1800b9f00 / FUN_1800ba1e0) ----------
    // ctNums：已排序嘅「按鍵序列×10＋拼法序號」，位置＋1 就係 CT_PYDATA 碼；
    // ctSeq / ctOffs / ctPool：每個按鍵序列對應嘅拼法清單，第一項係按鍵本身 (= 全部拼法)。
    private int[] ctNums;
    private uint[] ctSeq;
    private ushort[] ctOffs;
    private byte[] ctPool;

    private static int Rdata(long va) { return (int)(va - 0x180117000L + 0x115600); }
    private static int U24(byte[] b, int p) { return b[p] | b[p + 1] << 8 | b[p + 2] << 16; }

    private void LoadCanton(byte[] image)
    {
        int b = Rdata(0x1801400e0L);
        int count = U24(image, b) & 0xffff;
        ctNums = new int[count + 1];
        ctNums[0] = 10;
        for (int i = 1; i <= count; i++) ctNums[i] = U24(image, b + 3 * i);
        ctSeq = new uint[570];
        ctOffs = new ushort[571];
        for (int i = 0; i < 570; i++) ctSeq[i] = BitConverter.ToUInt32(image, Rdata(0x180141070L) + i * 4);
        for (int i = 0; i < 571; i++) ctOffs[i] = BitConverter.ToUInt16(image, Rdata(0x180141960L) + i * 2);
        ctPool = new byte[ctOffs[570]];
        Buffer.BlockCopy(image, Rdata(0x180141e00L), ctPool, 0, ctPool.Length);
    }

    internal List<string> CantonSpellings(string keys)
    {
        var list = new List<string>();
        int v;
        if (keys.Length == 0 || !int.TryParse(keys, out v)) return list;
        int i = Array.IndexOf(ctSeq, (uint)v);
        if (i < 0) return list;
        string s = Encoding.ASCII.GetString(ctPool, ctOffs[i], ctOffs[i + 1] - ctOffs[i]).Replace('0', ' ');
        list.AddRange(s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        return list;
    }

    internal int CantonCode(int value)
    {
        int i = Array.BinarySearch(ctNums, 1, ctNums.Length - 1, value);
        return i > 0 ? i + 1 : 0;
    }

    private HashSet<int>[][] shapeGroups;
    private HashSet<int>[][] soundGroups;

    private static HashSet<int>[] ReadGroups(byte[] image, params long[] addresses)
    {
        return addresses.Select(address =>
        {
            var keys = new HashSet<int>();
            if (address != 0)
                for (int p = Rdata(address); p + 1 < image.Length; p += 2)
                {
                    int key = image[p] << 8 | image[p + 1];
                    if (key == 0) break;
                    keys.Add(key);
                }
            return keys;
        }).ToArray();
    }

    private void LoadExtraGroups(byte[] image)
    {
        // FUN_1800ae730: shape classes E and the default phonetic classes e (q9ExtKEY = 2).
        shapeGroups = new[]
        {
            ReadGroups(image, 0, 0x180135d10, 0x180135d70, 0x180135eb0, 0x18012ed80,
                0x1801345a0, 0x180135110, 0x18012d410, 0x18012d830, 0x18012ed10),
            ReadGroups(image, 0, 0x1801232f8, 0x1801396c0, 0x180139770, 0x180137020,
                0x180139010, 0x180139310, 0x1801362f0, 0x180136530, 0x180136fa0)
        };
        long[] initials = { 0, 0x180121980, 0x180122360, 0x180122740, 0x180123350,
            0x180123f60, 0x1801250f0, 0x180126340, 0x180125880, 0x180126340 };
        var groups = ReadGroups(image, initials);
        soundGroups = new[] { groups, (HashSet<int>[])groups.Clone() };
        soundGroups[0][0] = ReadGroups(image, 0x18013d370)[0];
        soundGroups[1][0] = ReadGroups(image, 0x18013fde0)[0];
    }

    internal bool InExtraGroup(int lang, bool shape, int group, string character)
    {
        byte[] code = Dbcs.Encode(lang, character);
        return code != null && (shape ? shapeGroups : soundGroups)[lang][group].Contains(code[0] << 8 | code[1]);
    }

    internal static string Prefix(int lang) { return lang == 0 ? "B5_" : "GB_"; }

    // FUN_1800c9de0：主碼表先讀資源，q9data 有同名 .tbl 就用檔案 (原版「自動調前」改過嘅次序就喺呢度)。
    // 重建版自己調過嘅次序寫入 UserDir，唔改原版檔案；載入次序：重建版副本 → 原版副本 → DLL 資源。
    private static readonly Dictionary<string, string> UserTableFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "STIMDATA", "q9stb" }, { "STIMDATA2", "q9stb2" }, { "Q9DATA", "q9b" }, { "Q9DATA_TS", "q9b_ts" },
        { "PYDATA", "q9x2pyb" }, { "CYDATA", "q9x2cyb" }, { "CT_PYDATA", "q9xpctb" }
    };

    private string OwnTablePath(int lang, string name)
    {
        string file;
        return UserTableFiles.TryGetValue(name, out file) ? Path.Combine(UserDir, Prefix(lang) + file + ".tbl") : null;
    }

    internal Tbl Table(int lang, string name)
    {
        string key = Prefix(lang) + name;
        Tbl t;
        if (!tables.TryGetValue(key, out t))
        {
            byte[] raw;
            if (!res.TryGetValue(key, out raw)) throw new InvalidDataException("找不到資源 " + key);
            byte[] data = (byte[])raw.Clone();
            string own = OwnTablePath(lang, name);
            if (own != null)
            {
                string original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"q9\Q9W10\q9data\" + Path.GetFileName(own));
                foreach (string path in new[] { own, original })
                {
                    if (!File.Exists(path)) continue;
                    byte[] file = File.ReadAllBytes(path);
                    // 只接受同資源一樣格式 (長度同表頭) 嘅副本
                    if (file.Length == raw.Length && file[0] == raw[0] && file[1] == raw[1]) { data = file; break; }
                }
            }
            tables[key] = t = new Tbl(data);
        }
        return t;
    }

    internal void SaveTable(int lang, string name)
    {
        Tbl t;
        string path = OwnTablePath(lang, name);
        if (path == null || !tables.TryGetValue(Prefix(lang) + name, out t)) return;
        Directory.CreateDirectory(UserDir);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, t.Bytes);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal RelTable Rel(int lang)
    {
        string key = Prefix(lang) + "STIMRELDATA";
        RelTable t;
        if (!rels.TryGetValue(key, out t)) rels[key] = t = new RelTable(res[key]);
        return t;
    }

    // 繁簡互換表：B5→GB 以 (hi-0xA1)*157 + 尾位元組序號為索引；GB→B5 以 (hi-0xA1)*94 + (lo-0xA1)。
    internal string Convert(int fromLang, string s)
    {
        byte[] b = Dbcs.Encode(fromLang, s);
        if (b == null || b[0] < 0xA1) return s;
        byte[] t = res[Prefix(fromLang) + "B5GBCNVRTDATA"];
        int i;
        if (fromLang == 0)
        {
            int lo = b[1] >= 0xA1 ? b[1] - 0xA1 + 63 : b[1] - 0x40;
            if (b[1] < 0x40 || lo < 0 || lo >= 157) return s;
            i = (b[0] - 0xA1) * 157 + lo;
        }
        else
        {
            if (b[1] < 0xA1) return s;
            i = (b[0] - 0xA1) * 94 + b[1] - 0xA1;
        }
        if (i * 2 + 1 >= t.Length) return s;
        string r = Dbcs.Decode(1 - fromLang, t[i * 2], t[i * 2 + 1]);
        return r.Length == 0 || r == "?" ? s : r;
    }

    // 用戶文字檔 (自定關聯字 / 詞語 / 自定常用)：原版放喺 %APPDATA%\q9\Q9W10\q9data，預設範本喺安裝目錄。
    internal string UserFile(int lang, string name, bool forEdit)
    {
        string file = Prefix(lang) + name;
        string mine = Path.Combine(UserDir, file);
        if (File.Exists(mine)) return mine;
        string original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"q9\Q9W10\q9data\" + file);
        string source = File.Exists(original) ? original : Files.Exists(file) ? Files.FilePath(file) : Path.Combine(Files.Install, file);
        if (!forEdit) return source;
        Directory.CreateDirectory(UserDir);
        if (File.Exists(source)) File.Copy(source, mine); else File.WriteAllText(mine, "", Encoding.Unicode);
        return mine;
    }

    // ---------- 動態關聯字表 q9b5rel.tbl (FUN_1800bd900 / FUN_1800bdc60) ----------
    // u32[首位元組*4+4] = 區段開始、u32[首位元組*4+8] = 區段結束；
    // 0x40c 起每格 4 位元組 [字 2 位元組][u16]：主字格嘅 u16 = 後面關聯字數，關聯字格嘅 u16 = 使用次數（已由大到細排好）。
    private readonly byte[][] relTables = new byte[2][];

    private byte[] RelTable(int lang)
    {
        if (relTables[lang] != null) return relTables[lang];
        string mine = Path.Combine(UserDir, Prefix(lang) + "q9b5rel.tbl");
        string original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"q9\Q9W10\q9data\" + Prefix(lang) + "q9b5rel.tbl");
        byte[] raw;
        if (File.Exists(original)) raw = File.ReadAllBytes(original);          // 用你喺原版學返嚟嘅次序
        else if (!res.TryGetValue(Prefix(lang) + "RELDATA", out raw)) raw = new byte[0x40c];
        return relTables[lang] = raw;
    }

    internal List<KeyValuePair<string, int>> DynamicRelated(int lang, string ch)
    {
        var list = new List<KeyValuePair<string, int>>();
        byte[] key = Dbcs.Encode(lang, ch), d = RelTable(lang);
        if (key == null || d.Length < 0x40c) return list;
        int start = BitConverter.ToInt32(d, key[0] * 4 + 4), end = BitConverter.ToInt32(d, key[0] * 4 + 8);
        for (int i = start; i < end && 0x410 + i * 4 <= d.Length;)
        {
            int count = BitConverter.ToUInt16(d, 0x40e + i * 4);
            if (d[0x40c + i * 4] == key[0] && d[0x40d + i * 4] == key[1])
            {
                for (int j = i + 1; j <= i + count && 0x410 + j * 4 <= d.Length; j++)
                {
                    string s = Dbcs.Decode(lang, d[0x40c + j * 4], d[0x40d + j * 4]);
                    if (s.Length > 0) list.Add(new KeyValuePair<string, int>(s, BitConverter.ToUInt16(d, 0x40e + j * 4)));
                }
                break;
            }
            i += 1 + count;
        }
        return list;
    }

    // ---------- 字資料 INFODATA：每行「字 字形碼 劃數 筆劃碼 拼音 快碼 倉頡 粵拼」 ----------
    private readonly Dictionary<string, string[]>[] info = new Dictionary<string, string[]>[2];

    internal string[] Info(int lang, string ch)
    {
        if (info[lang] == null)
        {
            var map = new Dictionary<string, string[]>();
            byte[] raw;
            if (res.TryGetValue(Prefix(lang) + "INFODATA", out raw))
            {
                int p = 0;
                while (p < raw.Length)
                {
                    int e = Array.IndexOf(raw, (byte)'\n', p);
                    if (e < 0) e = raw.Length;
                    if (e - p > 3 && raw[p] >= 0x81)
                    {
                        string c = Dbcs.Decode(lang, raw[p], raw[p + 1]);
                        string rest = Encoding.ASCII.GetString(raw, p + 2, e - p - 2).Trim();
                        if (c.Length > 0 && !map.ContainsKey(c)) map[c] = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    }
                    p = e + 1;
                }
            }
            info[lang] = map;
        }
        string[] fields;
        return info[lang].TryGetValue(ch, out fields) ? fields : null;
    }

    internal List<string> UserLines(int lang, string name)
    {
        var list = new List<string>();
        string path = UserFile(lang, name, false);
        if (!File.Exists(path)) return list;
        string[] lines;
        try { lines = File.ReadAllLines(path, new UTF8Encoding(false, true)); }   // File.ReadAllLines 會識別 UTF-16 BOM
        catch (DecoderFallbackException) { lines = File.ReadAllLines(path, Encoding.GetEncoding(lang == 0 ? 950 : 936)); }
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line[0] == '\t' || line[0] == '﻿' && line.Length > 1 && line[1] == '\t') continue;
            list.Add(line.TrimStart('﻿'));
        }
        return list;
    }

    private readonly List<string>[] builtInPhrases = new List<string>[2];

    internal IEnumerable<string> Phrases(int lang)
    {
        if (builtInPhrases[lang] == null)
        {
            var phrases = new List<string>();
            byte[] encoded;
            if (res.TryGetValue(Prefix(lang) + "Q9INPHRASEDATA", out encoded))
            {
                // FUN_18000da40: decrypt the resource, then read DBCS lines in their stored order.
                byte[] text = encoded.Select(b => (byte)(b ^ 0xe8)).ToArray();
                int length = Array.IndexOf(text, (byte)0);
                if (length < 0) length = text.Length;
                var line = new StringBuilder();
                for (int i = 0; i < length; i++)
                {
                    byte b = text[i];
                    if (b == '\r') continue;
                    if (b == '\n') { AddPhrase(phrases, line); continue; }
                    if (b < 0x80) line.Append((char)b);
                    else if (i + 1 < length) { line.Append(Dbcs.Decode(lang, b, text[i + 1])); i++; }
                }
                AddPhrase(phrases, line);
            }
            builtInPhrases[lang] = phrases;
        }
        return builtInPhrases[lang];
    }

    private static void AddPhrase(List<string> phrases, StringBuilder line)
    {
        if (line.Length > 0 && !char.IsWhiteSpace(line[0])) phrases.Add(line.ToString());
        line.Clear();
    }
}

internal enum St { Idle, Compose, Select }

internal sealed class Engine
{
    // 模式編號同原版選單一致 (FUN_18000a9f0)：1 筆劃 2 字形 3 拼音 4 注音 5 粵拼 6 數字 7 表情符號
    internal const int Stroke = 1, Glyph = 2, Pinyin = 3, Zhuyin = 4, Canton = 5, Number = 6, Emoji = 7;
    internal bool Chinese { get { return Mode >= Stroke && Mode <= Canton; } }
    internal readonly Q9Data Data;
    internal int Mode = Stroke, Lang;
    internal bool OutputConvert, HalfPunct, RadicalFirst;
    internal int RelMode = 2;                 // 0 固定 1 固定+自定 2 動態 (原版預設) 3 關閉
    internal St State = St.Idle;
    internal string Buf = "";                 // 內部碼 (筆劃模式已轉換鍵位)
    internal string Kind = "";                // 選字來源: code rel punct sym phrase fav emoji
    internal readonly string[] Cand = new string[9];   // 以鍵位排列，Cand[0] = 「1」鍵
    internal string Last = "";                // 上一個上屏嘅字
    internal string Note = "";
    internal bool NoChar;                     // 「未有字」畫面
    internal bool LookupSym;
    internal bool CantonNoCharZero;
    internal bool ExtraResult;                // 補形／補音揀完嘅清單
    internal bool ExtraNoChar;                // 補形／補音揀完冇字           // 粵拼「未有字」之後再撳 0                  // 由檢字選單入嘅符號頁 (原版冇四角三角形、兩邊掣同檢字選單一樣)
    // 原版 FUN_1800ad920 睇「上一個候選字清單」停喺第幾頁：第一頁先顯示字形檢字掣／字形 0 嘅「檢字」標點頁
    private int codePageOff;
    internal bool CodePageFirst { get { return codePageOff == 0; } }
    internal bool PunctCheck;                 // 字形冇關聯字時按 0 嘅標點頁 (0 = 檢字、兩行提示)
    // 拼音四角符號 (碼 0x00) 清單：原版冇選字三角形 (原版截圖 P13)
    internal bool SymbolCode { get { return Kind == "code" && (Mode == Pinyin || Mode == Glyph) && Buf.Length > 0 && Buf[0] == '0'; } }

    // 資料視窗：上一個字嘅資料 (格式同原版 "筆劃﹕%s" 等)
    internal string[] InfoLines()
    {
        if (Last.Length == 0) return null;
        string[] f = Data.Info(Lang, Last);
        if (f == null) return new string[0];   // 冇字資料 (符號等)：原版資料區乜都唔顯示 (原版截圖 P298)
        byte[] code = Dbcs.Encode(Lang, Last);
        Func<int, string> field = i => f != null && f.Length > i ? f[i] : "*";
        Func<int, string> first = i => field(i).Split(',')[0];
        string hex = code == null ? "*" : string.Format("{0:X2}{1:X2}", code[0], code[1]);
        return new[]
        {
            "筆劃﹕" + first(2) + " 字形﹕" + first(0),
            // 原版只顯示頭 5 個字元：CHANG1 → CHANG、SHUANG1 → SHUAN、TAO4 → TAO4 (原版截圖)
            "拼音﹕" + (field(3).Length > 5 ? field(3).Substring(0, 5) : field(3)) + " 劃數﹕" + field(1),
            "內碼﹕" + hex + " 倉頡﹕" + field(5)
        };
    }
    internal Action<string> Output;
    internal Action Beep = delegate { };

    private List<string> full = new List<string>();
    private int previousLookupMode;
    private int phraseSuffixLength = 1;
    private int off;
    private bool visual;                      // 清單以畫面次序 (上排先) 儲存
    // 重建版自己學返嘅關聯字次數 (唔寫入原版檔案)："語言:前字" → 後字 → 加咗幾多次
    private readonly Dictionary<string, Dictionary<string, int>> learnedRel = new Dictionary<string, Dictionary<string, int>>();
    private string[] mru;                     // q9sdyd：每個基本筆劃 4 個最近用字
    private readonly string statePath;

    private static readonly string[] DefaultRel = { "你這好到資就個能的", "你这好到资就个能的" };
    private static readonly string[] DefaultMru = { "去本成手最果四豐會多鳳香章麼痛言展己又系", "去本成打量里四出个多几香意度病说展己又系" };

    internal Engine(Q9Data data)
    {
        Data = data;
        statePath = Path.Combine(data.UserDir, "state.txt");
        LoadState();
        ResetQuiet();
    }

    // ---------- 狀態儲存 (重建版自己嘅檔案，不改動原版資料) ----------
    private void LoadState()
    {
        mru = new string[2];
        for (int lang = 0; lang < 2; lang++)
        {
            mru[lang] = DefaultMru[lang];
            string original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"q9\Q9W10\q9data\" + Q9Data.Prefix(lang) + "q9sdyd.bin");
            if (!File.Exists(original)) continue;
            byte[] b = File.ReadAllBytes(original);
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < b.Length && i < 40; i += 2) sb.Append(Dbcs.Decode(lang, b[i], b[i + 1]));
            if (new System.Globalization.StringInfo(sb.ToString()).LengthInTextElements == 20) mru[lang] = sb.ToString();
        }
        if (!File.Exists(statePath)) return;
        foreach (string line in File.ReadAllLines(statePath, Encoding.UTF8))
        {
            int eq = line.IndexOf('=');
            if (eq < 1) continue;
            string key = line.Substring(0, eq), value = line.Substring(eq + 1);
            if (key == "mru0" || key == "mru1") { if (Elements(value).Count == 20) mru[key[3] - '0'] = value; }
            else if (key.StartsWith("rel2:"))
            {
                var counts = new Dictionary<string, int>();
                foreach (string part in value.Split(','))
                {
                    int colon = part.LastIndexOf(':'), n;
                    if (colon > 0 && int.TryParse(part.Substring(colon + 1), out n)) counts[part.Substring(0, colon)] = n;
                }
                learnedRel[key.Substring(5)] = counts;
            }
        }
    }

    // 最近用字或者學到嘅關聯字有改動，未寫入 state.txt
    internal bool StateDirty;

    // 先寫暫存檔再換入，中途斷電或者程式死咗都唔會留低寫咗一半嘅 state.txt
    internal void SaveState()
    {
        var lines = new List<string> { "mru0=" + mru[0], "mru1=" + mru[1] };
        foreach (var pair in learnedRel)
            lines.Add("rel2:" + pair.Key + "=" + string.Join(",", pair.Value.Select(p => p.Key + ":" + p.Value)));
        Directory.CreateDirectory(Data.UserDir);
        foreach (var table in dirtyTables.ToList())
        {
            Data.SaveTable(table.Key, table.Value);
            dirtyTables.Remove(table);
        }
        string temp = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temp, lines, Encoding.UTF8);
            if (File.Exists(statePath)) File.Replace(temp, statePath, null); else File.Move(temp, statePath);
            StateDirty = false;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static List<string> Elements(string s)
    {
        var list = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    // ---------- 碼表 ----------
    private Tbl Main { get { return Data.Table(MainLang, MainName); } }
    private int MainLang { get { return Mode == Zhuyin || Mode == Canton ? 0 : Lang; } }
    private string MainName
    {
        get
        {
            if (Mode == Glyph) return "Q9DATA";
            if (Mode == Pinyin) return "PYDATA";
            if (Mode == Zhuyin) return "CYDATA";
            if (Mode == Canton) return "CT_PYDATA";
            return RadicalFirst ? "STIMDATA2" : "STIMDATA";
        }
    }

    // 輸入過的字自動調前 (原版選單 Character position adjustment，登錄 q9UpdateOrder 預設 1)
    internal bool UpdateOrder = true;
    private readonly HashSet<KeyValuePair<int, string>> dirtyTables = new HashSet<KeyValuePair<int, string>>();

    // FUN_1800add50 選字分支 → FUN_1800af290：數字碼揀字之後調前。字形「0」碼、
    // 筆劃單一基本筆劃 (碼 < 6 或 10) 唔調；筆劃搬去第一頁第 9 格，其他碼表搬去第一頁尾後一格
    private void PromoteChosen(string ch)
    {
        if (!UpdateOrder || Kind != "code" || Buf.Length == 0 || !Buf.All(char.IsDigit)) return;
        if (Mode == Glyph && Buf[0] == '0') return;
        int code = Code();
        if (Mode == Stroke && (code < 6 || code == 10)) return;
        if (!Main.Promote(code, ch, Mode == Stroke ? 8 : 9)) return;
        dirtyTables.Add(new KeyValuePair<int, string>(MainLang, MainName));
        StateDirty = true;
    }

    // 筆劃模式鍵位：7 8 9 → 橫 豎 撇 (1 2 3)，1 2 3 → 7 8 9 (FUN_1800af370)
    internal static char Swap(char k) { return k >= '1' && k <= '3' ? (char)(k + 6) : k >= '7' && k <= '9' ? (char)(k - 6) : k; }

    private int Code()
    {
        if (Buf.Length == 0) return 0;
        // 字形表：一碼 a → a0，兩碼 ab → ab0，三碼 abc；筆劃表：直接以內部碼作索引 (FUN_1800c0070)
        // 字形「0」開頭 (標點符號) 直接用個數：「09」= 碼 9 (原版截圖 G21109)
        if (Mode == Glyph) return int.Parse(Buf.Length < 3 && Buf[0] != '0' ? Buf + "0" : Buf);
        if (Mode == Pinyin) return Phonetic(Buf, PyInitial, PyFinal, 41);
        if (Mode == Zhuyin) return Phonetic(Buf, ZyInitial, ZyFinal, 30);
        if (Mode == Canton) return Data.CantonCode(int.Parse(Buf));
        return int.Parse(Buf);
    }

    // ---------- 拼音 / 注音碼 (FUN_1800b8ea0 / FUN_1800bb670) ----------
    // 內部碼 4 位 d0 d1 d2 d3：d0 聲母鍵、d1 韻母（或介音）、d2 複韻母、d3 聲調。
    // 碼 = (聲母表[d0*10+d3]-1) × 韻母數 + 韻母表[d1*10+d2]
    private static readonly Dictionary<int, int> PyInitial = Numbered(new[] { 0, 12, 14, 15, 16, 18, 22, 24, 25, 26, 28,
        32, 34, 35, 36, 38, 42, 44, 45, 46, 48, 52, 54, 55, 56, 58, 62, 64, 65, 66, 68, 82, 84, 85, 86, 88, 92, 94, 95, 96, 98 });
    private static readonly Dictionary<int, int> PyFinal = Numbered(new[] { 0, 4, 5, 6, 8, 9, 10, 20, 21, 22, 23, 24, 25, 26,
        27, 28, 29, 30, 40, 45, 46, 50, 54, 55, 58, 60, 61, 62, 63, 64, 65, 66, 68, 69, 70, 80, 84, 85, 88, 89, 90 });
    private static readonly Dictionary<int, int> ZyInitial = Numbered(Enumerable.Range(0, 10)
        .SelectMany(d0 => new[] { 2, 4, 5, 6, 8 }.Select(t => d0 * 10 + t)).ToArray());
    private static readonly Dictionary<int, int> ZyFinal = Numbered(new[] { 0, 1, 2, 3, 4, 5, 6, 70, 71, 72, 73, 74, 75, 76,
        77, 78, 79, 80, 81, 82, 83, 84, 86, 87, 88, 90, 91, 93, 96, 99 });

    private static Dictionary<int, int> Numbered(int[] keys)
    {
        var map = new Dictionary<int, int>();
        for (int i = 0; i < keys.Length; i++) map[keys[i]] = i + 1;
        return map;
    }

    private static int Phonetic(string b, Dictionary<int, int> initials, Dictionary<int, int> finals, int width)
    {
        if (b.Length != 4) return 0;
        int i, f;
        if (!initials.TryGetValue((b[0] - '0') * 10 + b[3] - '0', out i) || !finals.TryGetValue((b[1] - '0') * 10 + b[2] - '0', out f)) return 0;
        return (i - 1) * width + f;
    }

    // 普通話拼音：聲母 → 單韻母 → 複韻母 →（常用音節先揀聲調）(FUN_1800b74b0)
    private static readonly HashSet<int> NeedTone = new HashSet<int> { 120, 160, 320, 321, 328, 346, 358, 360, 362, 364, 385, 389,
        460, 461, 520, 521, 524, 528, 560, 820, 960 };

    private static char NoInitialVowel(char key)
    {
        switch (key) { case '2': return '6'; case '4': return '2'; case '6': return '4'; case '8': return '8'; case '9': return '5'; }
        return '\0';
    }

    // 複韻母鍵 → 內部碼；'\0' = 冇呢個組合
    private static char Compound(char vowel, char key)
    {
        string from = "", to = "";
        switch (vowel)
        {
            case '8': from = "07894"; to = "08945"; break;       // a: ai an ang ao
            case '5': from = "07894"; to = "08456"; break;       // e: ei en eng er
            case '4': from = "078"; to = "056"; break;           // o: ong ou
            case '6': return key == '7' ? '\0' : key;            // u
            default: return key;                                 // i
        }
        int i = from.IndexOf(key);
        return i < 0 ? '\0' : to[i];
    }

    // 冇聲母時直接揀單字嘅鍵位 (a / e / o 本身)
    private static int DirectPick(char d0, char key)
    {
        string keys = d0 == '8' ? "5612" : d0 == '9' ? "56123" : d0 == '6' ? "456" : "";
        return keys.IndexOf(key) + 1;
    }

    private static char ToneKey(char key)
    {
        int i = "791305".IndexOf(key);
        return i < 0 ? '\0' : "482655"[i];
    }

    // 筆劃查表 (FUN_1800c0650 + FUN_1800c03e0)：單一基本筆劃時「1 2 3 6」鍵換成最近用字；
    // 兩碼或以上時，上文預測略過頭 6 個常規候選，再將最多 3 個關聯字接喺第一頁尾。
    private List<string> Lookup()
    {
        Tbl t = Main;
        var list = t.Get(Code());
        if (Mode != Stroke) return list;
        if (Buf.Length == 1 && Buf[0] >= '1' && Buf[0] <= '5')
        {
            var recent = Elements(mru[Lang]);
            int b = (Buf[0] - '1') * 4;
            int[] slot = { 0, 1, 2, 5 };
            for (int i = 0; i < 4 && slot[i] < list.Count; i++) list[slot[i]] = recent[b + i];
        }
        for (int i = 0; i < list.Count; i++) if (t.IsComponent(list[i])) list[i] = Tbl.Blank;
        if (Buf.Length >= 2 && Last.Length > 0 && RelMode != 3)
        {
            int code = Code();
            // FUN_1800beb80 讀最多九個同碼關聯字，FUN_1800c02d0 排除頭六個常規候選。
            var leading = new HashSet<string>(list.Take(6));
            var predicted = Data.Rel(Lang).Get(Last)
                .Where(rec => rec.Codes.Contains(code)).Take(9)
                .Select(rec => rec.Ch).Where(ch => !leading.Contains(ch))
                .Distinct().ToList();
            if (predicted.Count > 0) list = WithPrediction(list, predicted);
        }
        return list;
    }

    // FUN_1800c0650：第一頁 = 頭 (9 - n) 個常規候選 + 最多 3 個預測字 (n = 預測字數，最多 3)；
    // 預測字有 4 個或以上而常規候選仲有下一頁，第二頁最前面先放第 4 至第 9 個預測字，
    // 之後先接返常規候選。常規候選入面同預測字重複嘅字唔會刪走 (原版照讀碼表)。
    private static List<string> WithPrediction(List<string> list, List<string> predicted)
    {
        int first = Math.Min(3, predicted.Count), keep = 9 - first;
        var result = list.Take(keep).ToList();
        result.AddRange(predicted.Take(first));
        if (predicted.Count > 3 && list.Count > keep) result.AddRange(predicted.Skip(3));
        result.AddRange(list.Skip(keep));
        return result;
    }

    // ---------- 關聯字 (FUN_1800be830 / FUN_1800be6c0) ----------
    // 動態關聯字 (FUN_1800b5e20)：清單第 i 個放「i+1」鍵（由左下開始）；
    // 第一頁 8 個關聯字，唔夠就用預設字補，第 9 格固定係預設字最後一個「的」。
    // 未有上一個字時顯示預設字，上下行對調 (你這好 喺最上)。
    private string lastRelChar = "";
    private List<string> DynamicPage(string ch)
    {
        var defaults = Elements(DefaultRel[Lang]);
        if (ch.Length == 0)
            return defaults.Skip(6).Concat(defaults.Skip(3).Take(3)).Concat(defaults.Take(3)).ToList();
        // 上咗符號 (唔係文字)：原版保留上一隻字嘅關聯字頁；之前未有就顯示預設字、唔對調上下行 (原版截圖 P298、P279)
        if (!char.IsLetter(ch, 0))
        {
            if (lastRelChar.Length == 0) return defaults;
            ch = lastRelChar;
        }
        lastRelChar = ch;
        var counts = Data.DynamicRelated(Lang, ch).Select(p => new KeyValuePair<string, int>(p.Key, p.Value)).ToList();
        Dictionary<string, int> learned;
        if (learnedRel.TryGetValue(Lang + ":" + ch, out learned))
            foreach (var pair in learned)
            {
                int i = counts.FindIndex(p => p.Key == pair.Key);
                if (i >= 0) counts[i] = new KeyValuePair<string, int>(pair.Key, counts[i].Value + pair.Value);
                else counts.Add(new KeyValuePair<string, int>(pair.Key, pair.Value));
            }
        // 次數多嘅排前，同次數保持原本次序 (原版逐格向上冒泡)
        var related = counts.Select((p, i) => new { p.Key, p.Value, i }).OrderByDescending(x => x.Value).ThenBy(x => x.i).Select(x => x.Key).ToList();
        // 呢隻字冇關聯字：原版保留之前嘅預設字頁；啱啱開或者轉輸入法之後未出過關聯字就乜都唔顯示
        if (related.Count == 0)
            return relActive ? defaults : related;       // 呢度唔對調上下行：你這好喺最底 (原版截圖)
        var page = related.Take(8).ToList();
        foreach (string filler in defaults)
        {
            if (page.Count >= 9) break;
            if (!page.Contains(filler)) page.Add(filler);
        }
        while (page.Count < 9) page.Add(Tbl.Blank);
        page[8] = defaults[8];
        page.AddRange(related.Skip(8));
        return page;
    }

    private List<string> Related(string ch)
    {
        var list = new List<string>();
        if (RelMode == 3) return list;
        if (RelMode == 2) return DynamicPage(ch);
        if (ch.Length > 0)
        {
            if (RelMode == 1)
                foreach (string line in Data.UserLines(Lang, "Q9UserRelChar.TXT"))
                {
                    var e = Elements(line);
                    if (e.Count == 2 && e[0] == ch && !list.Contains(e[1]) && list.Count < 8) list.Add(e[1]);
                }
            foreach (var rec in Data.Rel(Lang).Get(ch)) if (!list.Contains(rec.Ch)) list.Add(rec.Ch);
        }
        // 第一頁 8 個關聯字，再用預設字補足 9 格
        var page = list.Take(8).ToList();
        foreach (string filler in Elements(DefaultRel[Lang]))
        {
            if (page.Count >= 9) break;
            if (!page.Contains(filler)) page.Add(filler);
        }
        page.AddRange(list.Skip(8));
        return page;
    }

    private void ShowPage()
    {
        if (State == St.Select && Kind == "code") codePageOff = off;
        for (int i = 0; i < 9; i++) Cand[i] = null;
        int count = Math.Min(9, full.Count - off);
        for (int i = 0; i < count; i++)
        {
            int key = i;
            if (visual && count == 9) key = (2 - i / 3) * 3 + i % 3;   // 上排 → 7 8 9
            string s = full[off + i];
            Cand[key] = s == Tbl.Blank ? null : s;
        }
    }

    private void SetList(string kind, List<string> list, bool visualOrder)
    {
        Kind = kind; full = list; off = 0; visual = visualOrder; LookupSym = false; PunctCheck = false; ExtraResult = false;
        ShowPage();
    }

    internal bool HasMore { get { return off + 9 < full.Count; } }
    internal int Page { get { return off / 9 + 1; } }
    internal int Pages { get { return Math.Max(1, (full.Count + 8) / 9); } }

    internal void Reset()
    {
        if (previousLookupMode != 0) { Mode = previousLookupMode; previousLookupMode = 0; }
        Buf = ""; State = St.Idle; Note = ""; NoChar = false;
        if (Chinese)
        {
            SetList("rel", Related(Last), RelMode != 2 && RelMode != 3);
            if (full.Count > 0) relActive = true;
        }
        else if (Mode == Emoji) OpenEmoji();
        else SetList("", new List<string>(), false);
    }

    // 點擊左上角細字直接上字
    internal bool PickSmall(int index)
    {
        if (State == St.Select || Cand[index] == null || (Kind != "rel" && Kind != "code")) return false;
        Commit(Cand[index]);
        return true;
    }

    // 原版啱啱啟動或者轉輸入法時唔顯示關聯字，取消或者上字之後先出
    internal void ResetQuiet()
    {
        Reset();
        if (Kind == "rel") SetList("rel", new List<string>(), false);
        relActive = false;
    }

    private bool relActive;                   // 啟動／轉輸入法之後出過關聯字未

    // 原版簡體設定停用注音同粵拼 (FUN_1800d2590)。
    internal bool SupportsMode(int mode) { return Lang == 0 || (mode != Zhuyin && mode != Canton); }
    internal void SetMode(int mode)
    {
        if (!SupportsMode(mode)) return;
        previousLookupMode = 0;
        Mode = mode;
        ResetQuiet();
    }
    internal void SetLang(int lang)
    {
        Lang = lang;
        Last = "";
        previousLookupMode = 0;
        if (!SupportsMode(Mode)) Mode = Stroke;
        ResetQuiet();
    }

    // ---------- 按鍵 ----------
    // 鍵值：'0'-'9' '.' '+' '-' '\b'。回傳 false 表示引擎冇處理 (交返俾應用程式)。
    internal bool Key(char k)
    {
        if (k == '\b' && NoChar && ExtraNoChar) return true;   // 補形／補音冇字時，原版退格會留喺同一畫面
        Note = "";
        bool wasNoChar = NoChar;
        NoChar = false; CantonNoCharZero = false; ExtraNoChar = false;
        if (Mode == Number) return false;
        // 字形「未有字」之後原版已經清碼 (亦冇關聯字)：下一個鍵當靜止時處理，0 = 標點 (原版截圖 8250)
        if (wasNoChar && (Mode == Glyph || Mode == Pinyin || Mode == Zhuyin) && State == St.Compose && k != '' && k != '.')   // 拼音一樣 (原版截圖 6276)
        {
            Buf = ""; State = St.Idle;
            SetList("code", new List<string>(), false);
            return IdleKey(k);
        }
        // 粵拼「未有字」：字母鍵由呢個字母重新開始，0 入標點
        if (wasNoChar && Mode == Canton && State == St.Compose && k >= '0' && k <= '9')
        {
            // 0：原版停喺「未有字」，0 鍵變「選字」、三行提示 (原版截圖 C8280)
            if (k == '0') { Beep(); NoChar = true; CantonNoCharZero = true; return true; }
            if (k == '7') { Beep(); NoChar = true; return true; }
            Buf = k + "1"; AfterEdit();
            return true;
        }
        // 筆劃「未有字」：原版候選數係 0 (FUN_1800bf390 param_7)，數字鍵清碼，其他數字由呢一筆重新開始 (原版截圖 2431)；
        // 0 = 揀預設關聯字 (你這好到資就個能的)，入選字 (原版截圖 3580)
        if (wasNoChar && Mode == Stroke && State == St.Compose && k >= '0' && k <= '9')
        {
            Buf = "";
            if (k == '0')
            {
                SetList("rel", Elements(DefaultRel[Lang]), false);
                State = St.Select;
                return true;
            }
            Buf = Swap(k).ToString();
            AfterEdit();
            return true;
        }
        if (Kind == "emojicat" && (k == '\b' || k == '+' || k == '-' || k > '6')) return k != '\b';
        if (State == St.Select) return SelectKey(k);
        if (State == St.Idle) return IdleKey(k);
        return ComposeKey(k);
    }

    private bool IdleKey(char k)
    {
        bool hasRel = HasRelated;
        if (k >= '1' && k <= '9')
        {
            if ((Mode == Pinyin || Mode == Canton) && k == '7') { Beep(); return true; }   // 7 鍵冇字母
            Buf = Mode == Stroke ? Swap(k).ToString() : Mode == Canton ? k + "1" : k.ToString();
            State = St.Compose;
            AfterEdit();
            return true;
        }
        // 注音冇聲母時以 0 開始；有關聯字時「0」「.」對調 (FUN_1800af370)
        if (Mode == Zhuyin && (k == '0' && !hasRel || k == '.' && hasRel))
        {
            Buf = "0"; State = St.Compose; AfterEdit();
            return true;
        }
        // 有關聯字時「0」係選字、「.」係標點；冇關聯字時「0」直接入標點 (FUN_1800af370 0x2e/0x30 互換)
        if (k == '0' && hasRel) { State = St.Select; return true; }
        if (k == '0' || k == '.' && hasRel)
        {
            OpenPunct();
            // 字形冇關聯字、上一個清單喺第一頁：原版當字形碼「0」(FUN_1800b6380)，0 鍵係檢字 (原版截圖)
            PunctCheck = k == '0' && !hasRel && Mode == Glyph && codePageOff == 0;
            return true;
        }
        if (k == '+') { phraseSuffixLength = 1; OpenPhrases(); return true; }
        if (k == '-') { OpenTexts("fav", Data.UserLines(Lang, "yourAlwaysUse.txt"), "未有自定常用字句"); return true; }
        return false;
    }

    internal bool HasRelated { get { return Kind == "rel" && Cand.Any(c => c != null); } }

    // 取碼時碼表有冇條目 (原版 param_7 候選數，部件都計)
    internal bool HasPreview { get { return State == St.Compose && Kind == "code" && full.Count > 0; } }

    private void AfterEdit()
    {
        // 筆劃同粵拼每一步都喺左上角預覽候選字 (原版紫色細字)
        if (Mode == Stroke || Mode == Canton) { SetList("code", Lookup(), false); return; }
        if (Mode == Pinyin && Buf.Length == 2 && Buf[1] == '0' && DirectPick(Buf[0], '6') > 0)
        {
            // 冇聲母 a / e / o：單字直接排喺空位
            var single = Data.Table(Lang, "PYDATA").Get(Phonetic(Buf[0] + "005", PyInitial, PyFinal, 41));
            var hint = new List<string>(new string[9]);
            foreach (char key in "123456789")
            {
                int pick = DirectPick(Buf[0], key);
                if (pick > 0 && pick <= single.Count) hint[key - '1'] = single[pick - 1];
            }
            Kind = "pyhint"; full = hint; off = 0; visual = false;
            ShowPage();
            return;
        }
        SetList("code", new List<string>(), false);
    }

    // 完成取碼，入選字；冇字就保留原本輸入
    private bool Finish(string code)
    {
        string save = Buf;
        Buf = code;
        var list = Lookup();
        if (list.All(c => c == Tbl.Blank))
        {
            Buf = save; Beep(); AfterEdit(); NoChar = true; codePageOff = 0;
            return false;
        }
        SetList("code", list, false);
        State = St.Select; codePageOff = 0;
        return true;
    }

    private bool BackOne()
    {
        if (Buf.Length <= 1) Reset();
        else
        {
            Buf = Buf.Substring(0, Buf.Length - 1);
            AfterEdit();
        }
        return true;
    }

    private bool PinyinKey(char k)
    {
        if (k == '\b') return BackOne();
        if (k < '0' || k > '9') return true;
        char d0 = Buf[0];
        if (Buf.Length == 1)
        {
            if (k == '0')
            {
                if (NoInitialVowel(d0) == '\0') { Beep(); return true; }
                Buf += "0"; AfterEdit();
                return true;
            }
            if ("1379".IndexOf(k) >= 0) return Finish("0" + k + "00");   // 四角：標點同符號
            Buf += k; AfterEdit();
            return true;
        }
        if (Buf.Length == 2)
        {
            bool noInitial = Buf[1] == '0';
            char vowel = noInitial ? NoInitialVowel(d0) : Buf[1];
            if (noInitial)
            {
                int pick = DirectPick(d0, k);
                if (pick > 0)
                {
                    string s = Cand[k - '1'];
                    if (s == null) { Beep(); return true; }
                    Kind = "code"; Commit(s);
                    return true;
                }
                if (k == '0' && (d0 == '2' || d0 == '6' || d0 == '8') || d0 == '8' && k == '3') { Beep(); return true; }
            }
            char stored = Compound(vowel, k);
            if (stored == '\0') { Beep(); return true; }
            if (!noInitial && NeedTone.Contains(int.Parse(Buf + stored))) { Buf += stored; AfterEdit(); return true; }
            return Finish(Buf + stored + "5");
        }
        char tone = ToneKey(k);
        if (tone == '\0') { Beep(); return true; }
        return Finish(Buf + tone);
    }

    // 注音：聲母（0 = 冇）→ 韻母（直接完成）或者介音 ㄧㄨㄩ → 韻母 (FUN_1800ba8d0)
    private bool ZhuyinKey(char k)
    {
        if (k == '\b') return BackOne();
        if (k < '0' || k > '9') return true;
        if (Buf.Length == 1)
        {
            if (k == '0' && Buf == "0") { OpenLookupMenu(); return true; }
            if (k == '0') return Finish(Buf + "005");
            if (k <= '6') return Finish(Buf + "0" + k + "5");
            Buf += k; AfterEdit();
            return true;
        }
        return Finish(Buf + k + "5");
    }

    // ---------- 滑鼠點格 (FUN_1800b4de0 → FUN_1800b7260 / FUN_1800ba6b0) ----------
    // 拼音、注音取碼時，格仔四角係聲調區 (FUN_1800b7170 / FUN_1800ba5c0)：
    // 左上 7 = 第一聲、右上 9 = 第二聲、左下 1 = 第四聲、右下 3 = 第三聲，中間 = 所有聲調 ('5')。
    // corner 係 '7' '9' '1' '3' 或者 '\0' (中間)。滑鼠模式唔會停低問聲調，一下就完成取碼。
    internal bool MouseKey(char k, char corner)
    {
        if (State != St.Compose || k < '0' || k > '9' || (Mode != Pinyin && Mode != Zhuyin)) return Key(k);
        Note = ""; NoChar = false;
        char tone = corner == '\0' ? '5' : ToneKey(corner);
        if (Mode == Pinyin)
        {
            char d0 = Buf[0];
            if (Buf.Length == 1)
            {
                // FUN_1800b74b0 (滑鼠)：單韻母 i o e u a 直接當完成韻母，再補聲調；中間照鍵盤
                if (corner == '\0' || "24568".IndexOf(k) < 0) return Key(k);
                return Finish(Buf + k + "0" + tone);
            }
            if (Buf.Length != 2 || Buf[1] == '0' && "689".IndexOf(d0) >= 0) return Key(k);
            bool noInitial = Buf[1] == '0';
            char vowel = noInitial ? NoInitialVowel(d0) : Buf[1];
            if (noInitial && (k == '0' && (d0 == '2' || d0 == '6' || d0 == '8') || d0 == '8' && k == '3')) { Beep(); return true; }
            char stored = Compound(vowel, k);
            if (stored == '\0') { Beep(); return true; }
            return Finish(Buf + stored + tone);
        }
        if (Buf.Length == 1)
        {
            if (Buf == "0" && k == '0') return Key(k);
            // FUN_1800ba8d0 (滑鼠)：韻母唔即刻補「5」；介音 ㄧㄨㄩ 點角落 = 介音單獨成韻
            if (k <= '6') return Finish(Buf + (k == '0' ? "00" : "0" + k) + tone);
            if (corner == '\0') return Key(k);
            return Finish(Buf + k + "0" + tone);
        }
        return Finish(Buf + k + tone);
    }

    // 粵拼：逐個字母鍵，內部碼 = 按鍵 + 拼法序號；+／- 揀拼法，0 選字 (FUN_1800b9320)
    internal string CantonKeys { get { return Buf.Length > 0 ? Buf.Substring(0, Buf.Length - 1) : ""; } }
    internal int CantonChoice { get { return Buf.Length > 0 ? Buf[Buf.Length - 1] - '0' : 0; } }

    private bool CantonKey(char k)
    {
        string keys = CantonKeys;
        if (k == '\b')
        {
            if (keys.Length <= 1) { Reset(); return true; }
            Buf = keys.Substring(0, keys.Length - 1) + "1"; AfterEdit();
            return true;
        }
        if (k == '+' || k == '-')
        {
            int count = Data.CantonSpellings(keys).Count, choice = CantonChoice;
            // 原版 FUN_1800b9320：加號由尾返到第一項，減號喺第一項停低。
            if (k == '-' && choice == 1) return true;
            choice = k == '+' ? choice % count + 1 : choice - 1;
            Buf = keys + choice; AfterEdit();
            return true;
        }
        if (k == '0') { Finish(Buf); return true; }
        if (k == '7') { Beep(); Note = "沒有這個拼音"; return true; }
        // FUN_1800b9320：碼表冇候選 (冇預覽字) 時，字母鍵先清碼，再由呢個字母重新開始
        if (k >= '1' && k <= '9' && !HasPreview) { Buf = k + "1"; AfterEdit(); return true; }
        // 冇呢個拼法：原版入「未有字」(冇預覽、0 鍵標點、資料區空白)，下一個鍵重新開始 (原版截圖 C11)
        if (k < '1' || k > '9' || Data.CantonSpellings(keys + k).Count == 0) { Beep(); NoChar = true; return true; }
        Buf = keys + k + "1";
        if (Buf.Length == 7) Finish(Buf); else AfterEdit();
        return true;
    }

    private bool ComposeKey(char k)
    {
        if (k == '.') { Reset(); return true; }
        if (Mode == Pinyin) return PinyinKey(k);
        if (Mode == Zhuyin) return ZhuyinKey(k);
        if (Mode == Canton) return CantonKey(k);
        if (k == '+') return true;   // 取碼時「+」原版冇反應 (原版截圖 G8+)
        if (k == '-') return OpenExtra(true);
        if (k == '\b') return BackOne();
        // FUN_1800bf390：筆劃取碼時碼表冇條目，數字鍵先清碼；0 入標點頁，其他數字由呢一筆重新開始
        if (Mode == Stroke && k >= '0' && k <= '9' && !HasPreview)
        {
            Buf = "";
            if (k == '0') { OpenPunct(); return true; }
            Buf = Swap(k).ToString();
            AfterEdit();
            return true;
        }
        if (k >= '1' && k <= '9')
        {
            string previous = Buf;
            Buf += Mode == Stroke ? Swap(k) : k;
            if (Buf.Length < 3) { AfterEdit(); return true; }
            var list = Lookup();
            if (list.Count == 0) { Buf = previous; Beep(); AfterEdit(); NoChar = true; codePageOff = 0; return true; }   // 原版：「未有字」畫面 (原版截圖)
            SetList("code", list, false);
            State = St.Select; codePageOff = 0;
            return true;
        }
        if (k == '0')
        {
            var list = Lookup();
            if (list.All(c => c == Tbl.Blank)) { Beep(); Note = "沒有候選字"; return true; }
            SetList("code", list, false);
            State = St.Select; codePageOff = 0;
            return true;
        }
        return true;
    }

    private bool SelectKey(char k)
    {
        if (Kind == "lookup-menu" && k >= '1' && k <= '9') return ChooseLookup(k - '0');
        if (Kind == "stroke-count" && k >= '1' && k <= '9') return OpenLookupRow("STDATA", off + k - '0');
        if (Kind == "radical-stroke" && k >= '1' && k <= '9') return OpenRadicals(k - '0');
        if (Kind == "radical-count" && k >= '1' && k <= '9')
        {
            int i = off + k - '1';
            if (i >= radicalIndex.Count) { Beep(); return true; }
            return OpenLookupRow("RADDATA", 10 + radicalIndex[i]);
        }
        if ((Kind == "shape-extra" || Kind == "sound-extra") && k >= '0' && k <= '9')
            return FilterExtra(Kind == "shape-extra", k - '0');
        if (Kind == "punct" && k == '0') { if (PunctCheck) OpenLookupMenu(); else OpenSymbols(); return true; }
        if (Kind == "code" && (Mode == Glyph || Mode == Stroke) && (k == '+' || (k == '-' && off == 0)))
            return OpenExtra(k == '-');
        if (k >= '1' && k <= '9')
        {
            string s = Cand[k - '1'];
            if (s == null || s == Tbl.Hidden) { Beep(); return true; }
            if (Kind == "punct" && k == '9' && PunctCheck)
            {
                // 「檢字」款 = 字形碼「0」：9 鍵 (其他) 入字形碼「09」符號清單 (原版截圖 G21109)
                Buf = "09";
                SetList("code", Main.Get(Code()), false);
                State = St.Select; codePageOff = 0;
                return true;
            }
            if (Kind == "punct" && k == '9') { OpenLookupMenu(); return true; }
            if (Kind == "emojicat") { SetList("emoji", Data.Table(Lang, "EMOJIDATA").Get(k - '0'), false); return true; }
            Commit(s);
            return true;
        }
        if (k == '0' || k == '+')
        {
            if (HasMore) off += 9;
            // FUN_1800be2e0 / FUN_1800be830 (參數 3)：關聯字揭到尾，頁數設返 1，由第一頁再開始
            else if (Kind == "rel") off = 0;
            // FUN_1800ad6c0 (參數 3)：候選字揭到尾，返回值帶 2 (嗶一聲) 再由第一頁開始
            else if (Kind == "code") { Beep(); off = 0; }
            else if (off == 0) { Beep(); return true; }
            else off = 0;
            ShowPage();
            return true;
        }
        if (k == '-')
        {
            if (off == 0)
            {
                // 關聯字第一頁：FUN_1800be2e0 參數 2 直接返回，冇聲冇反應
                if (Kind == "rel") return true;
                Beep();
                // FUN_1800ad6c0 (參數 2)：非筆劃候選字第一頁再按上頁，揭去最後一頁；筆劃 (FUN_1800c0650) 停喺原位
                if (Kind == "code" && Mode != Stroke && Pages > 1) { off = (Pages - 1) * 9; ShowPage(); }
                return true;
            }
            off -= 9; ShowPage();
            return true;
        }
        if (k == '\b' && Kind == "code" && Buf.Length > 0)
        {
            State = St.Compose;
            if (Mode == Pinyin)
            {
                if (Buf[0] == '0' && Buf[1] != '0') { Reset(); return true; }       // 四角符號
                Buf = Buf.Substring(0, 2);
            }
            // FUN_1800ba8d0：有介音保留兩碼，直接揀韻母就只保留聲母鍵（包括 0）。
            else if (Mode == Zhuyin) Buf = Buf.Substring(0, Buf[1] == '0' ? 1 : 2);
            else if (Mode != Canton && Buf.Length == 3) Buf = Buf.Substring(0, 2);
            AfterEdit();
            return true;
        }
        if (k == '.' || k == '\b')
        {
            if (Kind == "emoji") { OpenEmoji(); return true; }
            Reset();
            if (Mode == Emoji) OpenEmoji();
            return true;
        }
        return true;
    }

    internal void OpenEmoji()
    {
        SetList("emojicat", new List<string> { "😃", "🎈", "🍕", "🚄", "⛅", "❉" }, false);
        State = St.Select;
    }

    private void OpenPunct()
    {
        // 標點頁：1-8 鍵直接上屏，9 鍵開其他符號 (PUNDATA 碼 1-9)
        Tbl t = Data.Table(Lang, "PUNDATA");
        var list = new List<string>();
        for (int i = 1; i <= 8; i++)
        {
            var one = t.Get(i);
            list.Add(one.Count > 0 ? one[0] : Tbl.Blank);
        }
        list.Add("其他");
        SetList("punct", list, false);
        State = St.Select;
    }

    private void OpenSymbols()
    {
        SetList("sym", Data.Table(Lang, "PUNDATA").Get(9), false);
        State = St.Select;
    }

    private void OpenLookupMenu()
    {
        Buf = "";
        var labels = Lang == 0
            ? new[] { "劃", "音", "部", "ㄅ", "Ａ", "﹡", "├", "１", "()" }
            : new[] { "划", "音", "部", "あ", "α", "＊", "┅", "①", "()" };
        OpenTexts("lookup-menu", labels.ToList(), "未有檢字方法");
    }

    private bool ChooseLookup(int key)
    {
        if (key == 1)
        {
            var strokes = Enumerable.Range(1, 26).Select(n => n.ToString()).Concat(new[] { ">27" }).ToList();
            OpenTexts("stroke-count", strokes, "未有筆劃數目");
        }
        // 「音」：原版喺檢字選單揀咗只係返去靜止頁，唔轉拼音 (原版截圖 G1..92、G1..928)
        else if (key == 2) Reset();
        else if (key == 3)
        {
            // 九方部首筆劃檢字：先揀部首第一筆 (畫面同字形靜止一樣，FUN_1800b0970)
            SetList("radical-stroke", new List<string>(), false);
            State = St.Select;
        }
        else return OpenLookupRow("SBDATA", key);
        return true;
    }

    // RADDATA 第 1-9 行：該筆開頭嘅部首「偽碼」(A4A1 起)，索引 = (首位元組 - 0xA4) * 0x5E + 次位元組 - 0xA1；
    // 部首字本身係 FUN_1800b0970 入面嘅常數陣列；揀咗第 i 個部首 = RADDATA 第 10 + i 行
    internal const string RadicalGlyphs = "扌支寸十土士耒赤走甘皮老斗木艹麥黃鼓青革韋車弋犭戈女尢廾力又大爻屮山己凵匚冂冖几网臣長禸肉用彐月殳鬥鬯黽風馬髟門文方宀广心丶亠冫衤礻羊疒穴立火玄鹿麻音首高言辛齊龍阝子巛卩厶阝乙刀癶匕疋矛糸辶爿幺廴弓羽飛田皿目日曰毋口囗邑尸骨黑鼎貝足里臼艮見鼠厂工二一牙玉瓦歹干旡至而耳非石雨豕辰酉覀豆鬲面頁韭巾亅卜丨丿小米止比水虍虫黹聿隶鹵齒戶谷八父人儿入金食彡彳攵夂夊夕勹片牛瓜氏气爪斤欠毛自舌舛禾竹缶生白矢身釆阜隹行角豸舟色血黍鼻龜龠鬼魚鳥香";
    private List<int> radicalIndex = new List<int>();

    internal int RadicalIndexAt(int key)
    {
        int i = off + key - 1;
        return Kind == "radical-count" && i >= 0 && i < radicalIndex.Count ? radicalIndex[i] : -1;
    }

    private bool OpenRadicals(int stroke)
    {
        var bytes = Data.Table(Lang, "RADDATA").RawRow(stroke);
        radicalIndex = new List<int>();
        for (int p = 0; p + 1 < bytes.Length; p += 2)
        {
            int idx = (bytes[p] - 0xA4) * 0x5E + bytes[p + 1] - 0xA1;
            if (idx >= 0 && idx < RadicalGlyphs.Length) radicalIndex.Add(idx);
        }
        if (radicalIndex.Count == 0) { Beep(); return true; }
        var shown = radicalIndex.Select(i => RadicalGlyphs[i].ToString()).ToList();
        var keep = radicalIndex;
        SetList("radical-count", shown, false);
        radicalIndex = keep;
        State = St.Select;
        return true;
    }

    private bool OpenLookupRow(string table, int row)
    {
        var list = Data.Table(Lang, table).Get(row);
        OpenTexts(table == "SBDATA" ? "sym" : "lookup-result", list, "沒有中文字供選用");
        LookupSym = table == "SBDATA" && State == St.Select;
        return true;
    }

    private bool OpenExtra(bool shape)
    {
        if (Buf.Length == 0 || Main.Get(Code()).Count == 0) { Beep(); return true; }
        if (shape) SetList("shape-extra", Enumerable.Range(1, 9).Select(i => i.ToString()).ToList(), false);
        else
        {
            // 補音：拼音聲母圖 (PY2)，預覽 0 鍵 (選字) 嗰組字嘅第一頁 (原版截圖 G892+)
            var preview = Main.Get(Code()).Where(ch => ch != Tbl.Blank && Data.InExtraGroup(Lang, false, 0, ch)).ToList();
            SetList("sound-extra", preview, false);
        }
        State = St.Select;
        return true;
    }

    private bool FilterExtra(bool shape, int key)
    {
        var filtered = Main.Get(Code()).Where(ch => ch != Tbl.Blank && Data.InExtraGroup(Lang, shape, key, ch)).ToList();
        if (filtered.Count == 0)
        {
            // 冇字：原版入「未有字」畫面，兩邊用預設掣 (原版截圖 G892-0)
            Beep(); Buf = ""; State = St.Compose; SetList("code", new List<string>(), false);
            NoChar = true; ExtraNoChar = true;
            return true;
        }
        OpenTexts("code", filtered, "沒有中文字供選用");
        ExtraResult = State == St.Select && Kind == "code";
        return true;
    }

    private void OpenPhrases()
    {
        // FUN_18000dd40 / FUN_18000eaa0：自定詞語先行，詞長 4 代表四字或以上。
        var list = Data.UserLines(Lang, "Q9PHRASE.TXT").Concat(Data.Phrases(Lang))
            .Where(phrase =>
            {
                if (Last.Length == 0) return true;
                if (!phrase.StartsWith(Last, StringComparison.Ordinal)) return false;
                int length = Elements(phrase.Substring(Last.Length)).Count;
                return Math.Min(4, length) == phraseSuffixLength;
            })
            .ToList();
        SetList("phrase", list, false);
        State = St.Select;
        if (list.Count == 0) Note = "未有詞語";
    }

    internal void CyclePhraseLength()
    {
        phraseSuffixLength = phraseSuffixLength % 4 + 1;
        OpenPhrases();
    }

    private void OpenTexts(string kind, List<string> list, string empty)
    {
        if (list.Count == 0) { Note = empty; Beep(); return; }
        SetList(kind, list, false);
        State = St.Select;
    }

    internal bool TextList { get { return Kind == "phrase" || Kind == "fav"; } }

    private static readonly Dictionary<string, string> Half = new Dictionary<string, string>
    {
        { "，", "," }, { "。", "." }, { "！", "!" }, { "：", ":" }, { "；", ";" }, { "？", "?" }, { "、", "," },
        { "（", "(" }, { "）", ")" }, { "—", "-" }, { "…", "..." }
    };

    // 上屏 (FUN_1800add50 選字分支)
    internal void Commit(string s)
    {
        string text = s;
        if (Kind == "phrase" && Last.Length > 0 && s.StartsWith(Last)) text = s.Substring(Last.Length);  // 首字已經上屏
        bool single = Elements(s).Count == 1 && (Kind == "code" || Kind == "rel" || Kind == "lookup-result");
        if (HalfPunct && (Kind == "punct" || Kind == "sym"))
        {
            string half;
            if (Half.TryGetValue(text, out half)) text = half;
        }
        if (OutputConvert)
        {
            var sb = new StringBuilder();
            foreach (string e in Elements(text)) sb.Append(Data.Convert(Lang, e));
            text = sb.ToString();
        }
        if (Output != null && text.Length > 0) Output(text);
        if (single)
        {
            PromoteChosen(s);
            if (Mode == Stroke && Kind == "code" && Buf.Length >= 2 && Buf[0] >= '1' && Buf[0] <= '5')
            {
                // 最近用字輪替 (q9sdyd)
                var recent = Elements(mru[Lang]);
                int b = (Buf[0] - '1') * 4;
                if (recent.IndexOf(s, b, 4) < 0)
                {
                    recent.RemoveAt(b + 3);
                    recent.Insert(b, s);
                    mru[Lang] = string.Concat(recent);
                    StateDirty = true;
                }
            }
            if (RelMode == 2 && Last.Length > 0)
            {
                // 用過嘅關聯組合次數 +1 (FUN_1800bdc60)
                Dictionary<string, int> learned;
                if (!learnedRel.TryGetValue(Lang + ":" + Last, out learned)) learnedRel[Lang + ":" + Last] = learned = new Dictionary<string, int>();
                int n;
                learned.TryGetValue(s, out n);
                learned[s] = n + 1;
                StateDirty = true;
            }
            Last = s;
        }
        else if (Kind == "phrase" || Kind == "fav")
        {
            var e = Elements(s);
            Last = e.Count > 0 ? e[e.Count - 1] : "";
        }
        // 標點、符號：原版都當係上一個字 (資料區空白、關聯字用預設字唔對調) (原版截圖 G21101)
        else if ((Kind == "punct" || Kind == "sym") && Elements(s).Count == 1) Last = s;
        bool emoji = Kind == "emoji";
        Reset();
        if (emoji || Mode == Emoji) OpenEmoji();
    }

    // 畫面用：目前應顯示嘅九宮格底圖名稱
    internal string Image
    {
        get
        {
            if (Mode == Number) return null;      // 數字模式由面板直接畫 1-9
            if (State == St.Select)
            {
                if (Kind == "shape-extra") return Lang == 0 ? "ShapeExtra.png" : "ShapeExtra_GB.png";
                return Kind == "punct" ? "Q9_P.png" : Kind == "emojicat" ? "Q9_emoji.png" : Kind == "radical-stroke" ? "Q9_0.png" : Kind == "sound-extra" ? "PYREL.png" : null;
            }
            if (NoChar)
                return Mode == Glyph ? "Q9_N.png" : Mode == Stroke ? "q9stimNone.png" : Mode == Zhuyin ? "CYNONE.png" : "PYNONE.png";
            if (Mode == Glyph) return Buf.Length == 1 ? "Q9_" + Buf + ".png" : Buf.Length == 2 ? "Q9_D.png" : HasRelated ? "Q9_REL.png" : "Q9_0.png";
            if (Mode == Pinyin)
            {
                if (Buf.Length == 0) return HasRelated ? "PYREL.png" : "PY0.png";
                if (Buf.Length == 1) return "PY2.png";
                if (Buf.Length == 3) return "PY40.png";
                char vowel = Buf[1] == '0' ? NoInitialVowel(Buf[0]) : Buf[1];
                int i = "85246".IndexOf(vowel);
                return i < 0 ? "PY2.png" : new[] { "Py3a.png", "Py3e.png", "PY3I.png", "Py3o.png", "Py3u.png" }[i];
            }
            if (Mode == Zhuyin)
            {
                if (Buf.Length == 0) return HasRelated ? "CYREL.png" : "CYA0.png";
                return Buf.Length == 1 || Buf[1] == '0' ? "CYB.png" : "CYC1.png";
            }
            // FUN_1800b9790 / FUN_1800bf820：取碼時冇預覽字就用「未有字」圖 (索引 0x2e / 0x55)
            bool preview = HasPreview;
            if (Mode == Canton) return Buf.Length > 0 ? (preview ? "PYREL.png" : "PYNONE.png") : HasRelated ? "PYREL.png" : "PY0.png";
            if (Buf.Length == 0) return HasRelated ? "Q9STIM1.png" : "Q9STIM0.png";
            if (!preview) return "q9stimNone.png";
            if (Buf.Length == 1 && Buf[0] >= '1' && Buf[0] <= '5')
                return Buf[0] == '4' ? (Lang == 0 ? "Q9stim24b5.png" : "Q9stim24gb.png") : "Q9STIM2" + Buf + ".png";
            return "Q9STIM1.png";
        }
    }

    private static readonly string[] PyInitialNames = { "", "pqrs", "tuü", "wxyz", "ghi", "jkl", "mno", "", "abc", "def" };

    private string PinyinText()
    {
        if (Buf[0] == '0' && Buf.Length == 4 && Buf[1] != '0') return "符號";
        var sb = new StringBuilder();
        bool noInitial = Buf.Length >= 2 && Buf[1] == '0';
        if (!noInitial) sb.Append(PyInitialNames[Buf[0] - '0']);
        if (Buf.Length == 1) return sb.ToString();
        char vowel = noInitial ? NoInitialVowel(Buf[0]) : Buf[1];
        if (Buf.Length == 2) return sb.Append(noInitial ? "（無聲母）" : "").Append(VowelName(vowel)).ToString();
        sb.Append(noInitial ? "" : "+").Append(FinalName(vowel, Buf[2]));
        if (Buf.Length == 4) sb.Append(" ").Append(ToneName(Buf[3]));
        return sb.ToString();
    }

    private static string VowelName(char v)
    {
        switch (v) { case '8': return "a"; case '4': return "o"; case '5': return "e"; case '6': return "u/ü"; case '2': return "i"; }
        return "";
    }

    private static string FinalName(char vowel, char stored)
    {
        string[] names;
        switch (vowel)
        {
            case '8': names = new[] { "a", "", "", "", "ang", "ao", "", "", "ai", "an" }; break;
            case '5': names = new[] { "e", "", "", "", "en", "eng", "er", "", "ei", "" }; break;
            case '4': names = new[] { "o", "", "", "", "", "ong", "ou", "", "", "" }; break;
            case '6': names = new[] { "u/ü", "ui", "un/ün", "uo", "uan/üan", "uang", "üe", "", "ua", "uai" }; break;
            default: names = new[] { "i", "ing", "iong", "iu", "iao", "ie", "in", "ia", "ian", "iang" }; break;
        }
        return names[stored - '0'];
    }

    private static string ToneName(char t)
    {
        switch (t) { case '4': return "第一聲"; case '8': return "第二聲"; case '6': return "第三聲"; case '2': return "第四聲"; }
        return "";
    }

    private static readonly string[] ZyInitialNames = { "", "ㄐㄓㄗ", "ㄑㄔㄘ", "ㄒㄕㄙ", "ㄋㄌ", "ㄍㄎ", "ㄏㄖ", "ㄅㄆ", "ㄇㄈ", "ㄉㄊ" };
    private static readonly string[] ZyFinalNames = { "", "ㄠㄡ", "ㄢㄤ", "ㄣㄥㄦ", "ㄚㄛ", "ㄜㄝ", "ㄞㄟ", "ㄧ", "ㄨ", "ㄩ" };
    private static readonly string[] ZyAfterMedial = { "", "ㄣ", "ㄤ", "ㄥㄦ", "ㄞㄟ", "ㄠㄡ", "ㄢ", "ㄚ", "ㄛ", "ㄜㄝ" };

    private string ZhuyinText()
    {
        var sb = new StringBuilder(Buf[0] == '0' ? "（無聲母）" : ZyInitialNames[Buf[0] - '0']);
        if (Buf.Length >= 2) sb.Append(ZyFinalNames[Buf[1] - '0']);
        if (Buf.Length >= 3) sb.Append(Buf[1] == '0' ? ZyFinalNames[Buf[2] - '0'] : ZyAfterMedial[Buf[2] - '0']);
        return sb.ToString();
    }

    private static readonly string StrokeNames = " 一丨丿丶乙口艹竹宀";
    internal string CodeText
    {
        get
        {
            if (Buf.Length == 0) return "";
            if (Mode == Glyph) return Buf;
            if (Mode == Pinyin) return PinyinText();
            if (Mode == Zhuyin) return ZhuyinText();
            if (Mode == Canton)
            {
                var spell = Data.CantonSpellings(CantonKeys);
                var parts = new List<string>();
                for (int i = 0; i < spell.Count; i++)
                {
                    string s = i == 0 && spell[i] == CantonKeys ? "全部" : spell[i];
                    parts.Add(i + 1 == CantonChoice ? "[" + s + "]" : s);
                }
                return string.Join(" ", parts);
            }
            var sb = new StringBuilder();
            for (int i = 0; i < Buf.Length; i++)
                sb.Append(i == 1 && Buf[0] <= '5' && Buf[1] >= '6' ? '部' : StrokeNames[Buf[i] - '0']);
            return sb.ToString();
        }
    }
}
