using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

// 查 menu extras. 數字對譯, 英文對譯 and 中英文地址互查 come from older 九方 versions (documented in the shared
// q9help.chm, not present in W10 1.1.0.0); the web links mirror W10's query table (Search*/CEDict* items)
// but open the public sites directly instead of going through 九方's own redirect server.

internal static class NumberWords
{
    // 數字對譯形式, in the order of the original menu.
    internal static readonly string[] FormatNames = { "ONE TWO HUNDRED", "一二三佰千萬", "壹貳參佰仟萬", "１２３佰千萬" };

    internal static bool IsNumber(string text)
    {
        string integer, fraction;
        return Split(text, out integer, out fraction);
    }

    // Accepts half- or full-width digits, thousands separators and one decimal point.
    private static bool Split(string text, out string integer, out string fraction)
    {
        integer = fraction = "";
        if (string.IsNullOrEmpty(text)) return false;
        var digits = new StringBuilder();
        bool point = false;
        foreach (char raw in text.Trim())
        {
            char c = raw >= '０' && raw <= '９' ? (char)('0' + raw - '０') : raw == '．' ? '.' : raw == '，' ? ',' : raw;
            if (c >= '0' && c <= '9') digits.Append(c);
            else if (c == ',' && !point) continue;
            else if (c == '.' && !point) { point = true; integer = digits.ToString(); digits.Clear(); }
            else return false;
        }
        if (point) fraction = digits.ToString(); else integer = digits.ToString();
        integer = integer.TrimStart('0');
        if (integer.Length == 0) integer = "0";
        return (integer.Length > 0 || fraction.Length > 0) && integer.Length <= 16 && fraction.Length <= 10 &&
            !(point && fraction.Length == 0);
    }

    internal static string Convert(string text, int format, bool simplified)
    {
        string integer, fraction;
        if (!Split(text, out integer, out fraction)) return null;
        string result = format == 0 ? English(integer, fraction) : Chinese(integer, fraction, format);
        if (simplified && format != 0)
            result = result.Replace('萬', '万').Replace('億', '亿').Replace('貳', '贰').Replace('參', '叁')
                .Replace('陸', '陆').Replace('點', '点');
        return result;
    }

    private static readonly string[] Ones = { "ZERO", "ONE", "TWO", "THREE", "FOUR", "FIVE", "SIX", "SEVEN", "EIGHT", "NINE",
        "TEN", "ELEVEN", "TWELVE", "THIRTEEN", "FOURTEEN", "FIFTEEN", "SIXTEEN", "SEVENTEEN", "EIGHTEEN", "NINETEEN" };
    private static readonly string[] Tens = { "", "", "TWENTY", "THIRTY", "FORTY", "FIFTY", "SIXTY", "SEVENTY", "EIGHTY", "NINETY" };
    private static readonly string[] Scales = { "", " THOUSAND", " MILLION", " BILLION", " TRILLION", " QUADRILLION" };

    // British style, as in the help example: 123 → ONE HUNDRED AND TWENTY-THREE.
    private static string English(string integer, string fraction)
    {
        long value = long.Parse(integer);
        string words;
        if (value == 0) words = "ZERO";
        else
        {
            var groups = new List<int>();
            for (long v = value; v > 0; v /= 1000) groups.Add((int)(v % 1000));
            var parts = new List<string>();
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                int g = groups[i];
                if (g == 0) continue;
                // "AND" before a last group under 100 when larger groups exist: 1005 → ONE THOUSAND AND FIVE
                bool and = i == 0 && g < 100 && groups.Count > 1;
                parts.Add((and ? "AND " : "") + Hundreds(g) + Scales[i]);
            }
            words = string.Join(" ", parts);
        }
        if (fraction.Length > 0)
            words += " POINT " + string.Join(" ", fraction.Select(d => Ones[d - '0']));
        return words;
    }

    private static string Hundreds(int n)
    {
        string text = "";
        if (n >= 100) { text = Ones[n / 100] + " HUNDRED"; n %= 100; if (n > 0) text += " AND "; }
        if (n >= 20) text += Tens[n / 10] + (n % 10 > 0 ? "-" + Ones[n % 10] : "");
        else if (n > 0) text += Ones[n];
        return text;
    }

    private static string Chinese(string integer, string fraction, int format)
    {
        string digits = format == 2 ? "零壹貳參肆伍陸柒捌玖" : format == 3 ? "０１２３４５６７８９" : "零一二三四五六七八九";
        string units = format == 2 ? "仟佰拾" : "千百十";
        string[] big = { "", "萬", "億", "兆" };
        string zero = format == 3 ? "０" : "零";
        var groups = new List<int>();
        for (long v = long.Parse(integer); v > 0; v /= 10000) groups.Add((int)(v % 10000));
        var text = new StringBuilder();
        bool pendingZero = false;
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            int g = groups[i];
            if (g == 0) { pendingZero = text.Length > 0; continue; }
            if (text.Length > 0 && (pendingZero || g < 1000)) text.Append(zero);
            pendingZero = false;
            bool started = false, gap = false;
            for (int pos = 3, div = 1000; pos >= 0; pos--, div /= 10)
            {
                int d = g / div % 10;
                if (d == 0) { if (started) gap = true; continue; }
                if (gap) { text.Append(zero); gap = false; }
                text.Append(digits[d]);
                if (pos > 0) text.Append(units[3 - pos]);
                started = true;
            }
            text.Append(big[i]);
        }
        if (text.Length == 0) text.Append(zero);
        string result = text.ToString();
        if (format == 1 && result.StartsWith("一十")) result = result.Substring(1);   // 一十二 → 十二
        if (fraction.Length > 0) result += "點" + string.Concat(fraction.Select(d => digits[d - '0']));
        return result;
    }
}

internal sealed class AddressMatch
{
    internal string Chinese, English, Latitude, Longitude;
}

internal static class AddressLookup
{
    // 香港政府「地址查詢服務」(ALS): free, public, returns both Chinese and English addresses.
    internal static List<AddressMatch> Find(string query)
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
        var request = (HttpWebRequest)WebRequest.Create("https://www.als.gov.hk/lookup?n=5&q=" + Uri.EscapeDataString(query));
        request.Accept = "application/json";
        request.UserAgent = "Q9Rebuild";
        request.Timeout = 10000;
        string json;
        using (var response = request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            json = reader.ReadToEnd();
        var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
        var results = new List<AddressMatch>();
        var suggested = Get(root, "SuggestedAddress") as object[];
        if (suggested == null) return results;
        foreach (object item in suggested)
        {
            var premises = Get(Get(item, "Address"), "PremisesAddress");
            var geo = Get(premises, "GeospatialInformation");
            var match = new AddressMatch
            {
                Chinese = ChineseAddress(Get(premises, "ChiPremisesAddress")),
                English = EnglishAddress(Get(premises, "EngPremisesAddress")),
                Latitude = Text(geo, "Latitude"),
                Longitude = Text(geo, "Longitude")
            };
            if (match.Chinese.Length > 0 || match.English.Length > 0) results.Add(match);
        }
        return results;
    }

    private static object Get(object node, string key)
    {
        var map = node as Dictionary<string, object>;
        object value;
        return map != null && map.TryGetValue(key, out value) ? value : null;
    }

    private static string Text(object node, string key) { return (Get(node, key) as string ?? "").Trim(); }

    private static string Number(object street)
    {
        string from = Text(street, "BuildingNoFrom"), to = Text(street, "BuildingNoTo");
        return to.Length > 0 ? from + "-" + to : from;
    }

    private static string ChineseAddress(object a)
    {
        if (a == null) return "";
        var text = new StringBuilder();
        text.Append(Text(a, "Region")).Append(Text(Get(a, "ChiDistrict"), "DcDistrict"));
        var village = Get(a, "ChiVillage");
        if (village != null) text.Append(Text(village, "VillageName")).Append(Number(village)).Append(Number(village).Length > 0 ? "號" : "");
        var street = Get(a, "ChiStreet");
        if (street != null) { text.Append(Text(street, "StreetName")); string n = Number(street); if (n.Length > 0) text.Append(n).Append("號"); }
        text.Append(Text(Get(a, "ChiEstate"), "EstateName")).Append(Text(a, "BuildingName"));
        var block = Get(a, "ChiBlock");
        if (block != null) text.Append(Text(block, "BlockNo")).Append(Text(block, "BlockDescriptor"));
        return text.ToString();
    }

    private static string EnglishAddress(object a)
    {
        if (a == null) return "";
        var parts = new List<string>();
        var block = Get(a, "EngBlock");
        if (block != null)
            parts.Add(Text(block, "BlockDescriptorPrecedenceIndicator") == "Y"
                ? Text(block, "BlockDescriptor") + " " + Text(block, "BlockNo")
                : Text(block, "BlockNo") + " " + Text(block, "BlockDescriptor"));
        parts.Add(Text(a, "BuildingName"));
        parts.Add(Text(Get(a, "EngEstate"), "EstateName"));
        var village = Get(a, "EngVillage");
        if (village != null) parts.Add((Number(village) + " " + Text(village, "VillageName")).Trim());
        var street = Get(a, "EngStreet");
        if (street != null) parts.Add((Number(street) + " " + Text(street, "StreetName")).Trim());
        parts.Add(Text(Get(a, "EngDistrict"), "DcDistrict"));
        parts.Add(Text(a, "Region"));
        return string.Join(", ", parts.Where(p => p.Trim().Length > 0).Select(p => p.Trim()));
    }
}

internal static class QueryLinks
{
    // {0} = UTF-8 URL-encoded text, {1} = Big5 URL-encoded text (for the CUHK site).
    internal static readonly string[][] ChineseDictionaries =
    {
        new[] { "國語辭典(台灣教育部)", "https://dict.revised.moe.edu.tw/search.jsp?md=1&word={0}" },
        new[] { "粵語審音配詞字庫(香港中文大學)", "https://humanum.arts.cuhk.edu.hk/Lexis/lexi-can/search.php?q={1}" },
    };

    internal static readonly string[][] EnglishDictionaries =
    {
        new[] { "Google中英字典", "https://translate.google.com/?sl=auto&tl=zh-TW&text={0}&op=translate" },
        new[] { "雅虎中英字典", "https://tw.dictionary.search.yahoo.com/search?p={0}" },
        new[] { "金山詞霸中英字典", "https://www.iciba.com/word?w={0}" },
        new[] { "劍橋英文字典", "https://dictionary.cambridge.org/search/direct/?datasetsearch=english-chinese-traditional&q={0}" },
        new[] { "牛津英文字典", "https://www.oxfordlearnersdictionaries.com/search/english/?q={0}" },
    };

    internal static readonly string[][] Searches =
    {
        new[] { "Google 谷歌", "https://www.google.com/search?q={0}" },
        new[] { "Yahoo 雅虎", "https://hk.search.yahoo.com/search?p={0}" },
        new[] { "百度", "https://www.baidu.com/s?wd={0}" },
        new[] { "維基百科", "https://zh.wikipedia.org/w/index.php?search={0}" },
        new[] { "餐廳(開飯)", "https://www.openrice.com/zh/hongkong/restaurants?what={0}" },
        new[] { "商業電話(黃頁)", "https://www.yp.com.hk/Search/Result/zh/?keyword={0}" },
        new[] { "報章", "https://news.google.com/search?q={0}&hl=zh-HK&gl=HK&ceid=HK:zh-Hant" },
        new[] { "Facebook", "https://www.facebook.com/search/top/?q={0}" },
        new[] { "Youtube", "https://www.youtube.com/results?search_query={0}" },
        new[] { "拍賣", "https://www.carousell.com.hk/search/{0}" },
    };

    internal static string Url(string pattern, string text)
    {
        string big5;
        try
        {
            big5 = string.Concat(Encoding.GetEncoding(950).GetBytes(text).Select(b =>
                b < 0x80 && (char.IsLetterOrDigit((char)b)) ? ((char)b).ToString() : "%" + b.ToString("X2")));
        }
        catch (ArgumentException) { big5 = Uri.EscapeDataString(text); }
        return string.Format(pattern, Uri.EscapeDataString(text), big5);
    }

    internal static string Translate(string text, bool simplified)
    {
        return "https://translate.google.com/?sl=en&tl=" + (simplified ? "zh-CN" : "zh-TW") + "&text=" +
            Uri.EscapeDataString(text) + "&op=translate";
    }

    internal static string Map(AddressMatch match)
    {
        string q = match.Latitude.Length > 0 && match.Longitude.Length > 0
            ? match.Latitude + "," + match.Longitude : match.Chinese.Length > 0 ? match.Chinese : match.English;
        return "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(q);
    }
}
