param(
    [Parameter(Mandatory)][string]$Data,
    [string]$Output = (Join-Path $PSScriptRoot 'output/classic_video.mp4'),
    [string]$FrameList = '',
    [string]$SampleDir = (Join-Path $PSScriptRoot 'output/classic_samples'),
    [string]$CoverOutput = '',
    [switch]$CoverOnly,
    [ValidateRange(0.1, 1.0)][double]$ContentScale = 1.0,
    [ValidateRange(0, 6599)][int]$StartFrame = 0,
    [Alias('SmokeFrames')][ValidateRange(1, 6600)][int]$RenderFrames = 6600,
    [string]$FFmpeg = '',
    [ValidateSet('libx264', 'h264_nvenc')][string]$Encoder = 'libx264'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $PSVersionTable.PSVersion.Major -lt 7) { throw 'The classic renderer requires Windows and PowerShell 7.' }

if ($CoverOnly -and -not $CoverOutput) { throw '-CoverOnly requires -CoverOutput.' }

if ($StartFrame + $RenderFrames -gt 6600) { throw 'Requested frame range exceeds the classic220 preset.' }
if (-not $FrameList -and -not $CoverOnly) {
    if (-not $FFmpeg) { $FFmpeg = $env:FFMPEG_BIN }
    if (-not $FFmpeg) {
        $command = Get-Command ffmpeg -ErrorAction SilentlyContinue
        if ($command) { $FFmpeg = $command.Source }
    }
    if (-not $FFmpeg) { throw 'FFmpeg not found; pass -FFmpeg, set FFMPEG_BIN, or add it to PATH.' }
    if (-not (Test-Path -LiteralPath $FFmpeg -PathType Leaf)) {
        $command = Get-Command $FFmpeg -ErrorAction SilentlyContinue
        if ($command) { $FFmpeg = $command.Source }
        else { throw "FFmpeg executable was not found: $FFmpeg" }
    }
}
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$source = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

public static class ReusableClassicRenderer
{
    private const int Width = 1080;
    private const int Height = 1920;
    private const int Fps = 30;
    private const int FrameCount = 6600;
    private const float PlotLeft = 158f;
    private const float PlotRight = 1030f;
    private const float PlotTop = 330f;
    private const float PlotBottom = 1450f;
    private static readonly Color Red = ColorTranslator.FromHtml("#FF3B30");
    private static readonly Color Green = ColorTranslator.FromHtml("#34C759");
    private static readonly Color TextGray = Color.FromArgb(222, 226, 234);

    private sealed class Sample
    {
        public DateTime Date;
        public double Month;
        public double Close;
        public double Slope;
    }

    private sealed class Event
    {
        public DateTime Date;
        public double Month;
        public double Close;
        public string Label = "";
        public bool Major;
        public double Arrival;
        public double Hold;
    }

    private sealed class Model
    {
        public List<Sample> Daily = new List<Sample>();
        public List<Sample> Anchors = new List<Sample>();
        public List<Event> Events = new List<Event>();
        public DateTime Start;
        public DateTime End;
        public double InitialMonth;
        public double OverviewMin;
        public double OverviewMax;
        public double MinimumRange;
        public string Title = "";
        public string Subtitle = "";
        public string InstrumentLabel = "";
        public string Source = "";
        public string RangeLabel = "";
        public string Ending = "";
        public string Unit = "";
    }

    private struct FrameState
    {
        public DateTime Progress;
        public DateTime Head;
        public double HeadClose;
        public double CameraLeft;
        public double YMin;
        public double YMax;
    }

    public static void Run(string dataPath, string output, string frameList, string sampleDir,
        string coverOutput, bool coverOnly, int startFrame, int renderFrames, string ffmpeg, string encoder,
        double contentScale)
    {
        Model model = ReadModel(dataPath);
        FrameState[] states = coverOnly ? Array.Empty<FrameState>() : BuildStates(model);
        Console.WriteLine("Timeline: 6600 frames, 220 seconds; 12 calendar months visible.");
        foreach (Event ev in model.Events)
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
                "EVENT {0:yyyy-MM-dd} at {1:0.000}s, hold {2:0}s: {3}",
                ev.Date, ev.Arrival, ev.Hold, ev.Label));

        using (Bitmap background = BuildBackground())
        using (Font axisFont = new Font("Microsoft YaHei", 31f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font yearFont = new Font("Microsoft YaHei", 34f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font titleFont = new Font("Microsoft YaHei", 60f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font panelLabelFont = new Font("Microsoft YaHei", 34f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font valueFont = new Font("Microsoft YaHei", 66f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font eventFont = new Font("Microsoft YaHei", 34f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font endingFont = new Font("Microsoft YaHei", 55f, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            if (!String.IsNullOrWhiteSpace(coverOutput))
            {
                string coverPath = Path.GetFullPath(coverOutput);
                Directory.CreateDirectory(Path.GetDirectoryName(coverPath) ?? ".");
                using (Bitmap cover = RenderCover(model, background, (float)contentScale))
                    cover.Save(coverPath, ImageFormat.Png);
                Console.WriteLine("Cover " + coverPath);
            }
            if (coverOnly) return;

            if (!String.IsNullOrWhiteSpace(frameList))
            {
                Directory.CreateDirectory(sampleDir);
                foreach (int frame in ParseFrames(frameList))
                {
                    using (Bitmap bitmap = RenderFrame(model, states[frame], frame, background,
                        axisFont, yearFont, titleFont, panelLabelFont, valueFont, eventFont,
                        endingFont, (float)contentScale))
                    {
                        string path = Path.Combine(sampleDir,
                            "frame_" + frame.ToString("D6", CultureInfo.InvariantCulture) + ".png");
                        bitmap.Save(path, ImageFormat.Png);
                        Console.WriteLine(path);
                    }
                }
                return;
            }

            int count = renderFrames;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
            EncodeFrames(model, states, startFrame, count, output, ffmpeg, encoder, background,
                axisFont, yearFont, titleFont, panelLabelFont, valueFont, eventFont,
                endingFont, (float)contentScale);
        }
    }

    private static Model ReadModel(string path)
    {
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
        {
            JsonElement root = document.RootElement;
            Model model = new Model();
            foreach (JsonElement item in root.GetProperty("daily").EnumerateArray())
                model.Daily.Add(ReadSample(item));
            foreach (JsonElement item in root.GetProperty("anchors").EnumerateArray())
                model.Anchors.Add(ReadSample(item));
            foreach (JsonElement item in root.GetProperty("events").EnumerateArray())
            {
                DateTime date = ReadDate(item.GetProperty("date"));
                model.Events.Add(new Event {
                    Date = date,
                    Month = MonthCoordinate(date),
                    Close = item.GetProperty("close").GetDouble(),
                    Label = item.GetProperty("label").GetString() ?? "",
                    Major = item.TryGetProperty("major", out JsonElement major) && major.GetBoolean(),
                    Arrival = item.GetProperty("arrival").GetDouble(),
                    Hold = item.GetProperty("hold").GetDouble()
                });
            }
            model.Daily.Sort((a, b) => a.Date.CompareTo(b.Date));
            model.Anchors.Sort((a, b) => a.Date.CompareTo(b.Date));
            model.Events.Sort((a, b) => a.Date.CompareTo(b.Date));
            if (model.Daily.Count < 2 || model.Anchors.Count < 2)
                throw new InvalidDataException("Expected at least two daily closes and anchors.");
            model.Start = model.Daily[0].Date;
            model.End = model.Daily[model.Daily.Count - 1].Date;
            model.InitialMonth = MonthCoordinate(new DateTime(model.Start.Year, model.Start.Month, 1));
            if (model.Start != model.Anchors[0].Date || model.End != model.Anchors[model.Anchors.Count - 1].Date)
                throw new InvalidDataException("Anchors must start and end on the daily series endpoints.");
            for (int i = 1; i < model.Daily.Count; i++)
                if (model.Daily[i].Date <= model.Daily[i - 1].Date)
                    throw new InvalidDataException("Daily dates must be unique.");
            for (int i = 0; i < model.Daily.Count; i++)
                model.Daily[i].Slope = Derivative(model.Daily, i);
            for (int i = 1; i < model.Anchors.Count; i++)
                if (model.Anchors[i].Date <= model.Anchors[i - 1].Date)
                    throw new InvalidDataException("Anchor dates must be unique.");
            for (int i = 0; i < model.Anchors.Count; i++)
                model.Anchors[i].Slope = Derivative(model.Anchors, i);
            double low = model.Daily.Min(point => point.Close);
            double high = model.Daily.Max(point => point.Close);
            double margin = (high - low) * 0.1;
            model.OverviewMin = Math.Max(0.0, low - margin);
            model.OverviewMax = high + Math.Max(margin, high * 0.001);
            model.MinimumRange = Math.Max(high * 0.01, (high - low) * 0.12);
            if (root.TryGetProperty("display", out JsonElement display))
            {
                model.Title = ReadText(display, "title");
                model.Subtitle = ReadText(display, "subtitle");
                model.InstrumentLabel = ReadText(display, "instrument_label");
                model.Source = ReadText(display, "source");
                model.RangeLabel = ReadText(display, "range_label");
                model.Ending = ReadText(display, "ending");
                model.Unit = ReadText(display, "unit");
            }
            if (String.IsNullOrWhiteSpace(model.Title)) model.Title = model.InstrumentLabel;
            if (String.IsNullOrWhiteSpace(model.RangeLabel))
                model.RangeLabel = model.Start.ToString("yyyy-MM-dd") + " — " + model.End.ToString("yyyy-MM-dd");
            if (String.IsNullOrWhiteSpace(model.Ending)) model.Ending = model.Title;
            foreach (Event ev in model.Events)
            {
                int dailyIndex = UpperBound(model.Daily, ev.Date) - 1;
                if (dailyIndex < 0 || model.Daily[dailyIndex].Date != ev.Date ||
                    Math.Abs(model.Daily[dailyIndex].Close - ev.Close) > 0.005)
                    throw new InvalidDataException("Event close does not match daily data: " + ev.Date.ToString("yyyy-MM-dd"));
            }
            double departure = 3.0;
            foreach (Event ev in model.Events)
            {
                if (!Double.IsFinite(ev.Arrival) || !Double.IsFinite(ev.Hold) ||
                    ev.Hold < 0 || ev.Arrival < departure || ev.Arrival + ev.Hold > 218.0)
                    throw new InvalidDataException("Event arrival/hold must be ordered within 3..218 seconds.");
                departure = ev.Arrival + ev.Hold;
            }
            return model;
        }
    }

    private static string ReadText(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out JsonElement text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? "" : "";
    }

    private static DateTime ReadDate(JsonElement value)
    {
        return DateTime.ParseExact(value.GetString() ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static Sample ReadSample(JsonElement item)
    {
        DateTime date = ReadDate(item.GetProperty("date"));
        double close = item.GetProperty("close").GetDouble();
        if (!Double.IsFinite(close) || close <= 0.0)
            throw new InvalidDataException("Invalid close at " + date.ToString("yyyy-MM-dd"));
        return new Sample { Date = date, Month = MonthCoordinate(date), Close = close };
    }

    private static double MonthCoordinate(DateTime date)
    {
        return date.Year * 12 + date.Month - 1 +
            ((date.Day - 1) + date.TimeOfDay.TotalDays) / DateTime.DaysInMonth(date.Year, date.Month);
    }

    private static int UpperBound(List<Sample> points, DateTime date)
    {
        int low = 0, high = points.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (points[middle].Date <= date) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int LowerBoundMonth(List<Sample> points, double month)
    {
        int low = 0, high = points.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (points[middle].Month < month) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static DateTime ProgressAt(Model model, double time)
    {
        if (time <= 3.0) return model.Start;
        if (time >= 218.0) return model.End;
        double departure = 3.0;
        DateTime from = model.Start;
        foreach (Event ev in model.Events)
        {
            if (time < ev.Arrival)
                return EasedProgress(from, ev.Date, time - departure, ev.Arrival - departure);
            if (time < ev.Arrival + ev.Hold) return ev.Date;
            departure = ev.Arrival + ev.Hold;
            from = ev.Date;
        }
        return EasedProgress(from, model.End, time - departure, 218.0 - departure);
    }

    private static DateTime EasedProgress(DateTime from, DateTime to, double elapsed, double duration)
    {
        if (elapsed <= 0.0) return from;
        if (elapsed >= duration) return to;
        double ease = Math.Min(0.35, duration * 0.25);
        double area;
        if (elapsed < ease)
            area = elapsed / 2.0 - ease * Math.Sin(Math.PI * elapsed / ease) / (2.0 * Math.PI);
        else if (elapsed > duration - ease)
        {
            double remaining = duration - elapsed;
            area = duration - ease - remaining / 2.0 +
                ease * Math.Sin(Math.PI * remaining / ease) / (2.0 * Math.PI);
        }
        else area = elapsed - ease / 2.0;
        return from.AddTicks((long)Math.Round((to - from).Ticks * area / (duration - ease)));
    }

    private static FrameState[] BuildStates(Model model)
    {
        FrameState[] states = new FrameState[FrameCount];
        double previousMin = Double.NaN, previousMax = Double.NaN;
        double alpha = 1.0 - Math.Exp(-1.0 / (Fps * 0.7));
        for (int frame = 0; frame < FrameCount; frame++)
        {
            double time = (double)frame / Fps;
            DateTime progress = ProgressAt(model, time);
            double headMonth = MonthCoordinate(progress);
            // The visible quote always comes from the most recent actual trading day.
            // Camera motion stays continuous while no future quote is rendered.
            int quoteIndex = Math.Max(0, UpperBound(model.Daily, progress) - 1);
            Sample quote = model.Daily[quoteIndex];
            double headClose = quote.Close;
            double camera = Math.Max(model.InitialMonth, headMonth - 11.25);
            int first = Math.Max(0, LowerBoundMonth(model.Daily, camera) - 1);
            int last = Math.Max(first, UpperBound(model.Daily, progress) - 1);
            double min = headClose, max = headClose;
            for (int i = first; i <= last; i++)
            {
                Sample point = model.Daily[i];
                if (point.Month >= camera && point.Month <= camera + 12.0)
                {
                    min = Math.Min(min, point.Close);
                    max = Math.Max(max, point.Close);
                }
            }
            if (first + 1 < model.Daily.Count && model.Daily[first].Month < camera &&
                model.Daily[first + 1].Month > camera)
            {
                double crossing = CurveValue(model.Daily, first, camera);
                min = Math.Min(min, crossing);
                max = Math.Max(max, crossing);
            }
            double range = Math.Max(model.MinimumRange, (max - min) * 1.2);
            double center = (min + max) / 2.0;
            double targetMin = Math.Max(0.0, center - range / 2.0);
            double targetMax = Math.Max(center + range / 2.0, targetMin + model.MinimumRange);
            if (Double.IsNaN(previousMin))
            {
                previousMin = targetMin;
                previousMax = targetMax;
            }
            else
            {
                previousMin = targetMin < previousMin ? targetMin : Lerp(previousMin, targetMin, alpha);
                previousMax = targetMax > previousMax ? targetMax : Lerp(previousMax, targetMax, alpha);
            }
            states[frame] = new FrameState {
                Progress = progress, Head = quote.Date, HeadClose = headClose,
                CameraLeft = camera, YMin = previousMin, YMax = previousMax
            };
        }
        return states;
    }

    private static double CloseAt(List<Sample> points, DateTime time, double month)
    {
        int index = Math.Max(0, UpperBound(points, time) - 1);
        if (index >= points.Count - 1 || points[index].Date == time)
            return points[index].Close;
        return CurveValue(points, index, month);
    }

    private static double Lerp(double a, double b, double factor)
    {
        return a + (b - a) * factor;
    }

    private static double Derivative(List<Sample> points, int index)
    {
        if (index <= 0 || index >= points.Count - 1) return 0.0;
        double h0 = points[index].Month - points[index - 1].Month;
        double h1 = points[index + 1].Month - points[index].Month;
        if (h0 <= 0.0 || h1 <= 0.0) return 0.0;
        double s0 = (points[index].Close - points[index - 1].Close) / h0;
        double s1 = (points[index + 1].Close - points[index].Close) / h1;
        if (s0 * s1 <= 0.0) return 0.0;
        double w0 = 2.0 * h1 + h0;
        double w1 = h1 + 2.0 * h0;
        return (w0 + w1) / (w0 / s0 + w1 / s1);
    }

    private static double CurveValue(List<Sample> points, int index, double month)
    {
        Sample a = points[index], b = points[index + 1];
        double span = b.Month - a.Month;
        if (span <= 0.0) return b.Close;
        double u = Math.Max(0.0, Math.Min(1.0, (month - a.Month) / span));
        double u2 = u * u, u3 = u2 * u;
        double m0 = points[index].Slope, m1 = points[index + 1].Slope;
        return (2 * u3 - 3 * u2 + 1) * a.Close + (u3 - 2 * u2 + u) * span * m0
             + (-2 * u3 + 3 * u2) * b.Close + (u3 - u2) * span * m1;
    }

    private static Bitmap BuildBackground()
    {
        Bitmap image = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(image))
        using (LinearGradientBrush gradient = new LinearGradientBrush(new Point(0, 0),
            new Point(0, Height), ColorTranslator.FromHtml("#0F172A"), ColorTranslator.FromHtml("#1E293B")))
        using (Pen grid = new Pen(Color.FromArgb(51, 188, 197, 211), 1f))
        {
            g.FillRectangle(gradient, 0, 0, Width, Height);
            for (int x = 0; x <= Width; x += 48) g.DrawLine(grid, x, 0, x, Height);
            for (int y = 0; y <= Height; y += 48) g.DrawLine(grid, 0, y, Width, y);
        }
        return image;
    }

    private static float X(double month, FrameState state)
    {
        return PlotLeft + (float)((month - state.CameraLeft) / 12.0) * (PlotRight - PlotLeft);
    }

    private static float Y(double close, FrameState state)
    {
        return PlotBottom - (float)((close - state.YMin) / (state.YMax - state.YMin)) * (PlotBottom - PlotTop);
    }

    private static Bitmap RenderFrame(Model model, FrameState state, int frame, Bitmap background,
        Font axisFont, Font yearFont, Font titleFont, Font panelLabelFont, Font valueFont,
        Font eventFont, Font endingFont, float contentScale)
    {
        double time = (double)frame / Fps;
        Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImageUnscaled(background, 0, 0);
            GraphicsState original = g.Save();
            if (contentScale < 1f)
            {
                float left = Width * (1f - contentScale) / 2f;
                float top = Height * (1f - contentScale) / 2f;
                using (Matrix transform = new Matrix(contentScale, 0f, 0f, contentScale, left, top))
                    g.Transform = transform;
            }
            if (time >= 218.0)
                DrawOverview(g, model);
            else
            {
                DrawAxes(g, model, state, axisFont, yearFont);
                List<PointF> line = time >= 3.0 ? DrawLine(g, model.Daily, state) : new List<PointF>();
                if (time >= 3.0) DrawEvents(g, model, state, time, line, eventFont);
            }
            DrawHeader(g, model, state.HeadClose, titleFont, panelLabelFont, valueFont);
            DrawFooter(g, model);
            if (time >= 218.0) DrawEnding(g, model, time, endingFont);
            g.Restore(original);
            if (time < 3.0)
            {
                int opacity = (int)Math.Round(255.0 * (1.0 - time / 3.0));
                using (Brush fade = new SolidBrush(Color.FromArgb(opacity, 0, 0, 0)))
                    g.FillRectangle(fade, 0, 0, Width, Height);
            }
        }
        return bitmap;
    }

    private static PointF OverviewPoint(Model model, double month, double close)
    {
        double first = model.Anchors[0].Month;
        double last = model.Anchors[model.Anchors.Count - 1].Month;
        return new PointF(
            PlotLeft + (float)((month - first) / (last - first)) * (PlotRight - PlotLeft),
            PlotBottom - (float)((close - model.OverviewMin) /
                (model.OverviewMax - model.OverviewMin)) * (PlotBottom - PlotTop));
    }

    private static void DrawOverview(Graphics g, Model model)
    {
        using (Pen axis = new Pen(Color.FromArgb(236, 255, 255, 255), 2f))
        {
            g.DrawLine(axis, PlotLeft, PlotTop, PlotLeft, PlotBottom);
            g.DrawLine(axis, PlotLeft, PlotBottom, PlotRight, PlotBottom);
        }

        List<PointF[]> segments = new List<PointF[]>(model.Anchors.Count - 1);
        List<Color> colors = new List<Color>(model.Anchors.Count - 1);
        for (int i = 0; i < model.Anchors.Count - 1; i++)
        {
            Sample a = model.Anchors[i], b = model.Anchors[i + 1];
            PointF start = OverviewPoint(model, a.Month, a.Close);
            PointF end = OverviewPoint(model, b.Month, b.Close);
            int steps = Math.Max(2, (int)Math.Ceiling((end.X - start.X) / 3f));
            PointF[] segment = new PointF[steps + 1];
            for (int j = 0; j <= steps; j++)
            {
                double month = Lerp(a.Month, b.Month, (double)j / steps);
                segment[j] = OverviewPoint(model, month, CurveValue(model.Anchors, i, month));
            }
            segments.Add(segment);
            colors.Add(b.Close >= a.Close ? Red : Green);
        }

        GraphicsState clipping = g.Save();
        g.SetClip(new RectangleF(PlotLeft, PlotTop, PlotRight - PlotLeft, PlotBottom - PlotTop));
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                using (Pen pen = new Pen(pass == 0 ? Color.FromArgb(80, colors[i]) : colors[i],
                    pass == 0 ? 8f : 4f))
                {
                    pen.LineJoin = LineJoin.Round;
                    pen.StartCap = i == 0 ? LineCap.Round : LineCap.Flat;
                    pen.EndCap = i == segments.Count - 1 ? LineCap.Round : LineCap.Flat;
                    g.DrawLines(pen, segments[i]);
                }
            }
        }
        g.Restore(clipping);
    }

    private static Bitmap RenderCover(Model model, Bitmap background, float contentScale)
    {
        Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        using (Font title = new Font("Microsoft YaHei", 82f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font subtitle = new Font("Microsoft YaHei", 56f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font details = new Font("Microsoft YaHei", 31f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush white = new SolidBrush(Color.White))
        using (Brush gray = new SolidBrush(TextGray))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center })
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImageUnscaled(background, 0, 0);
            if (contentScale < 1f)
            {
                float left = Width * (1f - contentScale) / 2f;
                float top = Height * (1f - contentScale) / 2f;
                using (Matrix transform = new Matrix(contentScale, 0f, 0f, contentScale, left, top))
                    g.Transform = transform;
            }
            DrawOverview(g, model);
            g.DrawString(model.Title, title, white, new RectangleF(42f, 56f, 996f, 112f), center);
            g.DrawString(model.Subtitle, subtitle, white, new RectangleF(42f, 168f, 996f, 80f), center);
            g.DrawString(model.InstrumentLabel + " · " + model.RangeLabel, details, gray,
                new RectangleF(42f, 266f, 996f, 54f), center);
        }
        return bitmap;
    }

    private static double NiceStep(double approximate)
    {
        double power = Math.Pow(10.0, Math.Floor(Math.Log10(approximate)));
        foreach (double unit in new double[] { 1, 2, 2.5, 5, 10 })
            if (unit * power >= approximate) return unit * power;
        return 10.0 * power;
    }

    private static void DrawAxes(Graphics g, Model model, FrameState state, Font monthFont, Font yearFont)
    {
        using (Pen axis = new Pen(Color.FromArgb(236, 255, 255, 255), 2f))
        using (Pen tick = new Pen(Color.FromArgb(180, 236, 241, 249), 2f))
        using (Brush label = new SolidBrush(TextGray))
        using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center })
        using (StringFormat right = new StringFormat { Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center })
        {
            g.DrawLine(axis, PlotLeft, PlotTop, PlotLeft, PlotBottom);
            g.DrawLine(axis, PlotLeft, PlotBottom, PlotRight, PlotBottom);
            int firstMonth = (int)Math.Ceiling(state.CameraLeft);
            int lastMonth = (int)Math.Floor(state.CameraLeft + 12.0);
            for (int index = firstMonth; index <= lastMonth; index++)
            {
                float x = X(index, state);
                int year = index / 12, month = index % 12 + 1;
                bool january = month == 1;
                g.DrawLine(tick, x, PlotBottom, x, PlotBottom + (january ? 18f : 11f));
                float monthLeft = Math.Max(16f, Math.Min(Width - 30f - 66f, x - 33f));
                g.DrawString(month.ToString("D2", CultureInfo.InvariantCulture), monthFont, label,
                    new RectangleF(monthLeft, PlotBottom + 20f, 66f, 49f), centered);
                if (january)
                {
                    float yearLeft = Math.Max(16f, Math.Min(Width - 24f - 130f, x - 65f));
                    g.DrawString(year.ToString(CultureInfo.InvariantCulture), yearFont, label,
                        new RectangleF(yearLeft, PlotBottom + 65f, 130f, 53f), centered);
                }
            }
            double step = NiceStep((state.YMax - state.YMin) / 5.0);
            double first = Math.Ceiling(state.YMin / step) * step;
            for (double value = first; value <= state.YMax + 0.001; value += step)
            {
                float y = Y(value, state);
                if (y < PlotTop + 5f || y > PlotBottom - 5f) continue;
                g.DrawLine(tick, PlotLeft - 14f, y, PlotLeft, y);
                g.DrawString(value.ToString("N0", CultureInfo.InvariantCulture), monthFont, label,
                    new RectangleF(3f, y - 26f, PlotLeft - 24f, 52f), right);
            }
            g.DrawString(model.Unit, monthFont, label, new PointF(22f, PlotTop - 65f));
        }
    }

    private static List<PointF> DrawLine(Graphics g, List<Sample> points, FrameState state)
    {
        List<PointF> shapePoints = new List<PointF>(650);
        List<PointF[]> segments = new List<PointF[]>(300);
        List<Color> colors = new List<Color>(300);
        List<bool> tips = new List<bool>(300);
        GraphicsState clipping = g.Save();
        g.SetClip(new RectangleF(PlotLeft, PlotTop, PlotRight - PlotLeft, PlotBottom - PlotTop));
        double headMonth = MonthCoordinate(state.Head);
        int first = Math.Max(0, LowerBoundMonth(points, state.CameraLeft) - 1);
        int last = Math.Min(points.Count - 2, Math.Max(first, UpperBound(points, state.Head) - 1));
        for (int i = first; i <= last; i++)
        {
            double begin = Math.Max(state.CameraLeft, points[i].Month);
            double end = Math.Min(Math.Min(state.CameraLeft + 12.0, headMonth), points[i + 1].Month);
            if (begin >= end) continue;
            int steps = Math.Max(2, (int)Math.Ceiling((X(end, state) - X(begin, state)) / 4.0));
            PointF[] segment = new PointF[steps + 1];
            for (int j = 0; j <= steps; j++)
            {
                double month = Lerp(begin, end, (double)j / steps);
                segment[j] = new PointF(X(month, state), Y(CurveValue(points, i, month), state));
                shapePoints.Add(segment[j]);
            }
            Color color = points[i + 1].Close >= points[i].Close ? Red : Green;
            segments.Add(segment);
            colors.Add(color);
            tips.Add(end >= headMonth - 1e-9);
        }
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                Color color = pass == 0 ? Color.FromArgb(80, colors[i]) : colors[i];
                using (Pen pen = new Pen(color, pass == 0 ? 8f : 3f))
                {
                    pen.LineJoin = LineJoin.Round;
                    pen.StartCap = LineCap.Flat;
                    pen.EndCap = tips[i] ? LineCap.Round : LineCap.Flat;
                    g.DrawLines(pen, segments[i]);
                }
            }
        }
        if (shapePoints.Count == 0)
        {
            float x = X(headMonth, state), y = Y(state.HeadClose, state);
            Color color = points.Count > 1 && points[1].Close < points[0].Close ? Green : Red;
            using (Brush glow = new SolidBrush(Color.FromArgb(80, color)))
            using (Brush ink = new SolidBrush(color))
            {
                g.FillEllipse(glow, x - 4f, y - 4f, 8f, 8f);
                g.FillEllipse(ink, x - 1.5f, y - 1.5f, 3f, 3f);
            }
        }
        g.Restore(clipping);
        return shapePoints;
    }

    private static RectangleF PlaceBubble(Graphics g, Event ev, FrameState state,
        Font font, List<PointF> line, List<RectangleF> occupied)
    {
        SizeF text = g.MeasureString(ev.Label, font);
        float width = Math.Min(PlotRight - PlotLeft - 25f, text.Width + 42f);
        float height = Math.Max(70f, text.Height + 18f);
        float x = X(ev.Month, state), y = Y(ev.Close, state);
        RectangleF winner = RectangleF.Empty;
        double best = Double.PositiveInfinity;
        foreach (bool above in new bool[] { true, false })
        {
            foreach (float gap in new float[] { 50f, 120f, 210f, 320f })
            {
                float top = above ? y - height - gap : y + gap;
                top = Math.Max(PlotTop + 12f, Math.Min(PlotBottom - height - 12f, top));
                foreach (float offset in new float[] { 0f, -0.4f, 0.4f, 0.6f, -0.6f, 0.9f, -0.9f })
                {
                    float left = x - width * (0.5f + offset);
                    left = Math.Max(PlotLeft + 9f, Math.Min(PlotRight - width - 9f, left));
                    RectangleF candidate = new RectangleF(left, top, width, height);
                    RectangleF padded = RectangleF.Inflate(candidate, 12f, 12f);
                    double penalty = 0.0;
                    foreach (PointF point in line)
                        if (padded.Contains(point)) { penalty += 1000000.0; break; }
                    foreach (RectangleF used in occupied)
                        if (padded.IntersectsWith(used)) { penalty += 1000000000.0; break; }
                    penalty += Math.Abs((left + width / 2f) - x) / 8.0;
                    penalty += Math.Abs((above ? candidate.Bottom : candidate.Top) - y) / 20.0;
                    if (penalty < best) { best = penalty; winner = candidate; }
                }
            }
        }
        return winner;
    }

    private static void AppendFuturePath(List<PointF> line, List<Sample> points, FrameState state)
    {
        double headMonth = MonthCoordinate(state.Head);
        int first = Math.Max(0, UpperBound(points, state.Head) - 1);
        for (int i = first; i < points.Count - 1; i++)
        {
            double begin = Math.Max(headMonth, points[i].Month);
            double end = Math.Min(state.CameraLeft + 12.0, points[i + 1].Month);
            if (begin >= end) continue;
            int steps = Math.Max(2, (int)Math.Ceiling((X(end, state) - X(begin, state)) / 4.0));
            for (int j = 0; j <= steps; j++)
            {
                double month = Lerp(begin, end, (double)j / steps);
                line.Add(new PointF(X(month, state), Y(CurveValue(points, i, month), state)));
            }
        }
    }

    private static void DrawEvents(Graphics g, Model model, FrameState state, double time,
        List<PointF> line, Font font)
    {
        List<Event> visible = model.Events.Where(ev => ev.Arrival <= time &&
            ev.Month >= state.CameraLeft && ev.Month <= state.CameraLeft + 12.0).ToList();
        if (visible.Count == 0) return;
        if (visible.Any(ev => ev.Date == model.Events[model.Events.Count - 1].Date))
            AppendFuturePath(line, model.Daily, state);
        List<RectangleF> occupied = new List<RectangleF>();
        for (int i = 0; i < visible.Count; i++)
        {
            Event ev = visible[i];
            float x = X(ev.Month, state), y = Y(ev.Close, state);
            if (x < PlotLeft + 5f || x > PlotRight - 5f) continue;
            using (Brush dot = new SolidBrush(Color.White))
                g.FillEllipse(dot, x - 5f, y - 5f, 10f, 10f);
            bool collapse = i < visible.Count - 1 &&
                visible[visible.Count - 1].Month - ev.Month < 6.0;
            if (collapse) continue;
            RectangleF bubble = PlaceBubble(g, ev, state, font, line, occupied);
            occupied.Add(bubble);
            float entry = (float)Math.Min(1.0, (time - ev.Arrival) / 0.45);
            float eased = 1f - (float)Math.Pow(1f - entry, 3f);
            int alpha = Math.Max(0, Math.Min(255, (int)(255f * eased)));
            bubble.Y += y > bubble.Bottom ? -12f * (1f - eased) : 12f * (1f - eased);
            using (Brush shadow = new SolidBrush(Color.FromArgb((int)(75f * eased), 0, 0, 0)))
            using (Brush fill = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
            using (Brush ink = new SolidBrush(Color.FromArgb(alpha, 24, 31, 44)))
            {
                using (GraphicsPath path = RoundedRect(new RectangleF(bubble.X + 3f, bubble.Y + 5f,
                    bubble.Width, bubble.Height), 8f)) g.FillPath(shadow, path);
                using (GraphicsPath path = RoundedRect(bubble, 8f)) g.FillPath(fill, path);
                g.DrawString(ev.Label, font, ink,
                    new RectangleF(bubble.X + 19f, bubble.Y + 8f, bubble.Width - 33f, bubble.Height - 12f));
            }
        }
    }

    private static void DrawHeader(Graphics g, Model model, double close, Font titleFont, Font labelFont, Font valueFont)
    {
        RectangleF panel = new RectangleF(20f, 20f, 390f, 165f);
        using (Brush shadow = new SolidBrush(Color.FromArgb(85, 0, 0, 0)))
        using (Brush fill = new SolidBrush(Color.FromArgb(210, 28, 28, 30)))
        using (Brush white = new SolidBrush(Color.White))
        using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center })
        {
            using (GraphicsPath path = RoundedRect(new RectangleF(23f, 25f, panel.Width, panel.Height), 8f))
                g.FillPath(shadow, path);
            using (GraphicsPath path = RoundedRect(panel, 8f)) g.FillPath(fill, path);
            DrawFit(g, model.InstrumentLabel, labelFont, white, new RectangleF(38f, 31f, 355f, 49f), null);
            g.DrawString(Math.Round(close).ToString("N0", CultureInfo.InvariantCulture), valueFont, white,
                new RectangleF(35f, 79f, panel.Width - 28f, 85f));
            DrawFit(g, model.Title, titleFont, white,
                new RectangleF(85f, 203f, 910f, 90f), centered);
        }
    }

    private static void DrawEnding(Graphics g, Model model, double time, Font font)
    {
        float entrance = (float)Math.Min(1.0, (time - 218.0) / 0.45);
        int alpha = Math.Max(0, Math.Min(255, (int)(255f * entrance)));
        using (Brush shadow = new SolidBrush(Color.FromArgb((int)(140f * entrance), 0, 0, 0)))
        using (Brush white = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
        using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center })
        {
            RectangleF area = new RectangleF(28f, 1704f - 18f * (1f - entrance), 1024f, 100f);
            RectangleF shade = new RectangleF(area.X + 2f, area.Y + 4f, area.Width, area.Height);
            DrawFit(g, model.Ending, font, shadow, shade, centered);
            DrawFit(g, model.Ending, font, white, area, centered);
        }
    }

    private static void DrawFit(Graphics g, string text, Font font, Brush brush, RectangleF area, StringFormat format)
    {
        float size = font.Size;
        while (size > 12f)
        {
            using (Font fitted = new Font(font.FontFamily, size, font.Style, GraphicsUnit.Pixel))
            {
                if (g.MeasureString(text, fitted).Width <= area.Width)
                {
                    g.DrawString(text, fitted, brush, area, format ?? StringFormat.GenericDefault);
                    return;
                }
            }
            size -= 1f;
        }
        using (Font fitted = new Font(font.FontFamily, 12f, font.Style, GraphicsUnit.Pixel))
            g.DrawString(text, fitted, brush, area, format ?? StringFormat.GenericDefault);
    }

    private static void DrawFooter(Graphics g, Model model)
    {
        using (Font font = new Font("Microsoft YaHei", 25f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush brush = new SolidBrush(Color.FromArgb(190, TextGray)))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center })
        {
            DrawFit(g, model.RangeLabel, font, brush, new RectangleF(85f, 1595f, 910f, 44f), center);
            DrawFit(g, model.Source, font, brush, new RectangleF(85f, 1639f, 910f, 44f), center);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2f;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    private static int[] ParseFrames(string text)
    {
        List<int> frames = new List<int>();
        foreach (string token in text.Split(','))
        {
            int frame = Int32.Parse(token.Trim(), CultureInfo.InvariantCulture);
            if (frame < 0 || frame >= FrameCount)
                throw new ArgumentOutOfRangeException("frameList", "Frames must be within 0..6599.");
            if (!frames.Contains(frame)) frames.Add(frame);
        }
        return frames.ToArray();
    }

    private static void EncodeFrames(Model model, FrameState[] states, int startFrame, int count, string output,
        string ffmpeg, string encoder, Bitmap background, Font axisFont, Font yearFont,
        Font titleFont, Font panelLabelFont, Font valueFont, Font eventFont, Font endingFont,
        float contentScale)
    {
        using (Process process = new Process())
        {
            ProcessStartInfo start = new ProcessStartInfo(ffmpeg) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true
            };
            string[] args = new string[] {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", "1080x1920",
                "-framerate", "30", "-i", "pipe:0", "-map", "0:v:0",
                "-frames:v", count.ToString(CultureInfo.InvariantCulture),
                "-c:v", encoder, "-pix_fmt", "yuv420p", "-r", "30", "-fps_mode", "cfr"
            };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            if (encoder == "h264_nvenc")
            {
                start.ArgumentList.Add("-preset"); start.ArgumentList.Add("p5");
                start.ArgumentList.Add("-b:v"); start.ArgumentList.Add("12M");
                start.ArgumentList.Add("-maxrate"); start.ArgumentList.Add("18M");
                start.ArgumentList.Add("-bufsize"); start.ArgumentList.Add("24M");
            }
            else
            {
                start.ArgumentList.Add("-preset"); start.ArgumentList.Add("fast");
                start.ArgumentList.Add("-crf"); start.ArgumentList.Add("18");
                start.ArgumentList.Add("-threads"); start.ArgumentList.Add("2");
            }
            start.ArgumentList.Add("-an");
            start.ArgumentList.Add("-movflags"); start.ArgumentList.Add("+faststart");
            start.ArgumentList.Add(output);
            process.StartInfo = start;
            StringBuilder errors = new StringBuilder();
            process.ErrorDataReceived += (sender, e) => {
                if (e.Data != null) lock (errors) errors.AppendLine(e.Data);
            };
            if (!process.Start()) throw new IOException("Could not start FFmpeg.");
            process.BeginErrorReadLine();
            byte[] bytes = new byte[Width * Height * 4];
            try
            {
                Stream stdin = process.StandardInput.BaseStream;
                using (StreamWriter stateLog = new StreamWriter(Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".", "frame_states.jsonl"), false,
                    new UTF8Encoding(false)))
                for (int offset = 0; offset < count; offset++)
                {
                    int frame = startFrame + offset;
                    using (Bitmap bitmap = RenderFrame(model, states[frame], frame, background,
                        axisFont, yearFont, titleFont, panelLabelFont, valueFont, eventFont,
                        endingFont, contentScale))
                    {
                        BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, Width, Height),
                            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        try
                        {
                            if (locked.Stride != Width * 4)
                                throw new InvalidDataException("Unexpected bitmap stride.");
                            Marshal.Copy(locked.Scan0, bytes, 0, bytes.Length);
                        }
                        finally { bitmap.UnlockBits(locked); }
                    }
                    stdin.Write(bytes, 0, bytes.Length);
                    FrameState state = states[frame];
                    stateLog.WriteLine(JsonSerializer.Serialize(new {
                        frame = frame, time = (double)frame / Fps,
                        date = state.Head.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        close = state.HeadClose,
                        visible_last_date = state.Head.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        progress = state.Progress.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                        camera_left = state.CameraLeft, y_min = state.YMin, y_max = state.YMax
                    }));
                    if ((offset + 1) % 300 == 0 || offset + 1 == count)
                        Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
                            "Rendered {0}/{1} frames ({2:0.0}%)", offset + 1, count,
                            100.0 * (offset + 1) / count));
                }
                stdin.Close();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new IOException("FFmpeg failed: " + errors.ToString());
                Console.WriteLine("Encoded " + output);
            }
            catch
            {
                try { process.StandardInput.Close(); } catch { }
                if (!process.HasExited) process.Kill();
                process.WaitForExit();
                if (errors.Length > 0) Console.Error.WriteLine(errors.ToString());
                throw;
            }
        }
    }
}
'@

Add-Type -ReferencedAssemblies @(
    'System.Drawing.Common',
    'System.Drawing.Primitives',
    'System.Text.Json',
    'System.Runtime',
    'System.Linq',
    'System.Collections',
    'System.Console',
    'System.Memory',
    'System.Diagnostics.Process',
    'System.ComponentModel.Primitives',
    'System.Threading',
    'System.Text.Encoding.Extensions',
    'System.Private.Windows.GdiPlus',
    'System.Private.Windows.Core'
) -TypeDefinition $source

$dataPath = (Resolve-Path -LiteralPath $Data).Path
$ffmpegPath = if ($FrameList -or $CoverOnly) { '' } else { (Resolve-Path -LiteralPath $FFmpeg).Path }
[ReusableClassicRenderer]::Run($dataPath, [System.IO.Path]::GetFullPath($Output), $FrameList, $SampleDir, $CoverOutput, [bool]$CoverOnly, $StartFrame, $RenderFrames, $ffmpegPath, $Encoder, $ContentScale)
