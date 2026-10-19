// Standalone GDI+ cover renderer. Every daily close is retained in the polyline.
// The projection and gold/cream palette follow the approved KOSPI film.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

public static class KospiPublishCovers
{
    const int SS = 2;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Color Gold = Hex("D7BD80"), Cream = Hex("FFF0D1"), White = Hex("F0F2F3"),
        Muted = Hex("8392A7"), Teal = Hex("61D7BE"), Red = Hex("EE937D");
    static readonly List<Sample> Daily = new List<Sample>();
    static readonly List<TextRecord> TextRecords = new List<TextRecord>();
    static readonly List<object> ImageRecords = new List<object>();
    static int Width, Height;
    static double PriceMax;
    static string Primary, Accent, Subtitle, InstrumentName, RangeLabel, Unit, Cutoff, DurationLabel, Symbol, MeasureLabel;
    static string CurrentImage;
    sealed class Sample { public DateTime Date; public double Close; }
    sealed class TextRecord
    {
        public string image { get; set; }
        public string text { get; set; }
        public float x { get; set; }
        public float y { get; set; }
        public float width { get; set; }
        public float height { get; set; }
        public float font_size { get; set; }
    }
    sealed class Projection
    {
        readonly RectangleF Box;
        readonly double WorldWidth, X0, Y0, ScaleX, ScaleY;
        public Projection(RectangleF box, bool portrait)
        {
            Box = box; WorldWidth = portrait ? 11.0 : 16.0;
            var corners = new[] { Raw(0, 0, 0), Raw(1, 0, 0), Raw(0, PriceMax, 0), Raw(1, PriceMax, 0) };
            X0 = corners.Min(p => p.X); Y0 = corners.Min(p => p.Y);
            ScaleX = Box.Width / (corners.Max(p => p.X) - X0);
            ScaleY = Box.Height / (corners.Max(p => p.Y) - Y0);
        }
        PointF Raw(double u, double value, double z)
        {
            double x = (u - .5) * WorldWidth, y = value / PriceMax * 5.4;
            double yaw = 17 * Math.PI / 180, pitch = 14 * Math.PI / 180;
            double xx = x * Math.Cos(yaw) + z * Math.Sin(yaw);
            double zz = -x * Math.Sin(yaw) + z * Math.Cos(yaw);
            double yy = y * Math.Cos(pitch) - zz * Math.Sin(pitch);
            double q = 22 / (22 + zz * Math.Cos(pitch) + y * Math.Sin(pitch));
            return new PointF((float)(xx * q), (float)(-yy * q));
        }
        public PointF At(DateTime date, double value, double z = 0)
        {
            double u = (date - Daily[0].Date).TotalDays / (Daily[Daily.Count - 1].Date - Daily[0].Date).TotalDays;
            var p = Raw(u, value, z);
            return new PointF((float)(Box.Left + (p.X - X0) * ScaleX), (float)(Box.Top + (p.Y - Y0) * ScaleY));
        }
    }
    static Color Hex(string text) { return ColorTranslator.FromHtml("#" + text); }
    static Color Alpha(Color color, int alpha) { return Color.FromArgb(Math.Clamp(alpha, 0, 255), color); }
    static Graphics Setup(Bitmap bitmap, bool supersample = true)
    {
        var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (supersample) g.ScaleTransform(SS, SS);
        return g;
    }
    static float Text(Graphics g, string text, float x, float y, float size, Color color,
                      bool bold = false, bool number = false, int align = 0, bool record = true)
    {
        using var font = new Font(number ? "Bahnschrift" : "Microsoft YaHei UI", size,
            bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        sf.FormatFlags |= StringFormatFlags.NoWrap;
        var measure = g.MeasureString(text, font, new SizeF(10000, 10000), sf);
        float left = align == 1 ? x - measure.Width : align == 2 ? x - measure.Width / 2 : x;
        if (record)
        {
            float marginX = Width * .07f, marginY = Height * .07f;
            if (left < marginX - 1 || left + measure.Width > Width - marginX + 1 ||
                y < marginY - 1 || y + measure.Height > Height - marginY + 1)
                throw new InvalidOperationException("Text exceeds 7% inset: " + text + " @ " + left + "," + y);
            TextRecords.Add(new TextRecord { image = CurrentImage, text = text, x = left, y = y,
                width = measure.Width, height = measure.Height, font_size = size });
        }
        g.DrawString(text, font, brush, new PointF(left, y), sf);
        return measure.Width;
    }
    static void Line(Graphics g, PointF a, PointF b, Color color, float width = 1)
    {
        using var pen = new Pen(color, width);
        pen.StartCap = pen.EndCap = LineCap.Round;
        g.DrawLine(pen, a, b);
    }
    static void Dot(Graphics g, PointF p, float radius, Color color)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, p.X - radius, p.Y - radius, radius * 2, radius * 2);
    }
    static double Noise(int x, int y)
    {
        uint h = unchecked((uint)(x * 374761393 + y * 668265263));
        h = unchecked((h ^ (h >> 13)) * 1274126177u);
        return ((h ^ (h >> 16)) & 65535) / 65535.0 - .5;
    }
    static void Glow(Graphics g, float x, float y, float rx, float ry, Color color, int alpha)
    {
        int w = Math.Max(4, (int)(rx * 2)), h = Math.Max(4, (int)(ry * 2));
        using var sprite = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bits = sprite.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[bits.Stride * h];
        double edge = Math.Exp(-4.2);
        for (int j = 0; j < h; j++) for (int i = 0; i < w; i++)
        {
            double dx = (i + .5 - w / 2.0) / (w / 2.0), dy = (j + .5 - h / 2.0) / (h / 2.0);
            double r2 = dx * dx + dy * dy;
            if (r2 >= 1) continue;
            int k = j * bits.Stride + i * 4;
            bytes[k] = color.B; bytes[k + 1] = color.G; bytes[k + 2] = color.R;
            bytes[k + 3] = (byte)Math.Clamp((int)Math.Round(alpha * (Math.Exp(-4.2 * r2) - edge) / (1 - edge) + Noise(i, j)), 0, 255);
        }
        Marshal.Copy(bytes, 0, bits.Scan0, bytes.Length); sprite.UnlockBits(bits);
        g.DrawImage(sprite, new RectangleF(x - rx, y - ry, rx * 2, ry * 2),
            new RectangleF(0, 0, w, h), GraphicsUnit.Pixel);
    }
    static void Background(Graphics g, bool portrait)
    {
        using (var brush = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Hex("060B16"), Hex("050D1B"), 35f))
            g.FillRectangle(brush, 0, 0, Width, Height);
        Glow(g, Width * .67f, Height * .58f, Width * .65f, Height * .48f, Hex("244F83"), 48);
        Glow(g, Width * .78f, Height * .52f, Width * .35f, Height * .25f, Hex("3D4F83"), 28);
        Glow(g, Width * .87f, Height * .55f, Width * .23f, Height * .19f, Hex("9D7151"), 16);
        var random = new Random(20261018);
        for (int i = 0; i < 18000; i++)
            Dot(g, new PointF((float)random.NextDouble() * Width, (float)random.NextDouble() * Height),
                .28f, Alpha(Hex("B4C9E5"), random.Next(2, 10)));
        // Fine architectural lines, with contrast below the data and type.
        for (int i = 0; i < 7; i++)
            Line(g, new PointF(0, Height * .7f + i * 53), new PointF(Width, Height * .44f + i * 53), Alpha(Muted, 8), .7f);
    }
    static float Fitted(Graphics g, string value, float maxWidth, float initial, bool bold = false, bool numeric = false)
    {
        float size = initial;
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        while (size > 12) {
            using var font = new Font(numeric ? "Bahnschrift" : "Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            if (g.MeasureString(value, font, new SizeF(10000, 10000), sf).Width <= maxWidth) break;
            size -= 1;
        }
        return size;
    }
    static void Header(Graphics g, bool portrait)
    {
        float x = portrait ? 86 : 108, y = portrait ? 105 : 82, available = Width - x * 2;
        using (var brush = new SolidBrush(Gold)) g.FillRectangle(brush, x, y + 5, 4, 20);
        Text(g, Symbol, x + 19, y, Fitted(g, Symbol, available * .45f, 23, false, true), Gold, false, true);
        float badgeW = portrait ? 112 : 130, badgeX = Width - x - badgeW;
        using (var pen = new Pen(Alpha(Gold, 68), 1)) g.DrawRectangle(pen, badgeX, y - 3, badgeW, 39);
        Text(g, DurationLabel, badgeX + badgeW / 2, y + 5, Fitted(g, DurationLabel, badgeW - 14, 19, false, true), Cream, false, true, 2);
        if (!portrait) {
            float titleSize = Fitted(g, Primary + Accent, available, 140, true);
            float next = x + Text(g, Primary, x, 161, titleSize, White, true) + 10;
            Text(g, Accent, next, 161, titleSize, Gold, true);
            Text(g, Subtitle, x + 3, 357, Fitted(g, Subtitle, available * .70f, 36), Alpha(White, 226));
            Text(g, RangeLabel, Width - x, 366, 27, Gold, false, true, 1);
            Line(g, new PointF(x, 432), new PointF(Width - x, 432), Alpha(Muted, 45));
            Line(g, new PointF(x, 432), new PointF(x + 142, 432), Alpha(Gold, 190), 1.6f);
        } else {
            Text(g, Primary, x - 1, 196, Fitted(g, Primary, available, 146, true), White, true);
            Text(g, Accent, x, 382, Fitted(g, Accent, available, 182, true), Gold, true);
            Text(g, Subtitle, x + 3, 628, Fitted(g, Subtitle, available, 34), Alpha(White, 226));
            Text(g, RangeLabel, x + 3, 690, 27, Gold, false, true);
            Line(g, new PointF(x, 751), new PointF(Width - x, 751), Alpha(Muted, 45));
            Line(g, new PointF(x, 751), new PointF(x + 125, 751), Alpha(Gold, 190), 1.6f);
        }
    }
    static IEnumerable<int> TickYears()
    {
        int a = Daily[0].Date.Year, b = Daily[Daily.Count - 1].Date.Year;
        int step = Math.Max(1, (int)Math.Ceiling((b-a)/4.0));
        return Enumerable.Range(0, Math.Max(1,(b-a)/step+1)).Select(i=>a+i*step).Append(b).Distinct();
    }
    static void Curve(Graphics g, bool portrait)
    {
        var area = portrait ? new RectangleF(110, 815, 835, 366) : new RectangleF(140, 499, 1146, 370);
        var projection = new Projection(area, portrait);
        var first = Daily[0].Date; var last = Daily[Daily.Count - 1].Date;
        var points = Daily.Select(d => projection.At(d.Date, d.Close)).ToArray();
        var low = Daily.OrderBy(d => d.Close).First();
        var high = Daily.OrderByDescending(d => d.Close).First();
        var lp = projection.At(low.Date, low.Close); var hp = projection.At(high.Date, high.Close);
        // A subtle perspective floor shares the exact chart projection.
        using (var floor = new GraphicsPath())
        {
            floor.AddPolygon(new[] { projection.At(first, 0), projection.At(last, 0),
                projection.At(last, 0, 1.1), projection.At(first, 0, 1.1) });
            using var brush = new SolidBrush(Alpha(Hex("254666"), 20)); g.FillPath(brush, floor);
        }
        foreach (double value in new[] { 0.0, PriceMax / 2, PriceMax })
        {
            var a = projection.At(first, value); var b = projection.At(last, value);
            Line(g, a, b, Alpha(Muted, value == 0 ? 43 : 23), .8f);
            if (value > 0) {
                string axisLabel = value.ToString("0", Inv);
                float axisSize = portrait ? 14 : 15;
                using var axisFont = new Font("Bahnschrift", axisSize, FontStyle.Regular, GraphicsUnit.Pixel);
                using var axisFormat = new StringFormat(StringFormat.GenericTypographic);
                float axisWidth = g.MeasureString(axisLabel, axisFont, new SizeF(10000, 10000), axisFormat).Width;
                Text(g, axisLabel, Math.Max(a.X - 14, Width * .07f + axisWidth), a.Y - 8,
                    axisSize, Alpha(Muted, 150), false, true, 1);
            }
        }
        // Red/teal curtains compare close against 95 trading observations earlier,
        // matching the film. The visible line itself includes all 9,489 prices.
        for (int i = 95; i < Daily.Count - 1; i += 9)
        {
            int j = Math.Min(i + 9, Daily.Count - 1);
            Color phase = Daily[i].Close >= Daily[i - 95].Close ? Red : Teal;
            PointF bottomA = projection.At(Daily[i].Date, 0), bottomB = projection.At(Daily[j].Date, 0);
            using var brush = new SolidBrush(Alpha(phase, portrait ? 31 : 35));
            g.FillPolygon(brush, new[] { points[i], points[j], bottomB, bottomA });
            Line(g, points[i], bottomA, Alpha(phase, 15), .7f);
        }
        foreach (var layer in new[] { (width: 19f, alpha: 3), (width: 12f, alpha: 6),
            (width: 7f, alpha: 14), (width: 4f, alpha: 35), (width: 2.1f, alpha: 150), (width: 1.1f, alpha: 248) })
        {
            using var pen = new Pen(Alpha(layer.alpha == 248 ? Cream : Gold, layer.alpha), layer.width);
            pen.LineJoin = LineJoin.Round; pen.StartCap = pen.EndCap = LineCap.Round;
            g.DrawLines(pen, points);
        }
        Glow(g, hp.X, hp.Y, 80, 68, Gold, 50);
        Glow(g, hp.X, hp.Y, 25, 23, Gold, 100);
        Glow(g, hp.X, hp.Y, 10, 10, Cream, 170);
        Line(g, new PointF(hp.X - 36, hp.Y), new PointF(hp.X + 36, hp.Y), Alpha(Cream, 90), .6f);
        Line(g, new PointF(hp.X, hp.Y - 32), new PointF(hp.X, hp.Y + 32), Alpha(Cream, 90), .6f);
        Dot(g, hp, 2.7f, Cream);
        Glow(g, lp.X, lp.Y, 18, 17, Teal, 65); Dot(g, lp, 3, Teal);
        var end = points[points.Length - 1];
        Glow(g, end.X, end.Y, 28, 24, Gold, 100); Dot(g, end, 3.3f, Cream);
        using (var pen = new Pen(Alpha(Gold, 150), .7f)) g.DrawEllipse(pen, end.X - 7, end.Y - 7, 14, 14);
        // Keep the preferred left-hand annotation, including for an early peak.
        // Measure both lines before clamping so large quotes remain inside the inset.
        string highTitle = high.Date.Year.ToString() + "年高点", highPrice = high.Close.ToString("N2", Inv);
        float highTitleSize = portrait ? 23 : 25;
        float highPriceSize = Fitted(g, highPrice, portrait ? 220 : 270, portrait ? 28 : 31, false, true);
        using var titleFont = new Font("Microsoft YaHei UI", highTitleSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var priceFont = new Font("Bahnschrift", highPriceSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var annotationFormat = new StringFormat(StringFormat.GenericTypographic);
        SizeF titleBounds = g.MeasureString(highTitle, titleFont, new SizeF(10000, 10000), annotationFormat);
        SizeF priceBounds = g.MeasureString(highPrice, priceFont, new SizeF(10000, 10000), annotationFormat);
        float highWidth = Math.Max(titleBounds.Width, priceBounds.Width);
        float preferredHighX = hp.X - (portrait ? 187 : 230);
        float highX = Math.Clamp(preferredHighX, Width * .08f, Width * .93f - highWidth);
        float preferredHighY = hp.Y - (portrait ? 13 : 8);
        if (Math.Abs(highX - preferredHighX) > 1)
            preferredHighY = hp.Y - priceBounds.Height - 28;
        float highY = Math.Clamp(preferredHighY, portrait ? 805 : 480,
            area.Bottom - priceBounds.Height - 10);
        float leaderX = Math.Clamp(hp.X, highX, highX + highWidth);
        Line(g, new PointF(leaderX, highY + 18), hp, Alpha(Gold, 140), .8f);
        Text(g, highTitle, highX, highY - 26, highTitleSize, White, true);
        Text(g, highPrice, highX, highY + 10, highPriceSize, Gold, false, true);
        float lowX = Math.Clamp(lp.X - (portrait ? 22 : 48), Width*.08f, Width*.78f), lowY = Math.Clamp(lp.Y - (portrait ? 106 : 114), portrait ? 805 : 480, portrait ? 1090 : 800);
        Line(g, new PointF(lowX + 28, lowY + 71), new PointF(lp.X, lp.Y - 9), Alpha(Teal, 130), .8f);
        Text(g, low.Date.Year.ToString() + "年低点", lowX, lowY, portrait ? 22 : 24, White, true);
        Text(g, low.Close.ToString("N2", Inv), lowX, lowY + 35, portrait ? 28 : 30, Teal, false, true);
        foreach (int year in TickYears())
        {
            var day = year == first.Year ? first : year == last.Year ? last : new DateTime(year, 1, 1);
            var p = projection.At(day, 0);
            Line(g, p, new PointF(p.X, p.Y + 5), Alpha(Muted, 85), .8f);
            Text(g, year.ToString(Inv), p.X, p.Y + 14, portrait ? 17 : 18,
                Alpha(Muted, 220), false, true, year == first.Year ? 0 : year == last.Year ? 1 : 2);
        }
        ImageRecords.Add(new { file = CurrentImage, width = Width, height = Height,
            ratio = portrait ? "3:4" : "4:3", sample_count = points.Length,
            projection = "fixed perspective; yaw 17deg, pitch 14deg; shared data/axes",
            price_axis = new[] { 0.0, PriceMax },
            low_anchor = new { date = low.Date.ToString("yyyy-MM-dd"), close = low.Close, x = lp.X, y = lp.Y },
            high_anchor = new { date = high.Date.ToString("yyyy-MM-dd"), close = high.Close, x = hp.X, y = hp.Y },
            final_anchor = new { date = last.ToString("yyyy-MM-dd"), close = Daily[Daily.Count - 1].Close, x = end.X, y = end.Y } });
    }
    static void Footer(Graphics g, bool portrait)
    {
        float x = portrait ? 86 : 108, y = portrait ? 1250 : 930;
        Line(g, new PointF(x, y), new PointF(Width - x, y), Alpha(Muted, 55), .8f);
        Text(g, InstrumentName, x, y + 22, Fitted(g, InstrumentName, (Width - x * 2) * .68f, portrait ? 20 : 22), Alpha(White, 218));
        Text(g, "日收盘 · " + Unit, Width - x, y + 25, portrait ? 17 : 19, Muted, false, false, 1);
        if (portrait)
            Text(g, "数据截至 " + Cutoff + "  ·  " + MeasureLabel, x, y + 62, 18, Muted);
        else
        {
            Text(g, "数据截至 " + Cutoff, x, y + 50, 19, Muted);
            Text(g, MeasureLabel, Width - x, y + 50, 18, Muted, false, false, 1);
        }
    }
    static void Save(Bitmap big, string path, int width, int height)
    {
        using var result = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        result.SetResolution(96, 96);
        using (var g = Setup(result, false)) g.DrawImage(big, new Rectangle(0, 0, width, height));
        result.Save(path, Path.GetExtension(path).ToLowerInvariant() == ".jpg" ? ImageFormat.Jpeg : ImageFormat.Png);
    }
    static void Cover(string directory, bool portrait)
    {
        Width = portrait ? 1080 : 1440; Height = portrait ? 1440 : 1080;
        CurrentImage = portrait ? "cover_portrait_3x4.png" : "cover_landscape_4x3.png";
        using var image = new Bitmap(Width * SS, Height * SS, PixelFormat.Format24bppRgb);
        using (var g = Setup(image)) { Background(g, portrait); Header(g, portrait); Curve(g, portrait); Footer(g, portrait); }
        Save(image, Path.Combine(directory, CurrentImage), Width, Height);
        Save(image, Path.Combine(directory, portrait ? "qa/portrait_25pct.png" : "qa/landscape_25pct.png"), Width / 4, Height / 4);
    }
    static void Preview(string directory)
    {
        using var result = new Bitmap(1720, 1110, PixelFormat.Format24bppRgb);
        using var g = Setup(result, false);
        g.Clear(Hex("E9EDF1"));
        using var l = Image.FromFile(Path.Combine(directory, "cover_landscape_4x3.png"));
        using var p = Image.FromFile(Path.Combine(directory, "cover_portrait_3x4.png"));
        CurrentImage = "cover_preview.jpg";
        Text(g, "横封面 4:3", 60, 42, 27, Hex("253147"), true, false, 0, false);
        Text(g, "竖封面 3:4", 1060, 42, 27, Hex("253147"), true, false, 0, false);
        g.DrawImage(l, new Rectangle(60, 104, 936, 702));
        g.DrawImage(p, new Rectangle(1060, 104, 594, 792));
        Text(g, "1440 × 1080 px", 60, 832, 23, Hex("617086"), false, true, 0, false);
        Text(g, "1080 × 1440 px", 1060, 923, 23, Hex("617086"), false, true, 0, false);
        Text(g, "发布封面 · " + Symbol + " " + RangeLabel, 60, 1008, 27, Hex("253147"), true, false, 0, false);
        result.Save(Path.Combine(directory, "cover_preview.jpg"), ImageFormat.Jpeg);
    }
    static string Hash(string path) { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(); }
    public static void Render(string data, string directory, string project)
    {
        Daily.Clear(); TextRecords.Clear(); ImageRecords.Clear();
        using var doc = JsonDocument.Parse(File.ReadAllText(project));
        var conf = doc.RootElement;
        var publishing = conf.GetProperty("publishing");
        var lines = publishing.GetProperty("cover_lines").EnumerateArray().Select(e=>e.GetString()).ToArray();
        Primary=lines[0]; Accent=lines[1]; Subtitle=publishing.GetProperty("cover_subtitle").GetString();
        var instrument=conf.GetProperty("instrument");
        InstrumentName=instrument.GetProperty("name").GetString(); Symbol=instrument.GetProperty("symbol").GetString(); Unit=instrument.GetProperty("unit").GetString();
        DurationLabel=conf.GetProperty("video").GetProperty("duration").GetInt32().ToString()+" SEC";
        MeasureLabel=instrument.TryGetProperty("dividends_included",out var div)&&div.GetBoolean()?"日收盘":"不含分红再投资";
        foreach (string line in File.ReadLines(data).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] columns = line.Split(',');
            var sample = new Sample { Date = DateTime.ParseExact(columns[0], "yyyy-MM-dd", Inv), Close = double.Parse(columns[1], Inv) };
            if (!double.IsFinite(sample.Close) || sample.Close <= 0 ||
                (Daily.Count > 0 && sample.Date <= Daily[Daily.Count - 1].Date))
                throw new InvalidDataException("Daily data must be positive, unique and sorted.");
            Daily.Add(sample);
        }
        if (Daily.Count < 2) throw new InvalidDataException("At least two prices are required.");
        int firstYear=Daily[0].Date.Year,lastYear=Daily[Daily.Count-1].Date.Year;
        RangeLabel=firstYear.ToString()+"—"+lastYear.ToString();
        Cutoff=Daily[Daily.Count-1].Date.ToString("yyyy.MM.dd");
        double max=Daily.Max(d=>d.Close),step=Math.Pow(10,Math.Floor(Math.Log10(max)))/2;
        PriceMax=Math.Ceiling(max*1.03/step)*step;
        Directory.CreateDirectory(directory); Directory.CreateDirectory(Path.Combine(directory, "qa"));
        Cover(directory, false); Cover(directory, true); Preview(directory);
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var manifest = new { series = new { file = Path.GetFileName(data), sha256 = Hash(data), count = Daily.Count,
            first = Daily[0].Date.ToString("yyyy-MM-dd"), last = Daily[Daily.Count - 1].Date.ToString("yyyy-MM-dd"),
            measure = "daily_close_price_index", dividends_included = false },
            images = ImageRecords, text_bounds = TextRecords, minimum_text_inset = .07,
            downsample = "2x supersampling, bicubic", fonts = new[] { "Microsoft YaHei UI", "Bahnschrift" } };
        File.WriteAllText(Path.Combine(directory, "render_manifest.json"), JsonSerializer.Serialize(manifest, json), new System.Text.UTF8Encoding(false));
        foreach (string name in new[] { "cover_landscape_4x3.png", "cover_portrait_3x4.png" })
        {
            long bytes = new FileInfo(Path.Combine(directory, name)).Length;
            if (bytes > 5 * 1024 * 1024) throw new InvalidDataException("Cover exceeds the 5 MiB production target: " + name);
            Console.WriteLine(name + "  " + bytes.ToString("N0", Inv) + " bytes");
        }
    }
}
