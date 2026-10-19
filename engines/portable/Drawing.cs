// Deliberately small adapter for the drawing operations used by the three renderers.
// Geometry and timeline logic remain shared with the Windows renderer.
using SkiaSharp;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
namespace StockVideo.Drawing;

public enum PixelFormat { Format32bppArgb, Format24bppRgb }
public enum ImageLockMode { ReadOnly, WriteOnly }
public enum ImageFormat { Png, Jpeg }
public enum FontStyle { Regular, Bold }
public enum GraphicsUnit { Pixel }
public enum StringAlignment { Near, Center, Far }
[Flags] public enum StringFormatFlags { NoWrap = 1 }
public enum StringTrimming { EllipsisCharacter }
public enum SmoothingMode { AntiAlias }
public enum PixelOffsetMode { HighQuality }
public enum CompositingQuality { HighQuality }
public enum InterpolationMode { HighQualityBicubic }
public enum TextRenderingHint { AntiAliasGridFit }
public enum CompositingMode { SourceOver, SourceCopy }
public enum LineCap { Flat, Round }
public enum LineJoin { Miter, Round }
public enum CombineMode { Intersect }
public sealed class StringFormat : IDisposable {
    public StringAlignment Alignment { get; set; }
    public StringAlignment LineAlignment { get; set; }
    public StringFormatFlags FormatFlags { get; set; }
    public StringTrimming Trimming { get; set; }
    public static StringFormat GenericTypographic => new();
    public static StringFormat GenericDefault => new();
    public StringFormat() { }
    public StringFormat(StringFormat f) { Alignment=f.Alignment; LineAlignment=f.LineAlignment; FormatFlags=f.FormatFlags; }
    public void Dispose() { }
}
public sealed class Font : IDisposable {
    public static string TextFamily = OperatingSystem.IsMacOS() ? "PingFang SC" : "Microsoft YaHei";
    public static string NumberFamily = OperatingSystem.IsMacOS() ? "Helvetica Neue" : "Bahnschrift";
    public static string TextFile, NumberFile;
    public string FontFamily { get; }
    public float Size { get; }
    public FontStyle Style { get; }
    internal SKTypeface Face;
    readonly Dictionary<int, SKTypeface> fallback = new();
    public Font(string family, float size, FontStyle style, GraphicsUnit unit) {
        bool numeric=family=="Bahnschrift";
        FontFamily=family; Size=size; Style=style;
        var file=numeric?NumberFile:TextFile;
        Face=file!=null ? SKTypeface.FromFile(file) : SKTypeface.FromFamilyName(numeric?NumberFamily:TextFamily,
            style==FontStyle.Bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        if(Face==null) throw new InvalidOperationException("Cannot load font: "+(file??family));
    }
    internal SKPaint Paint(SKColor color) => new() { Typeface=Face, TextSize=Size, Color=color, IsAntialias=true };
    internal IEnumerable<(string Text, SKTypeface Face)> Runs(string text) {
        using var font = new SKFont(Face, Size);
        var buffer = new StringBuilder(); SKTypeface current = null;
        foreach (var rune in text.EnumerateRunes()) {
            var face = Face;
            if (font.GetGlyph(rune.Value) == 0) {
                if (!fallback.TryGetValue(rune.Value, out face)) {
                    face = SKFontManager.Default.MatchCharacter(rune.Value);
                    if (face == null) throw new InvalidOperationException($"No font contains U+{rune.Value:X}");
                    fallback[rune.Value] = face;
                }
            }
            if (current != null && face != current) { yield return (buffer.ToString(), current); buffer.Clear(); }
            current = face; buffer.Append(rune.ToString());
        }
        if (buffer.Length > 0) yield return (buffer.ToString(), current);
    }
    internal float Measure(string text) {
        using var paint=Paint(SKColors.White);float width=0;
        foreach(var run in Runs(text)){paint.Typeface=run.Face;width+=paint.MeasureText(run.Text);}
        return width;
    }
    public void Dispose() { foreach(var face in fallback.Values)face.Dispose();Face.Dispose(); }
}
public sealed class BitmapData {
    public IntPtr Scan0;
    public int Stride;
    internal byte[] Bytes;
    internal GCHandle Pin;
    internal ImageLockMode Mode;
}
public sealed class Bitmap : IDisposable {
    internal SKBitmap Image;
    public int Width => Image.Width;
    public int Height => Image.Height;
    public Bitmap(int width,int height,PixelFormat format=PixelFormat.Format32bppArgb) {
        Image=new SKBitmap(new SKImageInfo(width,height,SKColorType.Bgra8888,SKAlphaType.Premul));
        Image.Erase(SKColors.Transparent);
    }
    public Bitmap(string path) { Image=SKBitmap.Decode(path)??throw new IOException("Cannot decode "+path); }
    public Bitmap Clone(Rectangle r,PixelFormat format) {
        var result=new Bitmap(r.Width,r.Height);
        using var c=new SKCanvas(result.Image); c.DrawBitmap(Image,Graphics.Rect(r),SKRect.Create(r.Width,r.Height));
        return result;
    }
    public BitmapData LockBits(Rectangle r,ImageLockMode mode,PixelFormat format) {
        if(r.X!=0||r.Y!=0||r.Width!=Width||r.Height!=Height) throw new NotSupportedException("Only full bitmap locks are supported");
        var d=new BitmapData{Stride=Width*4,Bytes=new byte[Width*Height*4],Mode=mode};
        if(mode==ImageLockMode.ReadOnly) Marshal.Copy(Image.GetPixels(),d.Bytes,0,d.Bytes.Length);
        d.Pin=GCHandle.Alloc(d.Bytes,GCHandleType.Pinned);d.Scan0=d.Pin.AddrOfPinnedObject();return d;
    }
    public void UnlockBits(BitmapData d) {
        try {
            if(d.Mode==ImageLockMode.WriteOnly) {
                // Existing glow sprites write straight-alpha BGRA; Skia draws premultiplied pixels.
                for(int i=0;i<d.Bytes.Length;i+=4) for(int k=0;k<3;k++)
                    d.Bytes[i+k]=(byte)((d.Bytes[i+k]*d.Bytes[i+3]+127)/255);
                Marshal.Copy(d.Bytes,0,Image.GetPixels(),d.Bytes.Length);
            }
        } finally { d.Pin.Free(); }
    }
    public void SetResolution(float x,float y) { }
    public void Save(string path,ImageFormat format) {
        using var image=SKImage.FromBitmap(Image);
        using var data=image.Encode(format==ImageFormat.Png?SKEncodedImageFormat.Png:SKEncodedImageFormat.Jpeg,92);
        using var stream=File.Create(path);data.SaveTo(stream);
    }
    public void Dispose()=>Image.Dispose();
}
public abstract class Brush : IDisposable {
    internal abstract SKPaint Paint();
    public virtual void Dispose() { }
}
public sealed class SolidBrush : Brush {
    readonly Color color;
    public SolidBrush(Color c) { color=c; }
    internal override SKPaint Paint()=>new(){Color=Graphics.ColorOf(color),IsAntialias=true};
}
public sealed class LinearGradientBrush : Brush {
    readonly SKShader shader;
    public LinearGradientBrush(PointF a,PointF b,Color first,Color last) {
        shader=SKShader.CreateLinearGradient(new(a.X,a.Y),new(b.X,b.Y),
            new[]{Graphics.ColorOf(first),Graphics.ColorOf(last)},null,SKShaderTileMode.Clamp);
    }
    public LinearGradientBrush(Rectangle r,Color a,Color b,float angle) {
        double rad=angle*Math.PI/180;float dx=(float)Math.Cos(rad),dy=(float)Math.Sin(rad);
        float length=Math.Abs(r.Width*dx)+Math.Abs(r.Height*dy),cx=r.X+r.Width/2f,cy=r.Y+r.Height/2f;
        shader=SKShader.CreateLinearGradient(new(cx-dx*length/2,cy-dy*length/2),new(cx+dx*length/2,cy+dy*length/2),
            new[]{Graphics.ColorOf(a),Graphics.ColorOf(b)},null,SKShaderTileMode.Clamp);
    }
    internal override SKPaint Paint()=>new(){Shader=shader,IsAntialias=true};
    public override void Dispose()=>shader.Dispose();
}
public sealed class Pen : IDisposable {
    readonly Color color; readonly Brush brush; readonly float width;
    public LineCap StartCap {get;set;} public LineCap EndCap {get;set;}
    public LineJoin LineJoin {get;set;} public float[] DashPattern {get;set;}
    public Pen(Color c,float w=1){color=c;width=w;}
    public Pen(Brush b,float w=1){brush=b;width=w;}
    internal SKPaint Paint() {
        var p=brush?.Paint()??new SKPaint{Color=Graphics.ColorOf(color),IsAntialias=true};
        p.Style=SKPaintStyle.Stroke;p.StrokeWidth=width;
        p.StrokeCap=StartCap==LineCap.Round||EndCap==LineCap.Round?SKStrokeCap.Round:SKStrokeCap.Butt;
        p.StrokeJoin=LineJoin==LineJoin.Round?SKStrokeJoin.Round:SKStrokeJoin.Miter;
        if(DashPattern!=null) p.PathEffect=SKPathEffect.CreateDash(DashPattern.Select(v=>v*width).ToArray(),0);
        return p;
    }
    public void Dispose() { }
}
public sealed class GraphicsPath : IDisposable {
    internal SKPath Path=new();
    public void AddPolygon(PointF[] points) { Path.MoveTo(points[0].X,points[0].Y);foreach(var p in points.Skip(1))Path.LineTo(p.X,p.Y);Path.Close(); }
    public void AddArc(float x,float y,float w,float h,float start,float sweep)=>Path.ArcTo(SKRect.Create(x,y,w,h),start,sweep,Path.IsEmpty);
    public void CloseFigure()=>Path.Close();
    public void Dispose()=>Path.Dispose();
}
public sealed class GraphicsState { internal int Count; }
public sealed class Matrix : IDisposable {
    internal SKMatrix Value;
    public Matrix(float a,float b,float c,float d,float tx,float ty){Value=new SKMatrix(a,c,tx,b,d,ty,0,0,1);}
    public void Dispose() { }
}
public sealed class Graphics : IDisposable {
    readonly SKCanvas canvas;
    public SmoothingMode SmoothingMode {get;set;}
    public PixelOffsetMode PixelOffsetMode {get;set;}
    public CompositingQuality CompositingQuality {get;set;}
    public InterpolationMode InterpolationMode {get;set;}
    public TextRenderingHint TextRenderingHint {get;set;}
    public CompositingMode CompositingMode {get;set;}
    public Matrix Transform {set=>canvas.SetMatrix(value.Value);}
    Graphics(Bitmap b){canvas=new SKCanvas(b.Image);}
    public static Graphics FromImage(Bitmap b)=>new(b);
    internal static SKColor ColorOf(Color c)=>new(c.R,c.G,c.B,c.A);
    internal static SKRect Rect(RectangleF r)=>SKRect.Create(r.X,r.Y,r.Width,r.Height);
    public void Clear(Color c)=>canvas.Clear(ColorOf(c));
    public void ScaleTransform(float x,float y)=>canvas.Scale(x,y);
    public GraphicsState Save(){var s=new GraphicsState{Count=canvas.SaveCount};canvas.Save();return s;}
    public void Restore(GraphicsState s)=>canvas.RestoreToCount(s.Count);
    public void SetClip(RectangleF r,CombineMode mode=CombineMode.Intersect)=>canvas.ClipRect(Rect(r),SKClipOperation.Intersect,true);
    public void FillRectangle(Brush b,float x,float y,float w,float h){using var p=b.Paint();canvas.DrawRect(x,y,w,h,p);}
    public void FillRectangle(Brush b,RectangleF r)=>FillRectangle(b,r.X,r.Y,r.Width,r.Height);
    public void FillEllipse(Brush b,float x,float y,float w,float h){using var p=b.Paint();canvas.DrawOval(SKRect.Create(x,y,w,h),p);}
    public void DrawEllipse(Pen b,float x,float y,float w,float h){using var p=b.Paint();canvas.DrawOval(SKRect.Create(x,y,w,h),p);}
    public void DrawRectangle(Pen b,float x,float y,float w,float h){using var p=b.Paint();canvas.DrawRect(x,y,w,h,p);}
    public void DrawLine(Pen b,PointF a,PointF z)=>DrawLine(b,a.X,a.Y,z.X,z.Y);
    public void DrawLine(Pen b,float x,float y,float u,float v){using var p=b.Paint();canvas.DrawLine(x,y,u,v,p);}
    public void DrawLines(Pen b,PointF[] points){if(points.Length<2)return;using var path=new SKPath();path.MoveTo(points[0].X,points[0].Y);foreach(var pt in points.Skip(1))path.LineTo(pt.X,pt.Y);using var p=b.Paint();canvas.DrawPath(path,p);}
    public void FillPath(Brush b,GraphicsPath path){using var p=b.Paint();canvas.DrawPath(path.Path,p);}
    public void FillPolygon(Brush b,PointF[] pts){using var path=new GraphicsPath();path.AddPolygon(pts);FillPath(b,path);}
    public void DrawImage(Bitmap b,RectangleF dst,RectangleF src,GraphicsUnit unit){
        using var p=new SKPaint{IsAntialias=true,FilterQuality=SKFilterQuality.High,BlendMode=CompositingMode==CompositingMode.SourceCopy?SKBlendMode.Src:SKBlendMode.SrcOver};
        canvas.DrawBitmap(b.Image,Rect(src),Rect(dst),p);
    }
    public void DrawImage(Bitmap b,Rectangle dst)=>DrawImage(b,dst,new RectangleF(0,0,b.Width,b.Height),GraphicsUnit.Pixel);
    public void DrawImage(Bitmap b,Rectangle dst,float x,float y,float w,float h,GraphicsUnit unit)=>DrawImage(b,dst,new RectangleF(x,y,w,h),unit);
    public void DrawImageUnscaled(Bitmap b,int x,int y)=>DrawImage(b,new Rectangle(x,y,b.Width,b.Height));
    public SizeF MeasureString(string text,Font f){using var p=f.Paint(SKColors.White);var lines=text.Split('\n');return new(lines.Max(s=>f.Measure(s)),lines.Length*(p.FontMetrics.Descent-p.FontMetrics.Ascent+p.FontMetrics.Leading));}
    public SizeF MeasureString(string text,Font f,SizeF size,StringFormat sf)=>MeasureString(text,f);
    public SizeF MeasureString(string text,Font f,int width,StringFormat sf)=>MeasureString(text,f);
    public void DrawString(string text,Font f,Brush b,PointF pt)=>DrawString(text,f,b,pt,StringFormat.GenericDefault);
    public void DrawString(string text,Font f,Brush b,PointF pt,StringFormat sf)=>DrawString(text,f,b,new RectangleF(pt.X,pt.Y,0,0),sf);
    public void DrawString(string text,Font f,Brush b,float x,float y)=>DrawString(text,f,b,new PointF(x,y),StringFormat.GenericDefault);
    public void DrawString(string text,Font f,Brush b,RectangleF area)=>DrawString(text,f,b,area,StringFormat.GenericDefault);
    public void DrawString(string text,Font f,Brush b,RectangleF area,StringFormat sf){
        using var bp=b.Paint();using var p=f.Paint(bp.Color);var bounds=MeasureString(text,f);
        float y=area.Y+(sf.LineAlignment==StringAlignment.Center?(area.Height-bounds.Height)/2:sf.LineAlignment==StringAlignment.Far?area.Height-bounds.Height:0);
        int saved=canvas.Save();if(area.Width>0&&area.Height>0)canvas.ClipRect(Rect(area));
        foreach(var line in text.Split('\n')){
            float w=f.Measure(line),x=area.X+(sf.Alignment==StringAlignment.Center?(area.Width-w)/2:sf.Alignment==StringAlignment.Far?area.Width-w:0);
            float baseline=y-p.FontMetrics.Ascent, lineHeight=p.FontMetrics.Descent-p.FontMetrics.Ascent+p.FontMetrics.Leading;
            foreach(var run in f.Runs(line)){p.Typeface=run.Face;canvas.DrawText(run.Text,x,baseline,p);x+=p.MeasureText(run.Text);}
            p.Typeface=f.Face;y+=lineHeight;
        }
        canvas.RestoreToCount(saved);
    }
    public void Dispose(){canvas.Flush();canvas.Dispose();}
}

public static class Image {
    public static Bitmap FromFile(string path)=>new(path);
}
