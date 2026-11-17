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

    internal static void LoadHkscs(string path)
    {
        if (!File.Exists(path)) return;
        foreach (string line in File.ReadAllLines(path))
        {
            int sp = line.IndexOf(' ');
            if (sp != 4) continue;
            var sb = new StringBuilder();
            foreach (string cp in line.Substring(5).Split('+'))
                sb.Append(char.ConvertFromUtf32(Convert.ToInt32(cp, 16)));
            Hkscs[Convert.ToInt32(line.Substring(0, 4), 16)] = sb.ToString();
        }
    }

    internal static string Decode(int lang, byte b0, byte b1)
    {
        if (b0 < 0x80) return b0 < 0x20 ? "" : ((char)b0).ToString();
        if (lang == 0)
        {
            string s;
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

internal sealed class Q9Data
{
    internal readonly string Install, UserDir;
    private readonly Dictionary<string, byte[]> res;
    private readonly Dictionary<string, Tbl> tables = new Dictionary<string, Tbl>();
    private readonly Dictionary<string, RelTable> rels = new Dictionary<string, RelTable>();
    internal const string DllHash = "02755C6AC5DD4D4518734FD7FB7E069B67F4225147555997A1B79A5DA3C3DEE1";

    internal Q9Data(string install, string userDir)
    {
        Install = install;
        UserDir = userDir;
        byte[] image = File.ReadAllBytes(Path.Combine(install, "q9w10b5tsf64.dll"));
        using (var sha = System.Security.Cryptography.SHA256.Create())
            if (BitConverter.ToString(sha.ComputeHash(image)).Replace("-", "") != DllHash)
                throw new InvalidDataException("原裝 DLL 版本與已分析版本不符。");
        res = Pe.NamedResources(image);
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

    internal Tbl Table(int lang, string name)
    {
        string key = Prefix(lang) + name;
        Tbl t;
        if (!tables.TryGetValue(key, out t))
        {
            byte[] raw;
            if (!res.TryGetValue(key, out raw)) throw new InvalidDataException("找不到資源 " + key);
            tables[key] = t = new Tbl(raw);
        }
        return t;
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
        string template = Path.Combine(Install, file);
        string source = File.Exists(original) ? original : template;
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

    // 資料視窗：上一個字嘅資料 (格式同原版 "筆劃﹕%s" 等)
    internal string[] InfoLines()
    {
        if (Last.Length == 0) return null;
        string[] f = Data.Info(Lang, Last);
        byte[] code = Dbcs.Encode(Lang, Last);
        Func<int, string> field = i => f != null && f.Length > i ? f[i] : "*";
        Func<int, string> first = i => field(i).Split(',')[0];
        string hex = code == null ? "*" : string.Format("{0:X2}{1:X2}", code[0], code[1]);
        return new[]
        {
            "筆劃﹕" + first(2) + " 字形﹕" + first(0),
            "拼音﹕" + field(3) + " 劃數﹕" + field(1),
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

    internal void SaveState()
    {
        var lines = new List<string> { "mru0=" + mru[0], "mru1=" + mru[1] };
        foreach (var pair in learnedRel)
            lines.Add("rel2:" + pair.Key + "=" + string.Join(",", pair.Value.Select(p => p.Key + ":" + p.Value)));
        Directory.CreateDirectory(Data.UserDir);
        File.WriteAllLines(statePath, lines, Encoding.UTF8);
    }

    internal static List<string> Elements(string s)
    {
        var list = new List<string>();
        var e = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    // ---------- 碼表 ----------
    private Tbl Main
    {
        get
        {
            if (Mode == Glyph) return Data.Table(Lang, "Q9DATA");
            if (Mode == Pinyin) return Data.Table(Lang, "PYDATA");
            if (Mode == Zhuyin) return Data.Table(0, "CYDATA");
            if (Mode == Canton) return Data.Table(0, "CT_PYDATA");
            return Data.Table(Lang, RadicalFirst ? "STIMDATA2" : "STIMDATA");
        }
    }

    // 筆劃模式鍵位：7 8 9 → 橫 豎 撇 (1 2 3)，1 2 3 → 7 8 9 (FUN_1800af370)
    internal static char Swap(char k) { return k >= '1' && k <= '3' ? (char)(k + 6) : k >= '7' && k <= '9' ? (char)(k - 6) : k; }

    private int Code()
    {
        if (Buf.Length == 0) return 0;
        // 字形表：一碼 a → a0，兩碼 ab → ab0，三碼 abc；筆劃表：直接以內部碼作索引 (FUN_1800c0070)
        if (Mode == Glyph) return int.Parse(Buf.Length < 3 ? Buf + "0" : Buf);
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
                .Distinct().Take(3).ToList();
            if (predicted.Count > 0)
            {
                foreach (string p in predicted) list.Remove(p);
                list.InsertRange(Math.Min(list.Count, 9 - predicted.Count), predicted);
            }
        }
        return list;
    }

    // ---------- 關聯字 (FUN_1800be830 / FUN_1800be6c0) ----------
    // 動態關聯字 (FUN_1800b5e20)：清單第 i 個放「i+1」鍵（由左下開始）；
    // 第一頁 8 個關聯字，唔夠就用預設字補，第 9 格固定係預設字最後一個「的」。
    // 未有上一個字時顯示預設字，上下行對調 (你這好 喺最上)。
    private List<string> DynamicPage(string ch)
    {
        var defaults = Elements(DefaultRel[Lang]);
        if (ch.Length == 0)
            return defaults.Skip(6).Concat(defaults.Skip(3).Take(3)).Concat(defaults.Take(3)).ToList();
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
        Kind = kind; full = list; off = 0; visual = visualOrder;
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
        Note = "";
        NoChar = false;
        if (Mode == Number) return false;
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
        if (k == '0' || k == '.' && hasRel) { OpenPunct(); return true; }
        if (k == '+') { phraseSuffixLength = 1; OpenPhrases(); return true; }
        if (k == '-') { OpenTexts("fav", Data.UserLines(Lang, "yourAlwaysUse.txt"), "未有自定常用字句"); return true; }
        return false;
    }

    internal bool HasRelated { get { return Kind == "rel" && Cand.Any(c => c != null); } }

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
            Buf = save; Beep(); AfterEdit(); NoChar = true;
            return false;
        }
        SetList("code", list, false);
        State = St.Select;
        return true;
    }

    private bool BackOne()
    {
        Buf = Buf.Substring(0, Buf.Length - 1);
        if (Buf.Length == 0) Reset(); else AfterEdit();
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
        if (k == '7' || k < '1' || k > '9' || Data.CantonSpellings(keys + k).Count == 0) { Beep(); Note = "沒有這個拼音"; return true; }
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
        if (k == '-' || k == '+') return OpenExtra(k == '-');
        if (k == '\b')
        {
            Buf = Buf.Substring(0, Buf.Length - 1);
            if (Buf.Length == 0) Reset(); else AfterEdit();
            return true;
        }
        if (k >= '1' && k <= '9')
        {
            string previous = Buf;
            Buf += Mode == Stroke ? Swap(k) : k;
            if (Buf.Length < 3) { AfterEdit(); return true; }
            var list = Lookup();
            if (list.Count == 0) { Buf = previous; Beep(); AfterEdit(); Note = "沒有這個碼"; return true; }
            SetList("code", list, false);
            State = St.Select;
            return true;
        }
        if (k == '0')
        {
            var list = Lookup();
            if (list.All(c => c == Tbl.Blank)) { Beep(); Note = "沒有候選字"; return true; }
            SetList("code", list, false);
            State = St.Select;
            return true;
        }
        return true;
    }

    private bool SelectKey(char k)
    {
        if (Kind == "lookup-menu" && k >= '1' && k <= '9') return ChooseLookup(k - '0');
        if ((Kind == "stroke-count" || Kind == "radical-count") && k >= '1' && k <= '9')
        {
            int row = off + k - '0';
            return OpenLookupRow(Kind == "stroke-count" ? "STDATA" : "RADDATA", row);
        }
        if ((Kind == "shape-extra" || Kind == "sound-extra") && k >= '0' && k <= '9')
            return FilterExtra(Kind == "shape-extra", k - '0');
        if (Kind == "punct" && k == '0') { OpenSymbols(); return true; }
        if (Kind == "code" && (Mode == Glyph || Mode == Stroke) && (k == '+' || (k == '-' && off == 0)))
            return OpenExtra(k == '-');
        if (k >= '1' && k <= '9')
        {
            string s = Cand[k - '1'];
            if (s == null) { Beep(); return true; }
            if (Kind == "punct" && k == '9') { OpenLookupMenu(); return true; }
            if (Kind == "emojicat") { SetList("emoji", Data.Table(Lang, "EMOJIDATA").Get(k - '0'), false); return true; }
            Commit(s);
            return true;
        }
        if (k == '0' || k == '+')
        {
            if (HasMore) off += 9;
            else if (Kind == "rel") { OpenPunct(); return true; }     // 關聯字揭完再按「0」入標點
            else if (off == 0) { Beep(); return true; }
            else off = 0;
            ShowPage();
            return true;
        }
        if (k == '-')
        {
            if (off == 0) { Beep(); return true; }
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
        else if (key == 2)
        {
            int original = Mode;
            SetMode(Pinyin);
            previousLookupMode = original;
        }
        else if (key == 3)
        {
            var table = Data.Table(Lang, "RADDATA");
            var radicals = Enumerable.Range(1, 999).Select(i => table.Get(i).FirstOrDefault()).ToList();
            int last = radicals.FindLastIndex(ch => ch != null);
            if (last >= 0) OpenTexts("radical-count", radicals.Take(last + 1).ToList(), "未有部首");
        }
        else return OpenLookupRow("SBDATA", key);
        return true;
    }

    private bool OpenLookupRow(string table, int row)
    {
        var list = Data.Table(Lang, table).Get(row);
        OpenTexts(table == "SBDATA" ? "sym" : "lookup-result", list, "沒有中文字供選用");
        return true;
    }

    private bool OpenExtra(bool shape)
    {
        if (Buf.Length == 0 || Main.Get(Code()).Count == 0) { Beep(); return true; }
        SetList(shape ? "shape-extra" : "sound-extra",
            (shape ? Enumerable.Range(1, 9).Select(i => i.ToString()) :
                PyInitialNames.Skip(1)).ToList(), false);
        State = St.Select;
        return true;
    }

    private bool FilterExtra(bool shape, int key)
    {
        if (shape && key == 0) { Beep(); return true; }
        var filtered = Main.Get(Code()).Where(ch => ch != Tbl.Blank && Data.InExtraGroup(Lang, shape, key, ch)).ToList();
        OpenTexts("code", filtered, "沒有中文字供選用");
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
            }
            Last = s;
        }
        else if (Kind == "phrase" || Kind == "fav")
        {
            var e = Elements(s);
            Last = e.Count > 0 ? e[e.Count - 1] : "";
        }
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
                return Kind == "punct" ? "Q9_P.png" : Kind == "emojicat" ? "Q9_emoji.png" : null;
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
            if (Mode == Canton) return Buf.Length > 0 || HasRelated ? "PYREL.png" : "PY0.png";
            if (Buf.Length == 0) return HasRelated ? "Q9STIM1.png" : "Q9STIM0.png";
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
