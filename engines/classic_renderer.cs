using System;
using System.Collections.Generic;
using System.Diagnostics;
#if SKIA
using StockVideo.Drawing;
using Color = System.Drawing.Color;
using ColorTranslator = System.Drawing.ColorTranslator;
using Point = System.Drawing.Point;
using PointF = System.Drawing.PointF;
using SizeF = System.Drawing.SizeF;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
#else
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
#endif
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

public static class ReusableClassicRenderer
{
    public static double RenderScale = 1;
    private static int OutputWidth => (int)Math.Round(Width * RenderScale);
    private static int OutputHeight => (int)Math.Round(Height * RenderScale);
    private const int Width = 1080;
    private const int Height = 1920;
    private const int Fps = 30;
    private const int FrameCount = 6600;
    private const float PlotLeft = 234f;
    private const float PlotRight = 900f;
    private const float PlotTop = 520f;
    private const float PlotBottom = 1090f;
    private static readonly Color Red = ColorTranslator.FromHtml("#FFF0D1");
    private static readonly Color Green = ColorTranslator.FromHtml("#D7BD80");
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

    private sealed class ImpactCue
    {
        public double Time, Close;
        public DateTime Date;
        public string Title = "", Detail = "";
        public bool Key, Down;
    }

    private sealed class Model
    {
        public List<Sample> Daily = new List<Sample>();
        public List<Sample> Anchors = new List<Sample>();
        public List<Event> Events = new List<Event>();
        public List<ImpactCue> Impacts = new List<ImpactCue>();
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
        public bool StellarMotion;
        public bool PlanetaryMotion;
        public bool NebulaMotion;
        public bool ReadableChartMotion;
        public string IntroQuestion = "";
    }

    private struct FrameState
    {
        public DateTime Progress;
        public DateTime Head;
        public double HeadClose;
        public double CameraLeft;
        public double CameraSpan;
        public double YMin;
        public double YMax;
    }

    public static void Run(string dataPath, string output, string frameList, string sampleDir,
        string coverOutput, bool coverOnly, int startFrame, int renderFrames, string ffmpeg, string encoder,
        double contentScale)
    {
        Model model = ReadModel(dataPath);
        FrameState[] states = coverOnly ? Array.Empty<FrameState>() : BuildStates(model);
        if (!coverOnly)
        {
            BuildImpactCues(model, states);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "impact_cues.json"),
                JsonSerializer.Serialize(model.Impacts.Select(c => new { time=c.Time, date=c.Date.ToString("yyyy-MM-dd"), close=c.Close, title=c.Title, detail=c.Detail, key=c.Key })));
        }
        Console.WriteLine("Timeline: 6600 frames, 220 seconds; Cumulative history; fixed start, expanding time axis.");
        foreach (Event ev in model.Events)
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
                "EVENT {0:yyyy-MM-dd} at {1:0.000}s, hold {2:0}s: {3}",
                ev.Date, ev.Arrival, ev.Hold, ev.Label));

        using (Bitmap background = BuildBackground(model))
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
                model.ReadableChartMotion = ReadText(display, "motion_style") == "nebula_chart";
                model.NebulaMotion = model.ReadableChartMotion || ReadText(display, "motion_style") == "nebula";
                model.PlanetaryMotion = model.NebulaMotion || ReadText(display, "motion_style") == "planetary";
                model.StellarMotion = model.PlanetaryMotion || ReadText(display, "motion_style") == "stellar";
                model.IntroQuestion = ReadText(display, "intro_question");
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
            double camera = model.InitialMonth;
            // Keep every elapsed trading day visible; reserve 6% ahead of the cursor.
            double cameraSpan = Math.Max(12.0, (headMonth - camera) / 0.94);
            int first = Math.Max(0, LowerBoundMonth(model.Daily, camera) - 1);
            int last = Math.Max(first, UpperBound(model.Daily, progress) - 1);
            double min = headClose, max = headClose;
            for (int i = first; i <= last; i++)
            {
                Sample point = model.Daily[i];
                if (point.Month >= camera && point.Month <= camera + cameraSpan)
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
                CameraLeft = camera, CameraSpan = cameraSpan, YMin = previousMin, YMax = previousMax
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

    private static Bitmap BuildBackground(Model model)
    {
        if (model.NebulaMotion && !model.ReadableChartMotion) return BuildNebulaBackground();
        Bitmap image = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(image))
        using (LinearGradientBrush gradient = new LinearGradientBrush(new Point(0, 0),
            new Point(0, Height), ColorTranslator.FromHtml("#050914"), ColorTranslator.FromHtml("#101C30")))
        using (Pen grid = new Pen(Color.FromArgb(10, 131, 146, 167), 1f))
        {
            g.FillRectangle(gradient, 0, 0, Width, Height);
            for (int x = 0; x <= Width; x += 120) g.DrawLine(grid, x, 0, x, Height);
            for (int y = 0; y <= Height; y += 120) g.DrawLine(grid, 0, y, Width, y);
        }
        return image;
    }

    private static float X(double month, FrameState state)
    {
        return PlotLeft + (float)((month - state.CameraLeft) / state.CameraSpan) * (PlotRight - PlotLeft);
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
        Bitmap bitmap = new Bitmap(OutputWidth, OutputHeight, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.ScaleTransform((float)RenderScale, (float)RenderScale);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImageUnscaled(background, 0, 0);
            if (model.PlanetaryMotion && !model.NebulaMotion)
                using (Brush space = new SolidBrush(Color.FromArgb(3, 6, 15)))
                    g.FillRectangle(space, 0, 0, Width, Height);
            GraphicsState original = g.Save();
            if (contentScale < 1f)
            {
                float left = Width * (1f - contentScale) / 2f;
                float top = Height * (1f - contentScale) / 2f;
                using (Matrix transform = new Matrix(contentScale*(float)RenderScale, 0f, 0f, contentScale*(float)RenderScale, left*(float)RenderScale, top*(float)RenderScale))
                    g.Transform = transform;
            }
            {
                if (model.StellarMotion) DrawStarField(g, time, model.ReadableChartMotion ? 0.10 : 0.25);
                DrawAxes(g, model, state, axisFont, yearFont);
                List<PointF> line = time >= 3.0 ? DrawLine(g, model.Daily, state,
                    model.PlanetaryMotion && !model.ReadableChartMotion) : new List<PointF>();
                if (time >= 3.0)
                {
                    if (model.NebulaMotion) DrawNebulaHead(g, model, state, time);
                    else if (model.PlanetaryMotion) DrawPlanetaryHead(g, model, state, time);
                    else
                    {
                        DrawSparklers(g, model, state, time);
                        DrawImpact(g, model, state, time, false);
                        DrawEvents(g, model, state, time, line, eventFont);
                    }
                }
            }
            DrawHeader(g, model, state, time);
            DrawNavigator(g, model, state, time);
            DrawImpact(g, model, state, time, true);
            if (model.StellarMotion && !model.PlanetaryMotion && time >= 3 && time < 218)
                DrawStellarTransition(g, model, state, time);
            DrawFooter(g, model);
            if (time >= 218.0) DrawEnding(g, model, time, endingFont);
            g.Restore(original);
            if (time < 3.0)
            {
                if (model.NebulaMotion) DrawNebulaIntro(g, model, state, time, background);
                else if (model.PlanetaryMotion) DrawPlanetaryIntro(g, model, state, time);
                else if (model.StellarMotion) DrawStellarIntro(g, model, state, time);
                else DrawIntro(g, model, time);
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
        using (Pen axis = new Pen(Color.FromArgb(65, 131, 146, 167), 2f))
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
        using (Font font = new Font("Microsoft YaHei", 28f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush label = new SolidBrush(Color.FromArgb(215,189,128)))
        {
            g.DrawString(model.Start.Year.ToString(),font,label,new PointF(PlotLeft,PlotBottom+20));
            g.DrawString(model.End.Year.ToString(),font,label,new PointF(PlotRight-80,PlotBottom+20));
            foreach (Sample sample in new[] { model.Daily.OrderBy(p => p.Close).First(), model.Daily.OrderByDescending(p => p.Close).First() })
            {
                PointF point = OverviewPoint(model,sample.Month,sample.Close);
                string text = sample.Close.ToString("N2",CultureInfo.InvariantCulture);
                float x = Math.Max(PlotLeft,Math.Min(PlotRight-190,point.X-95));
                float y = Math.Max(PlotTop,point.Y-55);
                g.DrawString(text,font,label,new PointF(x,y));
            }
        }

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
        // A floating spatial composition: numerical scale remains in the actual quote,
        // with only two quiet reference levels and no chart frame or time ticks.
        if (model.PlanetaryMotion && !model.ReadableChartMotion)
        {
            using (Font font = new Font("Bahnschrift", 25, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush ink = new SolidBrush(Color.FromArgb(65, 131, 146, 167)))
            {
                double step = NiceStep((state.YMax-state.YMin)/2.0);
                for (double value=Math.Ceiling(state.YMin/step)*step;value<state.YMax;value+=step)
                    g.DrawString(value.ToString("N0",CultureInfo.InvariantCulture),font,ink,new PointF(112,Y(value,state)-15));
            }
            return;
        }
        using (Pen axis = new Pen(Color.FromArgb(65, 131, 146, 167), 2f))
        using (Pen tick = new Pen(Color.FromArgb(75, 131, 146, 167), 2f))
        using (Brush label = new SolidBrush(model.ReadableChartMotion ? Color.FromArgb(178, 190, 207) : TextGray))
        using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center })
        using (StringFormat right = new StringFormat { Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center })
        {
            g.DrawLine(axis, PlotLeft, PlotTop, PlotLeft, PlotBottom);
            g.DrawLine(axis, PlotLeft, PlotBottom, PlotRight, PlotBottom);
            int tickMonths = state.CameraSpan <= 24 ? 3 : state.CameraSpan <= 84 ? 12 :
                state.CameraSpan <= 168 ? 24 : state.CameraSpan <= 420 ? 60 : 120;
            int firstMonth = (int)Math.Ceiling(state.CameraLeft / tickMonths) * tickMonths;
            int lastMonth = (int)Math.Floor(Math.Min(state.CameraLeft + state.CameraSpan, MonthCoordinate(state.Head)));
            float previousLabelRight = state.CameraSpan > 420 ? PlotLeft + 80 : float.NegativeInfinity;
            for (int index = firstMonth; index <= lastMonth; index += tickMonths)
            {
                float x = X(index, state);
                if (state.CameraSpan > 420 && x < PlotLeft + 110) continue;
                int year = index / 12, month = index % 12 + 1;
                string text = tickMonths < 12 ? year.ToString() + "." + month.ToString("D2") : year.ToString();
                Font axisFont = tickMonths < 12 ? monthFont : yearFont;
                float boxLeft = Math.Max(96, Math.Min(740, x-80));
                float textWidth = g.MeasureString(text, axisFont, new SizeF(10000, 10000), centered).Width;
                float textLeft = boxLeft + (160 - textWidth) / 2;
                if (textLeft < previousLabelRight + 18) continue;
                previousLabelRight = textLeft + textWidth;
                g.DrawLine(tick, x, PlotBottom, x, PlotBottom + 14);
                g.DrawString(text, axisFont, label,
                    new RectangleF(boxLeft, PlotBottom+22, 160, 60), centered);
            }
            if (state.CameraSpan > 420)
                g.DrawString(model.Start.Year.ToString(), yearFont, label,
                    new RectangleF(PlotLeft-80, PlotBottom+22, 160, 60), centered);
            double step = NiceStep((state.YMax - state.YMin) / 5.0);
            double first = Math.Ceiling(state.YMin / step) * step;
            for (double value = first; value <= state.YMax + 0.001; value += step)
            {
                float y = Y(value, state);
                if (y < PlotTop + 5f || y > PlotBottom - 5f) continue;
                g.DrawLine(tick, PlotLeft - 14f, y, PlotRight, y);
                g.DrawString(value.ToString("N0", CultureInfo.InvariantCulture), monthFont, label,
                    new RectangleF(96f, y - 26f, PlotLeft - 96f - 24f, 52f), right);
            }
            g.DrawString(model.Unit, monthFont, label, new PointF(96f, PlotTop - 55f));
        }
    }

    // Keep first, last, minimum and maximum in each logical pixel column, in time order.
    // This preserves thin historical spikes while avoiding excessive overdraw as history compresses.
    private static List<PointF> ReducePixelColumns(List<PointF> points)
    {
        var result = new List<PointF>();
        for (int first = 0; first < points.Count;)
        {
            int end = first+1, min = first, max = first;
            int column = (int)Math.Floor(points[first].X);
            while (end < points.Count && (int)Math.Floor(points[end].X) == column)
            {
                if (points[end].Y < points[min].Y) min = end;
                if (points[end].Y > points[max].Y) max = end;
                end++;
            }
            foreach (int index in new[] { first, min, max, end-1 }.Distinct().OrderBy(i => i))
                result.Add(points[index]);
            first = end;
        }
        return result;
    }

    private static List<PointF> DrawLine(Graphics g, List<Sample> points, FrameState state, bool planetary = false)
    {
        List<PointF> shapePoints = new List<PointF>(650);
        GraphicsState clipping = g.Save();
        g.SetClip(new RectangleF(PlotLeft, PlotTop, PlotRight - PlotLeft, PlotBottom - PlotTop));
        double headMonth = MonthCoordinate(state.Head);
        int last = Math.Max(0, UpperBound(points, state.Head) - 1);
        for (int i = 0; i <= last; i++)
            shapePoints.Add(new PointF(X(points[i].Month, state), Y(points[i].Close, state)));
        shapePoints = ReducePixelColumns(shapePoints);
        if (shapePoints.Count > 1)
        {
            PointF[] area = shapePoints.Concat(new[] {
                new PointF(shapePoints[shapePoints.Count - 1].X, PlotBottom),
                new PointF(shapePoints[0].X, PlotBottom) }).ToArray();
            using (var fill = new LinearGradientBrush(new Point(0, (int)PlotTop),
                new Point(0, (int)PlotBottom), Color.FromArgb(34, 215, 189, 128), Color.FromArgb(0, 215, 189, 128)))
                if (!planetary) g.FillPolygon(fill, area);
            for (int pass = 0; pass < 4; pass++)
            {
                int[] alphas = planetary ? new[] { 8, 16, 45, 220 } : new[] { 12, 24, 65, 255 };
                float[] widths = planetary ? new[] { 18f, 9f, 4f, 1.9f } : new[] { 28f, 16f, 8f, 3.5f };
                using (Pen pen = new Pen(Color.FromArgb(alphas[pass], 255, 240, 209), widths[pass]))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, shapePoints.ToArray());
                }
            }
        }
        float hx = X(headMonth, state), hy = Y(state.HeadClose, state);
        if (planetary) { g.Restore(clipping); return shapePoints; }
        double seconds = (state.Progress - points[0].Date).TotalDays;
        float pulse = 20f + 6f * (float)Math.Sin(seconds * 0.12);
        using (Pen guide = new Pen(Color.FromArgb(45, 215, 189, 128), 1f))
            g.DrawLine(guide, hx, PlotTop, hx, PlotBottom);
        for (int ring = 3; ring >= 1; ring--)
        {
            float radius = pulse * ring / 2;
            using (Brush halo = new SolidBrush(Color.FromArgb(12 + (3-ring)*14, 215, 189, 128)))
                g.FillEllipse(halo, hx-radius, hy-radius, radius*2, radius*2);
        }
        using (Brush core = new SolidBrush(Color.White)) g.FillEllipse(core, hx-5, hy-5, 10, 10);
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

    // Stateless randomness and absolute birth times keep previews and render chunks identical.
    private static double SparkNoise(int id, int salt)
    {
        unchecked
        {
            uint value = (uint)(id * 747796405 + salt * 289133645 + 3185);
            value = (value ^ (value >> 16)) * 2246822519u;
            value = (value ^ (value >> 13)) * 3266489917u;
            return (value ^ (value >> 16)) / (double)uint.MaxValue;
        }
    }

    private static PointF SparkPosition(PointF origin, double age, double vx, double vy, double phase)
    {
        return new PointF(origin.X + (float)(vx*age + 5*(Math.Sin(phase+age*6)-Math.Sin(phase))),
            origin.Y + (float)(vy*age + 100*age*age));
    }

    private static void DrawSparklers(Graphics g, Model model, FrameState state, double time)
    {
        const int rate = 120;
        const double maxLife = 1.15;
        GraphicsState clip = g.Save();
        g.SetClip(new RectangleF(PlotLeft, PlotTop, PlotRight-PlotLeft, PlotBottom-PlotTop));
        int first = Math.Max(0, (int)Math.Ceiling((time-maxLife-3)*rate));
        int last = (int)Math.Floor((time-3)*rate);
        for (int id = first; id <= last; id++)
        {
            double born = 3 + id/(double)rate, age = time-born;
            bool burst = model.Events.Any(e => born >= e.Arrival && born < e.Arrival+0.28);
            int count = burst ? 3 : 1;
            DateTime birthDate = ProgressAt(model,born);
            Sample anchor = model.Daily[Math.Max(0,UpperBound(model.Daily,birthDate)-1)];
            // Reproject the birth quote as the history compresses; never attach old sparks to the new head.
            PointF origin = new PointF(X(anchor.Month,state),Y(anchor.Close,state));
            for (int n = 0; n < count; n++)
            {
                int seed = id*3+n;
                double life = 0.45+0.70*SparkNoise(seed,1);
                if (age < 0 || age >= life) continue;
                double angle = SparkNoise(seed,2)*Math.PI*2;
                double speed = (70+165*Math.Pow(SparkNoise(seed,3),0.65))*(burst?1.2:1);
                double vx = Math.Cos(angle)*speed, vy = Math.Sin(angle)*speed-55;
                double phase = SparkNoise(seed,4)*Math.PI*2;
                PointF pos = SparkPosition(origin,age,vx,vy,phase);
                PointF tail = SparkPosition(origin,Math.Max(0,age-0.045),vx,vy,phase);
                double remaining = 1-age/life;
                double fade = Math.Pow(remaining,1.35)*Math.Min(1,age/0.025);
                int alpha = Math.Max(0,Math.Min(255,(int)(245*fade)));
                if (alpha < 2) continue;
                float size = (float)((1.5+2.1*SparkNoise(seed,5))*(0.45+0.55*remaining));
                int green = (int)(105+135*remaining), blue = (int)(25+155*remaining*remaining);
                Color color = Color.FromArgb(alpha,255,green,blue);
                using (Pen trail = new Pen(Color.FromArgb(alpha/2,255,green,blue),Math.Max(1,size*0.7f)))
                    g.DrawLine(trail,tail.X,tail.Y,pos.X,pos.Y);
                using (Brush glow = new SolidBrush(Color.FromArgb(alpha/12,255,160,50)))
                    g.FillEllipse(glow,pos.X-size*3,pos.Y-size*3,size*6,size*6);
                using (Brush spark = new SolidBrush(color))
                    g.FillEllipse(spark,pos.X-size/2,pos.Y-size/2,size,size);
            }
        }
        float hx = X(MonthCoordinate(state.Head),state), hy = Y(state.HeadClose,state);
        float flicker = 1f+0.13f*(float)Math.Sin(time*9)+0.07f*(float)Math.Sin(time*17+1.3);
        for (int ring = 4; ring >= 1; ring--)
        {
            float radius = ring*7*flicker;
            using (Brush glow = new SolidBrush(Color.FromArgb(10+(4-ring)*8,255,185,75)))
                g.FillEllipse(glow,hx-radius,hy-radius,radius*2,radius*2);
        }
        using (Pen flare = new Pen(Color.FromArgb(150,255,233,186),1.5f))
        {
            g.DrawLine(flare,hx-12*flicker,hy,hx+12*flicker,hy);
            g.DrawLine(flare,hx,hy-16*flicker,hx,hy+16*flicker);
        }
        using (Brush core = new SolidBrush(Color.FromArgb(255,255,248,222)))
            g.FillEllipse(core,hx-4*flicker,hy-4*flicker,8*flicker,8*flicker);
        g.Restore(clip);
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
            double end = Math.Min(state.CameraLeft + state.CameraSpan, points[i + 1].Month);
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
        List<Event> visible = model.Events.Where(ev => ev.Arrival <= time && time < ev.Arrival + ev.Hold + 2.5 &&
            ev.Month >= state.CameraLeft && ev.Month <= state.CameraLeft + state.CameraSpan).ToList();
        if (visible.Count == 0) return;
        if (visible.Any(ev => ev.Date == model.Events[model.Events.Count - 1].Date))
            AppendFuturePath(line, model.Daily, state);
        List<RectangleF> occupied = new List<RectangleF>();
        for (int i = 0; i < visible.Count; i++)
        {
            Event ev = visible[i];
            float x = X(ev.Month, state), y = Y(ev.Close, state);
            if (x < PlotLeft + 5f || x > PlotRight - 5f) continue;
            double age = time - ev.Arrival;
            if (age < 1.2)
            {
                float radius = 8f + 65f * (float)(age / 1.2);
                using (Pen ring = new Pen(Color.FromArgb((int)(180*(1-age/1.2)),215,189,128), 2f))
                    g.DrawEllipse(ring,x-radius,y-radius,radius*2,radius*2);
            }
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
            using (Brush fill = new SolidBrush(Color.FromArgb(alpha, 27, 40, 59)))
            using (Brush ink = new SolidBrush(Color.FromArgb(alpha, 255, 240, 209)))
            {
                using (GraphicsPath path = RoundedRect(new RectangleF(bubble.X + 3f, bubble.Y + 5f,
                    bubble.Width, bubble.Height), 8f)) g.FillPath(shadow, path);
                using (GraphicsPath path = RoundedRect(bubble, 8f)) g.FillPath(fill, path);
                g.DrawString(ev.Label, font, ink,
                    new RectangleF(bubble.X + 19f, bubble.Y + 8f, bubble.Width - 33f, bubble.Height - 12f));
            }
        }
    }

    private static void DrawHeader(Graphics g, Model model, FrameState state, double time)
    {
        if (model.PlanetaryMotion && !model.ReadableChartMotion)
        {
            using (Font label = new Font("Microsoft YaHei", 34, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font number = new Font("Bahnschrift", 76, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font date = new Font("Bahnschrift", 31, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Brush white = new SolidBrush(Color.FromArgb(220,240,242,243)))
            using (Brush gold = new SolidBrush(Color.FromArgb(235,215,189,128)))
            using (Brush gray = new SolidBrush(Color.FromArgb(150,131,146,167)))
            {
                DrawFit(g,model.InstrumentLabel,label,white,new RectangleF(96,215,804,55),null);
                g.DrawString(state.HeadClose.ToString("N2",CultureInfo.InvariantCulture),number,gold,new PointF(96,282));
                g.DrawString(state.Head.ToString("yyyy.MM.dd")+"   ·   日收盘",date,gray,new PointF(98,390));
            }
            return;
        }
        using (Font small = new Font("Microsoft YaHei", 25f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Font title = new Font("Microsoft YaHei", 49f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font number = new Font("Bahnschrift", 100f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font date = new Font("Bahnschrift", 46f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush gold = new SolidBrush(Color.FromArgb(215, 189, 128)))
        using (Brush white = new SolidBrush(Color.FromArgb(240, 242, 243)))
        using (Brush gray = new SolidBrush(Color.FromArgb(131, 146, 167)))
        {
            g.FillRectangle(gold, 96, 170, 36, 4);
            DrawFit(g, model.InstrumentLabel + "  /  MARKET HISTORY", small, gray, new RectangleF(148, 150, 752, 40), null);
            DrawFit(g, model.Title, title, white, new RectangleF(96, 206, 804, 70), null);
            g.DrawString(state.HeadClose.ToString("N2", CultureInfo.InvariantCulture), number, gold, new RectangleF(90, 280, 690, 125));
            g.DrawString(state.Head.ToString("yyyy.MM.dd"), date, white, new RectangleF(96, 405, 500, 60));
            g.DrawString("日收盘 / " + model.Unit, small, gray, new RectangleF(695, 420, 205, 40));
            using (Pen divider = new Pen(Color.FromArgb(55, 131, 146, 167), 1))
                g.DrawLine(divider, 96, 480, 900, 480);
        }
    }

    private static void DrawNavigator(Graphics g, Model model, FrameState state, double time)
    {
        if (model.PlanetaryMotion && !model.ReadableChartMotion) return;
        const float left = 96, top = 1470, width = 804, height = 65;
        double start = model.Daily[0].Month, span = model.Daily[model.Daily.Count-1].Month-start;
        PointF[] points = model.Daily.Select(p => new PointF(left+(float)((p.Month-start)/span)*width,
            top+height-(float)((p.Close-model.OverviewMin)/(model.OverviewMax-model.OverviewMin))*height)).ToArray();
        using (Pen line = new Pen(Color.FromArgb(100, 131, 146, 167), 1.5f)) g.DrawLines(line, points);
        float cursor = left + (float)((MonthCoordinate(state.Head)-start)/span)*width;
        using (Brush window = new SolidBrush(Color.FromArgb(35, 215, 189, 128)))
            g.FillRectangle(window, left, top, Math.Max(1,cursor-left), height);
        using (Pen head = new Pen(Color.FromArgb(215,189,128), 2)) g.DrawLine(head,cursor,top-5,cursor,top+height+5);
        using (Font font = new Font("Microsoft YaHei", 23f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush label = new SolidBrush(Color.FromArgb(131,146,167)))
        {
            g.DrawString("全历史导航 · 亮区为已播放历史",font,label,new PointF(96,1430));
            g.DrawString(model.Start.Year.ToString(),font,label,new PointF(96,1545));
            g.DrawString(model.End.Year.ToString(),font,label,new PointF(838,1545));
        }
    }

    private static void BuildImpactCues(Model model, FrameState[] states)
    {
        foreach (Event ev in model.Events)
            model.Impacts.Add(new ImpactCue { Time=ev.Arrival, Date=ev.Date, Close=ev.Close, Key=true,
                Title=ev.Label, Detail="关键事件  /  " + ev.Date.ToString("yyyy.MM.dd") });
        // A causal 8% directional-change detector. Confirmation is shown now, never backdated to the pivot.
        double high=model.Daily[0].Close, low=high, lastCue=-10;
        int direction=0, next=1;
        for (int frame=0; frame<states.Length; frame++)
        {
            double time=frame/(double)Fps;
            while (next<model.Daily.Count && model.Daily[next].Date<=states[frame].Head)
            {
                Sample sample=model.Daily[next++];
                high=Math.Max(high,sample.Close);low=Math.Min(low,sample.Close);
                bool down=direction>0 && sample.Close<=high*0.92;
                bool up=direction<0 && sample.Close>=low*1.08;
                if (direction==0)
                {
                    if(sample.Close>=low*1.08) {direction=1;high=sample.Close;}
                    else if(sample.Close<=high*0.92) {direction=-1;low=sample.Close;}
                }
                else if(down || up)
                {
                    double change=down ? (sample.Close/high-1)*100 : (sample.Close/low-1)*100;
                    if(time-lastCue>=3.0 && time<215 && !model.Events.Any(e=>time>=e.Arrival && time<e.Arrival+3.0))
                    {
                        model.Impacts.Add(new ImpactCue { Time=time,Date=sample.Date,Close=sample.Close,Down=down,
                            Title=down?"阶段回落确认":"阶段反弹确认",
                            Detail=sample.Date.ToString("yyyy.MM.dd")+" · "+(down?"较阶段高点 ":"较阶段低点 +")+change.ToString("F1",CultureInfo.InvariantCulture)+"%  /  8%阈值" });
                        lastCue=time;
                    }
                    direction=down?-1:1;high=low=sample.Close;
                }
            }
        }
        model.Impacts=model.Impacts.OrderBy(c=>c.Time).ToList();
    }

    private static void DrawImpact(Graphics g, Model model, FrameState state, double time, bool card)
    {
        var active=model.Impacts.Where(c=>time>=c.Time && time<c.Time+(c.Key?3.0:2.0)).ToList();
        ImpactCue cue=active.LastOrDefault(c=>c.Key) ?? active.LastOrDefault();
        if(cue==null || time>=218) return;
        double age=time-cue.Time, duration=cue.Key?3.0:2.0;
        double opacity=Math.Min(1,age/0.12)*Math.Min(1,(duration-age)/0.4);
        Color accent=cue.Down?Color.FromArgb(97,215,190):Color.FromArgb(255,201,104);
        float x=X(MonthCoordinate(cue.Date),state), y=Y(cue.Close,state);
        if(card)
        {
            if (model.PlanetaryMotion && !model.ReadableChartMotion)
            {
                using (Font title=new Font("Microsoft YaHei",43,FontStyle.Regular,GraphicsUnit.Pixel))
                using (Font detail=new Font("Microsoft YaHei",24,FontStyle.Regular,GraphicsUnit.Pixel))
                using (Brush ink=new SolidBrush(Color.FromArgb((int)(255*opacity),accent)))
                using (Brush muted=new SolidBrush(Color.FromArgb((int)(165*opacity),180,194,210)))
                using (StringFormat center=new StringFormat { Alignment=StringAlignment.Center })
                {
                    DrawFit(g,cue.Title,title,ink,new RectangleF(96,1200,804,66),center);
                    DrawFit(g,cue.Date.ToString("yyyy.MM.dd"),detail,muted,new RectangleF(96,1280,804,40),center);
                }
                return;
            }
            float lift=24*(float)Math.Pow(1-Math.Min(1,age/0.4),3);
            using(Brush bg=new SolidBrush(Color.FromArgb((int)(245*opacity),20,32,49)))
            using(Brush ink=new SolidBrush(Color.FromArgb((int)(255*opacity),accent)))
            using(Font title=new Font("Microsoft YaHei",46,FontStyle.Bold,GraphicsUnit.Pixel))
            using(Font detail=new Font("Microsoft YaHei",25,FontStyle.Regular,GraphicsUnit.Pixel))
            {
                g.FillRectangle(bg,96,1190+lift,804,142);
                g.FillRectangle(ink,96,1190+lift,7,142);
                g.FillRectangle(ink,96,1190+lift,804*(float)Math.Min(1,age/.35),3);
                DrawFit(g,cue.Title,title,ink,new RectangleF(120,1202+lift,756,68),null);
                DrawFit(g,cue.Detail,detail,ink,new RectangleF(120,1278+lift,756,40),null);
            }
            return;
        }
        GraphicsState saved=g.Save();
        g.SetClip(new RectangleF(PlotLeft,PlotTop,PlotRight-PlotLeft,PlotBottom-PlotTop));
        double impact=Math.Max(0,1-age/1.3);
        using(Brush wash=new SolidBrush(Color.FromArgb((int)(22*impact),accent)))
            g.FillRectangle(wash,PlotLeft,PlotTop,PlotRight-PlotLeft,PlotBottom-PlotTop);
        for(int ring=0;ring<3;ring++)
        {
            double phase=age-ring*.12;
            if(phase<0 || phase>1.15) continue;
            float radius=12+(float)(180*(1-Math.Pow(1-phase/1.15,2)));
            using(Pen pen=new Pen(Color.FromArgb((int)(210*(1-phase/1.15)),accent),3.5f-ring*.6f))
                g.DrawEllipse(pen,x-radius,y-radius,2*radius,2*radius);
        }
        if(age<1.25)
        {
            for(int n=0;n<(cue.Key?72:44);n++)
            {
                double angle=2*Math.PI*SparkNoise(n,17),speed=95+200*SparkNoise(n,18);
                PointF head=SparkPosition(new PointF(x,y),age,Math.Cos(angle)*speed,Math.Sin(angle)*speed,0);
                PointF tail=SparkPosition(new PointF(x,y),Math.Max(0,age-.075),Math.Cos(angle)*speed,Math.Sin(angle)*speed,0);
                using(Pen ray=new Pen(Color.FromArgb((int)(235*Math.Pow(1-age/1.25,1.5)),accent),2.2f))
                    g.DrawLine(ray,tail.X,tail.Y,head.X,head.Y);
            }
        }
        g.Restore(saved);
    }

    private static double Ease01(double value)
    {
        double t = Math.Max(0, Math.Min(1, value));
        return t * t * (3 - 2 * t);
    }

    private static double CloudNoise(double x, double y)
    {
        int ix=(int)Math.Floor(x), iy=(int)Math.Floor(y);
        double u=Ease01(x-ix),v=Ease01(y-iy);
        double a=SparkNoise(ix+iy*197,111),b=SparkNoise(ix+1+iy*197,111);
        double c=SparkNoise(ix+(iy+1)*197,111),d=SparkNoise(ix+1+(iy+1)*197,111);
        return (a+(b-a)*u)*(1-v)+(c+(d-c)*u)*v;
    }

    // Original procedural nebula: built once per render process, no borrowed footage.
    private static Bitmap BuildNebulaBackground()
    {
        Bitmap image=new Bitmap(Width,Height,PixelFormat.Format32bppArgb);
        BitmapData data=image.LockBits(new Rectangle(0,0,Width,Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        byte[] pixels=new byte[data.Stride*Height];
        for(int y=0;y<Height;y++)
            for(int x=0;x<Width;x++)
            {
                double nx=(x-510)/540.0,ny=(y-900)/700.0;
                double f=.56*CloudNoise(x/210.0+17,y/230.0+19)
                    +.28*CloudNoise(x/87.0+32,y/95.0+11)+.16*CloudNoise(x/29.0+23,y/31.0+43);
                double ribbon=Math.Exp(-Math.Pow((nx+.32*Math.Sin(ny*3.2)-.06)/.47,2)-ny*ny*.85);
                double blue=Math.Exp(-Math.Pow((nx-.42)/.65,2)-Math.Pow((ny+.45)/.72,2));
                double cloud=Math.Pow(Math.Max(0,f-.24),1.7);
                double dust=SparkNoise(x+y*Width,114)*1.5;
                int p=y*data.Stride+x*4;
                pixels[p]=(byte)Math.Min(255,10+cloud*(38*ribbon+100*blue)+dust);
                pixels[p+1]=(byte)Math.Min(255,5+cloud*(25*ribbon+32*blue)+dust);
                pixels[p+2]=(byte)Math.Min(255,4+cloud*(125*ribbon+32*blue)+dust);
                pixels[p+3]=255;
            }
        Marshal.Copy(pixels,0,data.Scan0,pixels.Length);
        image.UnlockBits(data);
        return image;
    }

    private static PointF NebulaParticle(int i,double time,float tx,float ty)
    {
        double open=Ease01((time-.10)/.85),collapse=Ease01((time-1.22)/1.7);
        double angle=SparkNoise(i,121)*Math.PI*2+time*.18;
        double radius=Math.Sqrt(SparkNoise(i,122));
        double width=25+470*open;
        double px=Math.Cos(angle)*radius*width;
        double py=Math.Sin(angle)*radius*width*.46;
        py+=65*Math.Sin(px/180.0)+35*(SparkNoise(i,123)-.5);
        double twist=collapse*1.5,c=Math.Cos(twist),s=Math.Sin(twist);
        double depth=.55+.7*SparkNoise(i,124);
        return new PointF((float)((510+(px*c-py*s)*depth)*(1-collapse)+tx*collapse),
            (float)((865+(px*s+py*c)*depth)*(1-collapse)+ty*collapse));
    }

    private static void DrawNebulaIntro(Graphics g,Model model,FrameState state,double time,Bitmap background)
    {
        // Reveal the chart under the converging particles before the first quote moves.
        // The default nebula variant retains its original opaque background.
        if (model.ReadableChartMotion)
        {
            double cover = 1-Ease01((time-2.35)/.65);
            using (Brush curtain = new SolidBrush(Color.FromArgb((int)(255*cover),5,9,20)))
                g.FillRectangle(curtain,0,0,Width,Height);
        }
        else g.DrawImageUnscaled(background,0,0);
        DrawStarField(g,time,.28);
        float tx=X(MonthCoordinate(state.Head),state),ty=Y(state.HeadClose,state);
        double collapse=Ease01((time-1.22)/1.7);
        for(int i=0;i<2400;i++)
        {
            PointF p=NebulaParticle(i,time,tx,ty),tail=NebulaParticle(i,Math.Max(0,time-.022),tx,ty);
            double depth=SparkNoise(i,125),fade=(.35+.65*depth)*(1-.92*collapse);
            Color tint=i%5<2?Color.FromArgb(162,178,255):Color.FromArgb(255,229,190);
            float size=(float)(.65+1.5*depth);
            if(collapse>.02)
                using(Pen trail=new Pen(Color.FromArgb((int)(100*fade),tint),size*.65f))
                    g.DrawLine(trail,tail.X,tail.Y,p.X,p.Y);
            using(Brush dust=new SolidBrush(Color.FromArgb((int)(230*fade),tint)))
                g.FillEllipse(dust,p.X,p.Y,size,size);
            if(i%47==0)
                using(Brush glow=new SolidBrush(Color.FromArgb((int)(28*fade),tint)))
                    g.FillEllipse(glow,p.X-5,p.Y-5,10,10);
        }
        double pulse=(1-Ease01(time/.65))*.95+Ease01((time-1.65)/1.25);
        // The moving light, rather than a solid sphere, becomes the first real quote.
        float cx=(float)(510*(1-collapse)+tx*collapse),cy=(float)(865*(1-collapse)+ty*collapse);
        DrawLensGlow(g,cx,cy,pulse,Color.FromArgb(255,225,177));
        double fadeText=Ease01(time/.15)*(1-Ease01((time-1.45)/.7));
        using(Font title=new Font("Microsoft YaHei",48,FontStyle.Regular,GraphicsUnit.Pixel))
        using(Font subtitle=new Font("Microsoft YaHei",31,FontStyle.Regular,GraphicsUnit.Pixel))
        using(Brush white=new SolidBrush(Color.FromArgb((int)(235*fadeText),255,240,209)))
        using(Brush gray=new SolidBrush(Color.FromArgb((int)(180*fadeText),181,184,199)))
        using(StringFormat center=new StringFormat { Alignment=StringAlignment.Center })
        {
            DrawFit(g,model.InstrumentLabel,title,white,new RectangleF(96,430,804,80),center);
            DrawFit(g,"穿越涨跌的星河",subtitle,gray,new RectangleF(96,515,804,58),center);
            DrawFit(g,model.IntroQuestion,subtitle,white,new RectangleF(96,1215,804,90),center);
        }
    }

    private static void DrawNebulaHead(Graphics g,Model model,FrameState state,double time)
    {
        GraphicsState saved=g.Save();
        if (model.ReadableChartMotion)
            g.SetClip(new RectangleF(PlotLeft,PlotTop,PlotRight-PlotLeft,PlotBottom-PlotTop));
        float x=X(MonthCoordinate(state.Head),state),y=Y(state.HeadClose,state);
        ImpactCue cue=model.Impacts.LastOrDefault(c=>c.Time<=time);
        double age=cue==null?99:time-cue.Time;
        double burst=age<1.6?Math.Sin(Math.PI*age/1.6):0;
        Color tint=cue!=null&&cue.Down&&age<1.6?Color.FromArgb(143,209,235):Color.FromArgb(255,224,169);
        for(int i=0;i<210;i++)
        {
            double a=SparkNoise(i,131)*Math.PI*2+time*.25;
            double r=(9+SparkNoise(i,132)*65)*(1+2.3*burst)*(model.ReadableChartMotion?.65:1);
            float px=x+(float)(Math.Cos(a)*r),py=y+(float)(Math.Sin(a)*r*.5);
            int alpha=(int)((30+120*SparkNoise(i,133))*(.45+.55*burst));
            float size=(float)(.6+1.3*SparkNoise(i,134));
            using(Brush b=new SolidBrush(Color.FromArgb(alpha,tint)))g.FillEllipse(b,px,py,size,size);
        }
        DrawLensGlow(g,x,y,model.ReadableChartMotion?.35+.35*burst:.78+.22*burst,tint);
        using(Brush b=new SolidBrush(Color.FromArgb(255,255,247,229)))g.FillEllipse(b,x-3,y-3,6,6);
        g.Restore(saved);
    }

    private static void DrawOrb(Graphics g, float x, float y, float radius, double opacity, bool blue)
    {
        int r=blue?116:255, green=blue?187:214, b=blue?255:146;
        opacity=Math.Max(0,Math.Min(1,opacity));
        for(int i=64;i>0;i--)
        {
            float halo=radius*(1+i*.0475f);
            using(Brush light=new SolidBrush(Color.FromArgb((int)Math.Round(opacity),r,green,b)))
                g.FillEllipse(light,x-halo,y-halo,2*halo,2*halo);
        }
        // Nested, displaced illuminated discs create a shaded sphere rather than a flat dot.
        for(int i=128;i>0;i--)
        {
            float q=i/128f, size=radius*q;
            float ox=x-radius*(1-q)*.36f, oy=y-radius*(1-q)*.38f;
            double shade=.24+.76*Math.Sqrt(1-q*q);
            using(Brush surface=new SolidBrush(Color.FromArgb((int)(255*opacity),
                (int)(r*shade),(int)(green*shade),(int)(b*shade))))
                g.FillEllipse(surface,ox-size,oy-size,2*size,2*size);
        }
        using(Pen rim=new Pen(Color.FromArgb((int)(180*opacity),r,green,b),Math.Max(1,radius*.025f)))
            g.DrawEllipse(rim,x-radius,y-radius,2*radius,2*radius);
        float glint=Math.Max(1.6f,radius*.13f);
        using(Brush hot=new SolidBrush(Color.FromArgb((int)(210*opacity),255,247,225)))
            g.FillEllipse(hot,x-radius*.34f-glint,y-radius*.36f-glint,2*glint,2*glint);
    }

    private static PointF PlanetParticle(int i, double scatter, double converge, float tx, float ty, double time)
    {
        double theta=SparkNoise(i,81)*Math.PI*2+time*.48;
        double z=2*SparkNoise(i,82)-1, equator=Math.Sqrt(1-z*z);
        double radius=105+(220+400*SparkNoise(i,83))*scatter;
        double perspective=1/(1-z*.30);
        double x=Math.Cos(theta)*equator*radius*perspective;
        double y=(Math.Sin(theta)*equator*.66+z*.36)*radius*perspective;
        // Curving flight collapses towards the first real quote without drawing future prices.
        double spin=converge*2.4, c=Math.Cos(spin), s=Math.Sin(spin);
        return new PointF((float)((498+(x*c-y*s))*(1-converge)+tx*converge),
            (float)((865+(x*s+y*c))*(1-converge)+ty*converge));
    }

    private static void DrawPlanetaryIntro(Graphics g, Model model, FrameState state, double time)
    {
        double reveal=Ease01((time-2.65)/.35);
        using(Brush black=new SolidBrush(Color.FromArgb((int)(255*(1-reveal)),3,6,15)))
            g.FillRectangle(black,0,0,Width,Height);
        DrawStarField(g,time,.5*(1-reveal));
        double scatter=Ease01((time-.28)/.85);
        double converge=Ease01((time-1.4)/1.6);
        float tx=X(MonthCoordinate(state.Head),state),ty=Y(state.HeadClose,state);
        for(int i=0;i<1150;i++)
        {
            PointF p=PlanetParticle(i,scatter,converge,tx,ty,time);
            double before=Math.Max(0,time-.025);
            PointF tail=PlanetParticle(i,Ease01((before-.28)/.85),Ease01((before-1.4)/1.6),tx,ty,before);
            double depth=.3+.7*SparkNoise(i,84);
            double alpha=(.3+.7*scatter)*(1-.85*reveal)*depth;
            bool blue=i%3==0;
            Color color=blue?Color.FromArgb(125,195,255):Color.FromArgb(255,216,147);
            using(Pen streak=new Pen(Color.FromArgb((int)(140*alpha),color),.7f+(float)depth))
                g.DrawLine(streak,tail.X,tail.Y,p.X,p.Y);
            float size=(float)(.9+2.3*depth);
            using(Brush glow=new SolidBrush(Color.FromArgb((int)(15*alpha),color)))
                g.FillEllipse(glow,p.X-size*3,p.Y-size*3,size*6,size*6);
            using(Brush dot=new SolidBrush(Color.FromArgb((int)(235*alpha),color)))
                g.FillEllipse(dot,p.X-size/2,p.Y-size/2,size,size);
        }
        // Larger satellite bodies share the cloud's dispersal and gravitational return.
        for(int i=0;i<14;i++)
        {
            PointF p=PlanetParticle(i*79+4,scatter,converge,tx,ty,time);
            float size=(float)((7+16*SparkNoise(i,86))*(1-converge)+2);
            DrawOrb(g,p.X,p.Y,size,(1-reveal)*.95,i%3==0);
        }
        if(time<1.05) DrawOrb(g,498,865,(float)(88*(1-scatter)+4),1-Ease01((time-.3)/.65),false);
        double born=Ease01((time-2.05)/.9);
        PointF core=new PointF((float)(498*(1-converge)+tx*converge),(float)(865*(1-converge)+ty*converge));
        DrawLensGlow(g,core.X,core.Y,born,Color.FromArgb(255,222,166));
        DrawOrb(g,core.X,core.Y,(float)(4+8*born),born,false);
        double textFade=(1-Ease01((time-1.45)/.7))*Ease01(time/.2);
        using(Font title=new Font("Microsoft YaHei",47,FontStyle.Regular,GraphicsUnit.Pixel))
        using(Font question=new Font("Microsoft YaHei",39,FontStyle.Regular,GraphicsUnit.Pixel))
        using(Brush ink=new SolidBrush(Color.FromArgb((int)(235*textFade),240,242,243)))
        using(Brush gold=new SolidBrush(Color.FromArgb((int)(220*textFade),215,189,128)))
        using(StringFormat center=new StringFormat { Alignment=StringAlignment.Center })
        {
            DrawFit(g,model.InstrumentLabel,title,ink,new RectangleF(96,395,804,75),center);
            DrawFit(g,model.IntroQuestion,question,gold,new RectangleF(96,1290,804,100),center);
        }
    }

    private static void DrawPlanetaryHead(Graphics g, Model model, FrameState state, double time)
    {
        float x=X(MonthCoordinate(state.Head),state),y=Y(state.HeadClose,state);
        ImpactCue cue=model.Impacts.LastOrDefault(c=>c.Time<=time);
        double age=cue==null?99:time-cue.Time;
        double burst=age<1.6?Math.Sin(Math.PI*age/1.6):0;
        // Historical line remains fixed; only decorative orbital dust opens and contracts.
        for(int i=0;i<160;i++)
        {
            double angle=SparkNoise(i,91)*Math.PI*2+time*(.65+SparkNoise(i,92));
            double radius=14+SparkNoise(i,93)*(35+170*burst);
            double z=Math.Sin(angle);
            float px=x+(float)(Math.Cos(angle)*radius),py=y+(float)(z*radius*.38);
            double fade=(.3+.7*SparkNoise(i,94))*(.45+.55*burst);
            using(Brush dust=new SolidBrush(Color.FromArgb((int)(180*fade),255,221,158)))
                g.FillEllipse(dust,px,py,1.4f+(float)(z+1),1.4f+(float)(z+1));
        }
        DrawLensGlow(g,x,y,.55+.4*burst,Color.FromArgb(255,222,166));
        DrawOrb(g,x,y,(float)(11+3*burst),1,false);
    }

    // Decorative depth field. Positions are independent of prices and global-frame deterministic.
    private static void DrawStarField(Graphics g, double time, double strength)
    {
        for (int i = 0; i < 220; i++)
        {
            double depth = 0.25 + 0.75 * SparkNoise(i, 61);
            float x = (float)((SparkNoise(i, 62) * 1180 + time * (2 + depth * 4)) % 1180 - 50);
            float y = (float)(220 + SparkNoise(i, 63) * 1320 + 9 * Math.Sin(time * 0.2 + i));
            float radius = (float)(0.7 + depth * 1.8);
            int alpha = (int)(strength * (50 + 120 * depth) * (0.6 + 0.4 * Math.Sin(time + i) * Math.Sin(time + i)));
            using (Brush dot = new SolidBrush(Color.FromArgb(alpha, i % 4 == 0 ? 215 : 148, i % 4 == 0 ? 189 : 183, i % 4 == 0 ? 128 : 225)))
                g.FillEllipse(dot, x, y, radius * 2, radius * 2);
        }
    }

    private static void DrawLensGlow(Graphics g, float x, float y, double power, Color color)
    {
        power = Math.Max(0, Math.Min(1, power));
        for (int i = 32; i > 0; i--)
        {
            float radius = 5 + i * 3.6f;
            using (Brush glow = new SolidBrush(Color.FromArgb((int)(power * (2 + (32-i)*0.13)), color)))
                g.FillEllipse(glow, x-radius, y-radius, radius*2, radius*2);
        }
        for (int pass = 0; pass < 3; pass++)
            for (int i = -32; i < 32; i++)
            {
                double falloff = Math.Exp(-Math.Pow(i/12.0,2));
                using (Pen flare = new Pen(Color.FromArgb((int)(power * falloff * (pass==2?190:25)), color),pass==2?1.6f:12-pass*7))
                    g.DrawLine(flare,x+i*5,y,x+(i+1)*5,y);
            }
        using (Brush core = new SolidBrush(Color.FromArgb((int)(235*power),255,244,220)))
            g.FillEllipse(core,x-4,y-4,8,8);
    }

    private static void DrawStellarIntro(Graphics g, Model model, FrameState state, double time)
    {
        double reveal = Ease01((time - 2.3) / 0.7);
        using (Brush bg = new SolidBrush(Color.FromArgb((int)(255*(1-reveal)),5,9,20)))
            g.FillRectangle(bg,0,0,Width,Height);
        DrawStarField(g,time,(1-reveal)*0.85);
        double converge = Ease01((time-1.8)/1.2);
        float cx = (float)(498 + (X(MonthCoordinate(state.Head),state)-498)*converge);
        float cy = (float)(930 + (Y(state.HeadClose,state)-930)*converge);
        double birth = Ease01(time/.4);
        for (int i=0;i<640;i++)
        {
            double depth=.25+.75*SparkNoise(i,71);
            double angle=SparkNoise(i,72)*Math.PI*2 + time*(.25+depth*.35);
            double radius=(80+560*SparkNoise(i,73))*(.7+.3*birth)*(1-.98*converge);
            float x=cx+(float)(Math.Cos(angle)*radius);
            float y=cy+(float)(Math.Sin(angle)*radius*.7);
            float size=(float)(.8+2.3*depth);
            // Leave the title's centre visually calm; the light trail surrounds it.
            double quiet = x>95 && x<905 && y>650 && y<850 ? .15 : 1;
            int alpha=(int)(birth*(1-reveal)*(90+145*depth)*quiet);
            using(Brush dot=new SolidBrush(Color.FromArgb(alpha,i%3==0?255:145,i%3==0?221:183,i%3==0?155:238)))
                g.FillEllipse(dot,x,y,size,size);
        }
        double textOpacity=Ease01(time/.35)*(1-Ease01((time-2.15)/.65));
        using(Font title=new Font("Microsoft YaHei",86,FontStyle.Bold,GraphicsUnit.Pixel))
        using(Font sub=new Font("Microsoft YaHei",44,FontStyle.Bold,GraphicsUnit.Pixel))
        using(Font small=new Font("Microsoft YaHei",27,FontStyle.Regular,GraphicsUnit.Pixel))
        using(Brush white=new SolidBrush(Color.FromArgb((int)(255*textOpacity),240,242,243)))
        using(Brush gold=new SolidBrush(Color.FromArgb((int)(255*textOpacity),215,189,128)))
        using(StringFormat center=new StringFormat { Alignment=StringAlignment.Center })
        {
            DrawFit(g,model.InstrumentLabel,title,white,new RectangleF(96,650,804,130),center);
            DrawFit(g,String.IsNullOrWhiteSpace(model.IntroQuestion)?model.Subtitle:model.IntroQuestion,sub,gold,new RectangleF(96,790,804,90),center);
            DrawFit(g,model.RangeLabel,small,white,new RectangleF(96,1140,804,55),center);
        }
        DrawLensGlow(g,cx,cy,birth*(1-reveal),Color.FromArgb(255,213,137));
    }

    private static void DrawStellarTransition(Graphics g, Model model, FrameState state, double time)
    {
        Event ev=model.Events.LastOrDefault(e=>e.Major && e.Arrival<=time);
        double age=ev==null?99:time-ev.Arrival;
        if(age<0 || age>1.05) return;
        double fade=Math.Pow(1-age/1.05,2);
        float x=X(ev.Month,state),y=Y(ev.Close,state);
        GraphicsState saved=g.Save();
        g.SetClip(new RectangleF(PlotLeft,PlotTop,PlotRight-PlotLeft,PlotBottom-PlotTop));
        DrawLensGlow(g,x,y,fade,Color.FromArgb(255,213,137));
        g.Restore(saved);
        // Sweep through the card border, keeping all quote text and the price geometry still.
        float sweep=(float)(96+804*Ease01(age/.65));
        for(int i=0;i<18;i++)
        {
            float px=sweep-i*8;
            if(px<96 || px>900) continue;
            using(Pen ray=new Pen(Color.FromArgb((int)(200*fade*(1-i/18.0)),255,232,174),3))
                g.DrawLine(ray,px,1185,Math.Min(900,px+8),1185);
        }
    }

    private static void DrawIntro(Graphics g, Model model, double time)
    {
        float fade = (float)Math.Min(1, (3-time)/0.5);
        using (Brush bg = new SolidBrush(Color.FromArgb((int)(255*fade),5,9,20))) g.FillRectangle(bg,0,0,Width,Height);
        float entry = (float)(1-Math.Pow(1-Math.Min(1,time/1.2),3));
        int alpha = (int)(255*fade*entry);
        using (Font title = new Font("Microsoft YaHei", 92f, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font sub = new Font("Microsoft YaHei", 42f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush gold = new SolidBrush(Color.FromArgb(alpha,215,189,128)))
        using (Brush white = new SolidBrush(Color.FromArgb(alpha,240,242,243)))
        {
            g.FillRectangle(gold,96,620,804*entry,4);
            DrawFit(g,model.Title,title,white,new RectangleF(96,710+45*(1-entry),804,160),null);
            DrawFit(g,model.Subtitle,sub,gold,new RectangleF(96,895,804,90),null);
            DrawFit(g,model.RangeLabel,sub,white,new RectangleF(96,1050,804,90),null);
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
            RectangleF area = new RectangleF(96f, 1190f - 18f * (1f - entrance), 804f, 142f);
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
        if(model.PlanetaryMotion && !model.ReadableChartMotion)
        {
            using(Font font=new Font("Microsoft YaHei",23,FontStyle.Regular,GraphicsUnit.Pixel))
            using(Brush ink=new SolidBrush(Color.FromArgb(105,131,146,167)))
            using(StringFormat center=new StringFormat { Alignment=StringAlignment.Center })
                DrawFit(g,model.RangeLabel,font,ink,new RectangleF(96,1490,804,40),center);
            return;
        }
        using (Font font = new Font("Microsoft YaHei", 25f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (Brush brush = new SolidBrush(Color.FromArgb(190, TextGray)))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center })
        {
            DrawFit(g, model.RangeLabel, font, brush, new RectangleF(96f, 1344f, 804f, 36f), center);
            DrawFit(g, model.Source, font, brush, new RectangleF(96f, 1380f, 804f, 36f), center);
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
                "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", OutputWidth+"x"+OutputHeight,
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
                // Preserve fine chart lines and sparkler detail in the upload master.
                start.ArgumentList.Add("-preset"); start.ArgumentList.Add("slow");
                start.ArgumentList.Add("-crf"); start.ArgumentList.Add("14");
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
            byte[] bytes = new byte[OutputWidth * OutputHeight * 4];
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
                        BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, OutputWidth, OutputHeight),
                            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        try
                        {
                            if (locked.Stride != OutputWidth * 4)
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
                        camera_left = state.CameraLeft, camera_span = state.CameraSpan,
                        visible_first_date = model.Daily[0].Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        y_min = state.YMin, y_max = state.YMax
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
