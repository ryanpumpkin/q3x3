using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

internal static class ComparePanel
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            string output = args[1], keys = args[3];
            int size = int.Parse(args[4]), x = int.Parse(args[5]), y = int.Parse(args[6]);
            int height = int.Parse(args[7]), tolerance = int.Parse(args[8]);
            string scratch = AppDomain.CurrentDomain.BaseDirectory;
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Dbcs.LoadHkscs(args[2]);
            var data = new Q9Data(@"C:\Program Files\Q9W10B5", scratch);
            var engine = new Engine(data);
            var settings = new Settings(scratch);
            settings["Size"] = size;
            using (var panel = new Q9Panel(data, engine, settings))
            using (var source = new Bitmap(args[0]))
            {
                panel.Capture_ = true;
                foreach (char key in keys)
                {
                    int mode = "SGPZCNE".IndexOf(key) + 1;   // N 數字、E 表情符號
                    if (mode > 0) panel.SetModeForPreview(mode);
                    else panel.Press(key == 'b' ? '\b' : key);
                }
                var bounds = new Rectangle(x, y, panel.Width, height == 0 ? panel.Height : height);
                if (bounds.Height > panel.Height || !new Rectangle(Point.Empty, source.Size).Contains(bounds))
                    throw new ArgumentException("The reference crop is outside the screenshot or exceeds the panel height.");

                Console.WriteLine("Replay: {0} | {1}/{2} | output: {3}", keys, engine.State, engine.Kind, string.Concat(panel.Sent));
                Console.WriteLine("Top-to-bottom grid: {0}", string.Join(" / ", new[] { 6, 3, 0 }
                    .Select(row => string.Join(" ", engine.Cand.Skip(row).Take(3).Select(ch => ch ?? "-")))));
                string[] info = engine.InfoLines();
                if (info != null) Console.WriteLine(string.Join(" | ", info));
                using (var expected = source.Clone(bounds, PixelFormat.Format32bppArgb))
                using (var rendered = new Bitmap(panel.Width, panel.Height))
                {
                    panel.DrawToBitmap(rendered, panel.ClientRectangle);
                    using (var actual = rendered.Clone(new Rectangle(Point.Empty, bounds.Size), PixelFormat.Format32bppArgb))
                        return Compare(expected, actual, output, tolerance);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Compare(Bitmap expected, Bitmap actual, string output, int tolerance)
    {
        // Skin-relative bands keep the report useful at every supported panel size.
        string[] regions = { "Toolbar", "Input grid", "Side buttons and skin", "Information and page buttons" };
        int[] changed = new int[regions.Length], totals = new int[regions.Length], maxDelta = new int[regions.Length];
        Directory.CreateDirectory(output);
        using (var diff = new Bitmap(actual.Width, actual.Height))
        {
            for (int y = 0; y < actual.Height; y++)
            for (int x = 0; x < actual.Width; x++)
            {
                int region = y < actual.Width * .21 ? 0 : y >= actual.Width * 1.19 ? 3
                    : x >= actual.Width * .045 && x < actual.Width * .74 && y >= actual.Width * .22 && y < actual.Width * 1.13 ? 1 : 2;
                Color expectedPixel = expected.GetPixel(x, y), actualPixel = actual.GetPixel(x, y);
                int delta = Math.Max(Math.Abs(expectedPixel.R - actualPixel.R),
                    Math.Max(Math.Abs(expectedPixel.G - actualPixel.G), Math.Abs(expectedPixel.B - actualPixel.B)));
                totals[region]++;
                maxDelta[region] = Math.Max(maxDelta[region], delta);
                if (delta > tolerance)
                {
                    changed[region]++;
                    diff.SetPixel(x, y, Color.Magenta);
                }
                else
                {
                    diff.SetPixel(x, y, Color.FromArgb(actualPixel.R / 3, actualPixel.G / 3, actualPixel.B / 3));
                }
            }
            expected.Save(Path.Combine(output, "reference.png"), ImageFormat.Png);
            actual.Save(Path.Combine(output, "rebuild.png"), ImageFormat.Png);
            diff.Save(Path.Combine(output, "difference.png"), ImageFormat.Png);
        }
        Console.WriteLine("RGB channel tolerance: {0} | compared {1} x {2}", tolerance, actual.Width, actual.Height);
        for (int i = 0; i < regions.Length; i++)
            if (totals[i] > 0)
                Console.WriteLine("{0}: {1}/{2} differing pixels ({3:P1}), maximum channel delta {4}",
                    regions[i], changed[i], totals[i], (double)changed[i] / totals[i], maxDelta[i]);
        Console.WriteLine("Images: " + output);
        return changed.Sum() == 0 ? 0 : 1;
    }
}
