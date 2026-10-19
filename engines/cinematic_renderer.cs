// Native GDI+ film rendering adapted from the approved local cinema still.
// Actual daily vertices are preserved. A continuous reveal cursor is visual only;
// quoted closes always come from the unmodified source observations.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text.Json;

public static class ReusableCinema
{
    const int W=1920,H=1080,SS=2;
    static int Fps,TotalFrames,Lookback;
    static double Duration;
    static double MaxPrice;
    static string OutDir;
    static string InstrumentName,Symbol,Unit,InstrumentLabel,RangeLabel,SourceLabel,Ending;
    static DateTime DemoReferenceDate;
    static double DemoReferenceClose,Threshold;
    static DateTime ThresholdDate;
    static string OverviewLowId,OverviewHighId;
    static string StringValue(JsonElement obj,string name,string fallback) {
        return obj.ValueKind==JsonValueKind.Object&&obj.TryGetProperty(name,out JsonElement value)&&value.ValueKind==JsonValueKind.String?value.GetString():fallback;
    }
    static string DateRange { get {return Daily[0].Date.ToString("yyyy.MM.dd",Inv)+"—"+Daily[Daily.Count-1].Date.ToString("yyyy.MM.dd",Inv);} }
    static void Reset() {
        Daily.Clear();Timeline.Clear();Events.Clear();Axes.Clear();Chapters.Clear();Captions.Clear();Contexts.Clear();Sparks.Clear();
        LabelLayouts.Clear();LabelOffsets.Clear();TextWidths.Clear();GlowCache.Clear();FontCache.Clear();
    }
    static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    static readonly Color Gold=Hex("D7BD80"),Cream=Hex("FFF0D1"),White=Hex("F0F2F3"),
        Muted=Hex("8392A7"),Teal=Hex("61D7BE"),Red=Hex("EE937D");
    sealed class Sample { public DateTime Date; public double Close; }
    sealed class Segment { public double Start,End; public DateTime From,To; public int First,Last; public double[] Arc; }
    sealed class Mark { public string Id,Label; public DateTime Date; public double Close,Arrival,Hold; public int Index; public bool Low; }
    sealed class State {
        public int Index,First,Completed,RevealFrom,RevealTo;
        public double T,Zoom,Cursor,RevealAlpha,RevealClose,AxisMax,Window; public DateTime ViewStart,ViewEnd;
    }
    sealed class Spark {
        public int Id; public bool Low; public double Born,Life,Vx,Vy,Vz,Phase,Ox,Oy,Oz,Radius,Alpha;
        public DateTime AnchorDate; public double AnchorClose;
    }
    sealed class SparkState {
        public int id {get;set;} public bool low {get;set;}
        public double age {get;set;} public double lifetime {get;set;}
        public double x {get;set;} public double y {get;set;} public double z {get;set;}
        public double screen_x {get;set;} public double screen_y {get;set;}
        public double alpha {get;set;} public double radius {get;set;}
        public double trail_x {get;set;} public double trail_y {get;set;}
    }
    static double RampSeconds=.24,LabelFade=.25,LifeMin=.8,LifeMax=1.6,SpeedMin=.22,SpeedMax=.65,TrailSeconds=.07;
    static int ParticleSeed=20261018,ParticleRate=170,LowRate=65;
    static readonly List<Sample> Daily=new List<Sample>();
    static readonly List<Segment> Timeline=new List<Segment>();
    static readonly List<Mark> Events=new List<Mark>();
    sealed class AxisTransition { public double Start,End,From,To; }
    sealed class Chapter { public double Start,End; public int Number; public string Title; }
    sealed class Caption { public double Start,End; public string Title,Sub,Body; }
    sealed class Context { public DateTime Date,Until; public string Text; }
    static readonly List<AxisTransition> Axes=new List<AxisTransition>();
    static readonly List<Chapter> Chapters=new List<Chapter>();
    static readonly List<Caption> Captions=new List<Caption>();
    static readonly List<Context> Contexts=new List<Context>();
    static double WindowTransitionStart=66,WindowTransitionEnd=72,WindowFrom=96,WindowTo=24,OverviewStart=117,OverviewEnd=120;
    static readonly List<Spark> Sparks=new List<Spark>();
    static readonly Dictionary<string,Bitmap> GlowCache=new Dictionary<string,Bitmap>();
    static readonly Dictionary<string,Font> FontCache=new Dictionary<string,Font>();
    static Bitmap Background;
    static Color Hex(string h) {return ColorTranslator.FromHtml("#"+h);}
    static Color A(Color c,int alpha) {return Color.FromArgb(Math.Clamp(alpha,0,255),c);}
    static DateTime Date(string s) {return DateTime.ParseExact(s,"yyyy-MM-dd",Inv);}
    static double Smooth(double u) {u=Math.Clamp(u,0,1);return u*u*(3-2*u);}
    static double Smoother(double u) {u=Math.Clamp(u,0,1);return u*u*u*(u*(u*6-15)+10);}
    static double RampIntegral(double u) {return u*u*u*u*(u*(u-3)+2.5);}
    static double Travel(double elapsed,double duration) {
        // Integrate a quintic velocity ramp, keeping a steady middle section.
        double ramp=Math.Min(RampSeconds,duration*.24),t=Math.Clamp(elapsed,0,duration),distance;
        if(t<ramp)distance=ramp*RampIntegral(t/ramp);
        else if(t>duration-ramp)distance=duration-ramp-ramp*RampIntegral((duration-t)/ramp);
        else distance=t-ramp*.5;
        return Math.Clamp(distance/(duration-ramp),0,1);
    }
    static double CaptionOpacity(double t) {
        foreach(Caption c in Captions) {
            double cut=c.Start;if(cut==0)continue;
            if(t>=cut-LabelFade*.5&&t<cut+LabelFade*.5)
                return t<cut?1-Smoother((t-cut+LabelFade*.5)/(LabelFade*.5)):Smoother((t-cut)/(LabelFade*.5));
        }
        return Smoother(t/LabelFade);
    }
    static double AxisAt(double t) {
        double value=MaxPrice;
        foreach(AxisTransition a in Axes) {
            if(t<a.Start)break;
            value=a.From+(a.To-a.From)*Smoother((t-a.Start)/(a.End-a.Start));
        }
        return value;
    }
    static double MonthsAt(double t) {return WindowFrom+(WindowTo-WindowFrom)*Smoother((t-WindowTransitionStart)/(WindowTransitionEnd-WindowTransitionStart));}
    static DateTime StartAt(DateTime end,double months) {
        int whole=(int)Math.Floor(months);DateTime a=end.AddMonths(-whole),b=end.AddMonths(-whole-1);
        return a.AddTicks((long)((b-a).Ticks*(months-whole)));
    }
    static Font Font(float size,bool bold=false,bool numeric=false) {
        string key=size+":"+bold+":"+numeric;
        if(!FontCache.ContainsKey(key)) FontCache[key]=new Font(numeric?"Bahnschrift":"Microsoft YaHei",size,
            bold?FontStyle.Bold:FontStyle.Regular,GraphicsUnit.Pixel);
        return FontCache[key];
    }
    static void Text(Graphics g,string value,float x,float y,float size,Color c,bool bold=false,bool num=false,int align=0) {
        using(Brush b=new SolidBrush(c)) using(StringFormat sf=new StringFormat(StringFormat.GenericTypographic)) {
            sf.FormatFlags|=StringFormatFlags.NoWrap;
            sf.Alignment=align==1?StringAlignment.Far:align==2?StringAlignment.Center:StringAlignment.Near;
            g.DrawString(value,Font(size,bold,num),b,new PointF(x,y),sf);
        }
    }
    static void Line(Graphics g,PointF a,PointF b,Color c,float width=1) {
        using(Pen p=new Pen(c,width)) {p.StartCap=LineCap.Round;p.EndCap=LineCap.Round;g.DrawLine(p,a,b);}
    }
    static void Circle(Graphics g,PointF p,float r,Color c) {
        using(Brush b=new SolidBrush(c))g.FillEllipse(b,p.X-r,p.Y-r,r*2,r*2);
    }
    static void Ring(Graphics g,PointF p,float r,Color c,float width=1) {
        using(Pen pen=new Pen(c,width))g.DrawEllipse(pen,p.X-r,p.Y-r,r*2,r*2);
    }
    static double Noise(int x,int y) {
        uint h=unchecked((uint)(x*374761393+y*668265263));h=unchecked((h^(h>>13))*1274126177u);
        return ((h^(h>>16))&65535)/65535.0-.5;
    }
    static void Glow(Graphics g,float x,float y,float rx,float ry,Color c,int alpha) {
        // Cache dithered falloffs. Scaling changes only decoration, never chart data.
        int w=Math.Max(4,(int)Math.Ceiling(rx*2)),h=Math.Max(4,(int)Math.Ceiling(ry*2));
        string key=w+":"+h+":"+c.ToArgb()+":"+alpha;
        if(!GlowCache.TryGetValue(key,out Bitmap sprite)) {
            sprite=new Bitmap(w,h,PixelFormat.Format32bppArgb);
            BitmapData bits=sprite.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
            byte[] bytes=new byte[bits.Stride*h];double edge=Math.Exp(-3.8);
            for(int j=0;j<h;j++)for(int i=0;i<w;i++) {
                double dx=(i+.5-w/2.0)/(w/2.0),dy=(j+.5-h/2.0)/(h/2.0),r2=dx*dx+dy*dy;
                if(r2>=1)continue;int k=j*bits.Stride+i*4;
                bytes[k]=c.B;bytes[k+1]=c.G;bytes[k+2]=c.R;
                bytes[k+3]=(byte)Math.Clamp((int)Math.Round(alpha*(Math.Exp(-3.8*r2)-edge)/(1-edge)+Noise(i,j)),0,255);
            }
            Marshal.Copy(bytes,0,bits.Scan0,bytes.Length);sprite.UnlockBits(bits);GlowCache[key]=sprite;
        }
        g.DrawImage(sprite,new RectangleF(x-rx,y-ry,rx*2,ry*2),new RectangleF(0,0,w,h),GraphicsUnit.Pixel);
    }
    static Graphics Configure(Bitmap bitmap) {
        Graphics g=Graphics.FromImage(bitmap);g.SmoothingMode=SmoothingMode.AntiAlias;
        g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
        g.CompositingQuality=CompositingQuality.HighQuality;g.ScaleTransform(SS,SS);return g;
    }
    static void BuildBackground() {
        Background=new Bitmap(W*SS,H*SS,PixelFormat.Format32bppArgb);
        using(Graphics g=Configure(Background)) {
            using(LinearGradientBrush b=new LinearGradientBrush(new Rectangle(0,0,W,H),Hex("050914"),Hex("050D1B"),35f))g.FillRectangle(b,0,0,W,H);
            Glow(g,1120,560,1050,600,Hex("1A3E79"),35);Glow(g,1230,510,690,330,Hex("245C98"),29);
            Glow(g,430,580,340,220,Hex("103761"),24);Glow(g,1650,420,420,260,Hex("8B4B43"),15);
            Random r=new Random(3185);
            for(int i=0;i<15000;i++)Circle(g,new PointF((float)r.NextDouble()*W,(float)r.NextDouble()*H),.36f,A(Hex("B4C9E5"),r.Next(2,9)));
            Line(g,new PointF(90,278),new PointF(1830,278),A(Muted,36));
            Line(g,new PointF(90,278),new PointF(320,278),A(Gold,145));
            Line(g,new PointF(90,864),new PointF(1830,864),A(Muted,34),.8f);
        }
    }
    static int Lower(DateTime d) {
        int l=0,r=Daily.Count;while(l<r){int m=(l+r)/2;if(Daily[m].Date<d)l=m+1;else r=m;}return l;
    }
    static int Upper(DateTime d) {
        int l=0,r=Daily.Count;while(l<r){int m=(l+r)/2;if(Daily[m].Date<=d)l=m+1;else r=m;}return l;
    }
    static void BuildArcs() {
        foreach(Segment seg in Timeline) {
            seg.First=Lower(seg.From);seg.Last=Lower(seg.To);seg.Arc=new double[seg.Last-seg.First+1];
            for(int i=seg.First+1;i<=seg.Last;i++) {
                // For the approved demo retain the original end-of-segment projection exactly.
                // Other periods use a local rolling projection so multi-year edges cannot explode offscreen.
                bool demo=seg.Start>=72&&seg.End<=92;
                double approximateTime=seg.Start+(seg.End-seg.Start)*(i-seg.First)/(double)(seg.Last-seg.First);
                DateTime end=demo?seg.To:Daily[i].Date;double months=demo?24:MonthsAt(approximateTime);
                State reference=new State{Zoom=1,ViewEnd=end,ViewStart=StartAt(end,months),AxisMax=demo?AxisAt(72):AxisAt(approximateTime)};
                PointF a=P(Daily[i-1].Date,Daily[i-1].Close,reference),b=P(Daily[i].Date,Daily[i].Close,reference);
                double length=Math.Sqrt((b.X-a.X)*(b.X-a.X)+(b.Y-a.Y)*(b.Y-a.Y));
                seg.Arc[i-seg.First]=seg.Arc[i-seg.First-1]+Math.Max(length,1e-8);
            }
        }
    }
    static State At(int frame) {return AtTime(frame/(double)Fps);}
    static State AtTime(double time) {
        double t=Math.Clamp(time,0,TotalFrames/(double)Fps);
        Segment seg=Timeline.FirstOrDefault(s=>t>=s.Start&&t<s.End)??Timeline.Last();
        double u=Travel(t-seg.Start,seg.End-seg.Start),cursor=seg.First;
        if(seg.Last>seg.First) {
            double target=seg.Arc[seg.Arc.Length-1]*u;int j=Array.BinarySearch(seg.Arc,target);
            if(j>=0)cursor=seg.First+j;
            else {j=~j;cursor=seg.First+j-1+(target-seg.Arc[j-1])/(seg.Arc[j]-seg.Arc[j-1]);}
        }
        if(Math.Abs(cursor-Math.Round(cursor))<1e-9)cursor=Math.Round(cursor);
        int from=(int)Math.Floor(cursor),to=(int)Math.Ceiling(cursor);double alpha=cursor-from;
        DateTime end=Daily[from].Date.AddTicks((long)((Daily[to].Date-Daily[from].Date).Ticks*alpha));
        double months=MonthsAt(t);DateTime start=StartAt(end,months);
        double overview=Smoother((t-OverviewStart)/(OverviewEnd-OverviewStart-1.0/Fps));
        if(overview>0)start=start.AddTicks((long)((Daily[0].Date-start).Ticks*overview));
        double price=Daily[from].Close+(Daily[to].Close-Daily[from].Close)*alpha;
        double zoom;
        if(t<72)zoom=1+.012*Math.Pow(Math.Sin(Math.PI*t/72),2);
        else if(t<90)zoom=1+.018*Smoother((t-72)/18);
        else if(t<92)zoom=1.018-.035*Smoother((t-90)/2);
        else if(t<97)zoom=.983+.017*Smoother((t-92)/5);
        else if(t<117)zoom=1+.010*Smoother((t-97)/20);
        else zoom=1.010-.035*Smoother((t-117)/3);
        return new State {Index=to,Completed=from,RevealFrom=from,RevealTo=to,RevealAlpha=alpha,Cursor=cursor,RevealClose=price,
            First=Lower(start),T=t,Zoom=zoom,ViewStart=start,ViewEnd=end,AxisMax=AxisAt(t),Window=months};
    }
    static PointF World(double x,double y,double z,State s) {
        double yaw=17*Math.PI/180,pitch=14*Math.PI/180;
        double xx=x*Math.Cos(yaw)+z*Math.Sin(yaw),zz=-x*Math.Sin(yaw)+z*Math.Cos(yaw);
        double yy=y*Math.Cos(pitch)-zz*Math.Sin(pitch),q=22/(22+zz*Math.Cos(pitch)+y*Math.Sin(pitch));
        double sx=965+100*xx*q,sy=772-100*yy*q;
        return new PointF((float)(965+(sx-965)*s.Zoom),(float)(600+(sy-600)*s.Zoom));
    }
    static PointF P(DateTime day,double price,State s,double z=0) {
        double u=(day-s.ViewStart).TotalDays/(s.ViewEnd-s.ViewStart).TotalDays;
        return World((u-.5)*16,price/s.AxisMax*4.2,z,s);
    }
    static PointF HeadPoint(State s) {return P(s.ViewEnd,s.RevealClose,s);}
    static void Header(Graphics g,State s) {
        Sample d=Daily[s.Index];double prev=s.Index==0?d.Close:Daily[s.Index-1].Close,delta=d.Close-prev,pct=(d.Close/prev-1)*100;
        using(Brush b=new SolidBrush(Gold))g.FillRectangle(b,90,60,4,23);
        float nameSize=27;
        while(nameSize>16&&TextWidth(InstrumentLabel,nameSize,true,false)+TextWidth(Symbol,17,false,true)>530)nameSize--;
        Text(g,InstrumentLabel,110,54,nameSize,White,true);
        Text(g,Symbol,110+TextWidth(InstrumentLabel,nameSize,true,false)+20,64,17,Muted,false,true);
        Text(g,d.Date.Year.ToString(),85,98,112,White,false,true);
        Text(g,d.Date.ToString("MM.dd",Inv),367,170,31,A(White,205),false,true);
        Text(g,s.T>=72&&s.T<92?DemoReferenceDate.Year+"—"+ThresholdDate.Year+" 片段  ·  日收盘":RangeLabel+"  ·  真实日收盘",94,232,20,Muted);
        Text(g,"收盘（"+Unit+"）",1818,61,20,Muted,false,false,1);
        Text(g,d.Close.ToString("N2",Inv),1828,98,104,Cream,false,true,1);
        string daily=s.Index==0?"—":(delta>=0?"▲ ":"▼ ")+Math.Abs(delta).ToString("N2",Inv)+"    "+(pct>=0?"+":"−")+Math.Abs(pct).ToString("F2",Inv)+"%";
        Text(g,daily,1819,217,27,s.Index==0?Muted:delta>=0?Red:Teal,false,true,1);
        Text(g,"当日",1467,224,17,Muted,false,false,1);
        Chapter chapter=Chapters.LastOrDefault(c=>s.T>=c.Start)??Chapters[0];
        Caption caption=Captions.LastOrDefault(c=>s.T>=c.Start)??Captions[0];
        Text(g,"CHAPTER "+chapter.Number.ToString("D2",Inv),960,70,17,Gold,false,true,2);
        double fade=CaptionOpacity(s.T);bool low=caption.Sub.Contains("下跌")||caption.Title=="急跌";
        Text(g,caption.Title,960,116,38,A(White,(int)(255*fade)),true,false,2);
        string sub=caption.Sub;
        if(s.T>=74&&s.T<78)sub="较 "+DemoReferenceDate.ToString("yyyy.MM.dd",Inv)+"  "+((d.Close/DemoReferenceClose-1)*100).ToString("+0.00;−0.00;0.00",Inv)+"%";
        Text(g,sub,960,181,20,A(low?Teal:Gold,(int)(255*fade)),false,false,2);
    }
    static void Grid(Graphics g,State s) {
        int step=s.AxisMax<=1500?500:s.AxisMax<=5000?1000:2000;
        for(int v=step;v<=s.AxisMax;v+=step) {
            PointF a=P(s.ViewStart,v,s),b=P(s.ViewEnd,v,s);bool major=Math.Abs(v-Threshold)<.0001&&s.T>=72&&s.T<92;
            using(Pen pen=new Pen(A(major?Gold:Muted,major?65:22),.75f)) {
                if(major)pen.DashPattern=new[]{5f,7f};g.DrawLine(pen,a,b);
            }
            Text(g,v.ToString("N0",Inv),a.X-19,a.Y-8,17,A(Muted,180),false,true,1);
        }
        if(s.T>=72&&s.T<92&&Threshold>0&&Threshold<s.AxisMax&&Math.Abs(Threshold/step-Math.Round(Threshold/step))>.0001) {
            using(Pen pen=new Pen(A(Gold,65),.75f)) {pen.DashPattern=new[]{5f,7f};g.DrawLine(pen,P(s.ViewStart,Threshold,s),P(s.ViewEnd,Threshold,s));}
        }
        for(int k=0;k<4;k++)Line(g,World(-8,0,.15+k*.4,s),World(8,0,.15+k*.4,s),A(Muted,k==0?48:17),.8f);
        DateTime mark=new DateTime(s.ViewStart.Year,s.ViewStart.Month,1);
        while(mark<s.ViewStart)mark=mark.AddMonths(1);
        double years=(s.ViewEnd-s.ViewStart).TotalDays/365.25;
        while(mark<=s.ViewEnd) {
            bool tick=years<=2.2?(mark.Month==1||mark.Month==7):years<=10?mark.Month==1:mark.Month==1&&mark.Year%5==0;
            if(tick) {
                PointF p=P(mark,0,s);Line(g,p,P(mark,0,s,1.5),A(Muted,30),.8f);
                Text(g,mark.ToString(years<=2.2?"yyyy.MM":"yyyy",Inv),Math.Clamp(p.X,220,1775),Math.Min(810,p.Y+16),17,A(Muted,185),false,true,2);
            }
            mark=mark.AddMonths(1);
        }
    }
    static Color Phase(int i) {return Daily[i].Close>=Daily[Math.Max(0,i-Lookback)].Close?Hex("D66C78"):Hex("4CD5B5");}
    static void Curtains(Graphics g,State s) {
        // A short window has fewer samples than the approved multi-decade still;
        // use daily ribbons to retain its fine light-curtain texture.
        for(int i=Math.Max(Lookback,s.First-1);i<s.Completed+(s.RevealAlpha>0?1:0);i++) {
            int j=i+1;DateTime da=Daily[i].Date<s.ViewStart?s.ViewStart:Daily[i].Date;
            DateTime db=Daily[j].Date>s.ViewEnd?s.ViewEnd:Daily[j].Date;
            if(db<=da)continue;
            double span=(Daily[j].Date-Daily[i].Date).TotalDays;
            double pa=Daily[i].Close+(Daily[j].Close-Daily[i].Close)*(da-Daily[i].Date).TotalDays/span;
            double pb=Daily[i].Close+(Daily[j].Close-Daily[i].Close)*(db-Daily[i].Date).TotalDays/span;
            PointF a=P(da,pa,s),b=P(db,pb,s);
            PointF bottom=P(da,0,s),bottom2=P(db,0,s);Color c=Phase(i);
            using(GraphicsPath path=new GraphicsPath()) {
                path.AddPolygon(new[]{a,b,bottom2,bottom});
                using(LinearGradientBrush brush=new LinearGradientBrush(a,bottom,A(c,24),A(c,0)))g.FillPath(brush,path);
            }
            if(i%2==0)using(LinearGradientBrush brush=new LinearGradientBrush(a,bottom,A(c,55),A(c,0)))
                using(Pen pen=new Pen(brush,.55f))g.DrawLine(pen,a,bottom);
        }
        foreach(Mark e in Events)if(e.Index<=s.Completed&&e.Index>=s.First&&s.T>=e.Arrival) {
            PointF p=P(e.Date,e.Close,s),b=P(e.Date,0,s);Color c=e.Low?Teal:Gold;
            double fade=e.Arrival==0?1:Smoother((s.T-e.Arrival)/LabelFade);
            for(int k=7;k>=1;k--)using(LinearGradientBrush brush=new LinearGradientBrush(p,b,A(c,4+k/2),A(c,0)))
                using(Pen pen=new Pen(brush,(float)(k*1.8*fade)))if(fade>0)g.DrawLine(pen,p,b);
        }
    }
    static void Curve(Graphics g,State s) {
        List<PointF> points=new List<PointF>();
        if(s.First>0&&Daily[s.First].Date>s.ViewStart) {
            Sample a=Daily[s.First-1],b=Daily[s.First];
            double f=(s.ViewStart-a.Date).TotalDays/(b.Date-a.Date).TotalDays;
            points.Add(P(s.ViewStart,a.Close+(b.Close-a.Close)*f,s));
        }
        points.AddRange(Daily.Skip(s.First).Take(s.Completed-s.First+1).Select(d=>P(d.Date,d.Close,s)));
        if(s.RevealAlpha>0)points.Add(HeadPoint(s));
        PointF[] pts=points.ToArray();
        if(pts.Length<2)return;
        float[] widths={27,18,11,6,3,1.65f};int[] alphas={3,5,9,23,110,245};
        for(int i=0;i<widths.Length;i++)using(Pen pen=new Pen(A(i==5?Cream:Gold,alphas[i]),widths[i])) {
            pen.LineJoin=LineJoin.Round;pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;g.DrawLines(pen,pts);
        }
        PointF[] tail=pts.Skip(Math.Max(0,pts.Length-95)).ToArray();
        for(int j=0;j<3;j++)using(Pen pen=new Pen(A(Cream,9+j*8),10-j*3)) {pen.LineJoin=LineJoin.Round;g.DrawLines(pen,tail);}
    }
    static double Gaussian(Random r) {return Math.Sqrt(-2*Math.Log(Math.Max(1e-9,r.NextDouble())))*Math.Cos(2*Math.PI*r.NextDouble());}
    static void BuildSparks() {
        List<Mark> lows=Events.Where(e=>e.Low).ToList();
        for(int emitter=0;emitter<=lows.Count;emitter++) {
            Mark low=emitter==0?null:lows[emitter-1];bool isLow=low!=null;
            int rate=isLow?LowRate:ParticleRate;Random r=new Random(ParticleSeed+emitter*971);
            int pre=(int)Math.Ceiling(LifeMax*rate);double begin=isLow?low.Arrival:0;
            double finish=isLow?Math.Min(Duration,low.Arrival+Math.Max(6,low.Hold+3)):Duration;
            for(int n=isLow?0:-pre;begin+n/(double)rate<finish;n++) {
                double birth=begin+n/(double)rate;State at=AtTime(birth);
                double angle=r.NextDouble()*Math.PI*2,speed=SpeedMin+r.NextDouble()*(SpeedMax-SpeedMin);
                Sparks.Add(new Spark{Id=emitter*1000000+n+pre,Low=isLow,Born=birth,
                    Life=LifeMin+r.NextDouble()*(LifeMax-LifeMin),Vx=Math.Cos(angle)*speed,Vy=Math.Sin(angle)*speed*.7+.07,
                    Vz=(r.NextDouble()-.5)*.26,Phase=r.NextDouble()*Math.PI*2,
                    Ox=Gaussian(r)*.055,Oy=Gaussian(r)*.045,Oz=Gaussian(r)*.09,
                    Radius=.75+r.NextDouble()*.95,Alpha=105+r.NextDouble()*115,
                    AnchorDate=isLow?low.Date:at.ViewEnd,AnchorClose=isLow?low.Close:at.RevealClose});
            }
        }
        Sparks.Sort((a,b)=>a.Born.CompareTo(b.Born));
    }
    static int SparkLower(double born) {
        int l=0,r=Sparks.Count;while(l<r){int m=(l+r)/2;if(Sparks[m].Born<born)l=m+1;else r=m;}return l;
    }
    static (double x,double y,double z) SparkWorld(Spark p,double age,State s) {
        double baseX=((p.AnchorDate-s.ViewStart).TotalDays/(s.ViewEnd-s.ViewStart).TotalDays-.5)*16;
        double grow=1-Math.Exp(-age*4),turn=age*2.1;
        return (baseX+p.Ox+p.Vx*age+.09*(Math.Sin(p.Phase+turn)-Math.Sin(p.Phase))*grow,
            p.AnchorClose/s.AxisMax*4.2+p.Oy+p.Vy*age+.08*age*age+.07*(Math.Cos(p.Phase+turn)-Math.Cos(p.Phase))*grow,
            p.Oz+p.Vz*age+.045*Math.Sin(turn+p.Phase)*grow);
    }
    static IEnumerable<SparkState> ParticleStates(State s) {
        for(int particle=SparkLower(s.T-LifeMax);particle<Sparks.Count&&Sparks[particle].Born<=s.T;particle++) {
            Spark p=Sparks[particle];double age=s.T-p.Born;if(age<0||age>=p.Life)continue;
            var w=SparkWorld(p,age,s);PointF pos=World(w.x,w.y,w.z,s);
            var past=SparkWorld(p,Math.Max(0,age-TrailSeconds),s);PointF tail=World(past.x,past.y,past.z,s);
            double envelope=Smooth(age/.12)*(1-Smooth((age/p.Life-.35)/.65));
            // Slight twinkle changes brightness without making a particle vanish mid-flight.
            double edge=Smooth((pos.X-100)/24)*Smooth((1835-pos.X)/24)*Smooth((pos.Y-286)/22)*Smooth((831-pos.Y)/22);
            double alpha=p.Alpha*envelope*(.88+.12*Math.Sin(age*4+p.Phase))*edge;
            double zz=-w.x*Math.Sin(17*Math.PI/180)+w.z*Math.Cos(17*Math.PI/180);
            double q=22/(22+zz*Math.Cos(14*Math.PI/180)+w.y*Math.Sin(14*Math.PI/180));
            yield return new SparkState{id=p.Id,low=p.Low,age=age,lifetime=p.Life,x=w.x,y=w.y,z=w.z,
                screen_x=pos.X,screen_y=pos.Y,alpha=alpha,radius=p.Radius*s.Zoom*q,trail_x=tail.X,trail_y=tail.Y};
        }
    }
    static void Particles(Graphics g,State s) {
        foreach(SparkState p in ParticleStates(s)) {
            if(p.screen_x<100||p.screen_x>1834||p.screen_y<290||p.screen_y>827||p.alpha<1)continue;
            Color c=p.low?Teal:Gold;PointF pt=new PointF((float)p.screen_x,(float)p.screen_y);
            Line(g,new PointF((float)p.trail_x,(float)p.trail_y),pt,A(c,(int)(p.alpha*.34)),(float)(p.radius*.7));
            if(p.radius>1.2)Circle(g,pt,(float)p.radius*2,A(c,(int)(p.alpha*.08)));
            Circle(g,pt,(float)p.radius,A(c,(int)p.alpha));
        }
        Random r=new Random(ParticleSeed+77);
        for(int i=0;i<130;i++) {
            double x=-7+r.NextDouble()*14,y=(r.NextDouble()*4.0+s.T*(.025+r.NextDouble()*.04))%4.0;
            PointF p=World(x+.035*Math.Sin(s.T*.7+i),y,.3+r.NextDouble()*.6,s);
            Circle(g,p,.5f,A(Hex("A5D2EF"),r.Next(12,32)));
        }
    }
    static void Head(Graphics g,State s) {
        PointF p=HeadPoint(s);
        // Quantized alpha bounds the glow cache; geometry and data are unaffected.
        int breath=(int)Math.Round(2*Math.Sin(s.T*2*Math.PI*.65));
        Glow(g,p.X,p.Y,160,106,Gold,28+breath);Glow(g,p.X,p.Y,80,65,Gold,65+breath*2);
        Glow(g,p.X,p.Y,115,3,Cream,90);Glow(g,p.X,p.Y,4,57,Gold,65);
        Glow(g,p.X,p.Y,34,32,Gold,170);Glow(g,p.X,p.Y,13,13,Cream,210);
        Circle(g,p,4,Hex("FFFCEF"));Ring(g,p,10.5f+(float)Math.Sin(s.T*2*Math.PI*.65)*.35f,A(Gold,105),.7f);
        if(s.T>=88&&s.T<88.8) {
            double u=(s.T-88)/.8;Ring(g,p,(float)(13+u*85),A(Gold,(int)(125*(1-u))),.8f);
        }
    }
    static List<Mark> LabelMarks(State s) {
        List<Mark> visible=Events.Where(e=>e.Index<=s.Completed&&e.Index>=s.First&&s.T>=e.Arrival).ToList();
        if(s.T>=OverviewStart)return visible.Where(e=>e.Id==OverviewLowId||e.Id==OverviewHighId).ToList();
        if(s.T>=72&&s.T<92)return visible.Where(e=>e.Id.StartsWith("E")&&(e.Id!="E01"||s.T<78)).TakeLast(2).ToList();
        return visible.Where(e=>s.T<e.Arrival+Math.Max(7,e.Hold+4)).TakeLast(2).ToList();
    }
    sealed class LabelBox { public Mark Mark; public float X,Y,Width; }
    static readonly Dictionary<int,List<LabelBox>> LabelLayouts=new Dictionary<int,List<LabelBox>>();
    static readonly Dictionary<string,(float x,float y)> LabelOffsets=new Dictionary<string,(float x,float y)>();
    static (float x,float y) EventOffset(Mark e,bool overview) {
        string key=e.Id+(overview?":overview":":normal");
        if(LabelOffsets.TryGetValue(key,out var existing))return existing;
        double referenceTime=overview?Duration-1.0/Fps:e.Arrival+Math.Min(.3,Math.Max(.1,e.Hold*.25));
        State reference=AtTime(referenceTime);PointF anchor=P(e.Date,e.Close,reference);
        LabelBox box=ComputeLabelLayout(reference).FirstOrDefault(b=>b.Mark.Id==e.Id);
        var offset=box==null?(-42f,-139f):(box.X-anchor.X,box.Y-anchor.Y);LabelOffsets[key]=offset;return offset;
    }
    static List<LabelBox> LayoutLabels(State s) {
        int key=(int)Math.Round(s.T*Fps);if(LabelLayouts.TryGetValue(key,out List<LabelBox> found))return found;
        List<LabelBox> boxes=new List<LabelBox>();
        foreach(Mark e in LabelMarks(s)) {
            PointF p=P(e.Date,e.Close,s);float width=LabelWidth(e);var offset=EventOffset(e,false);
            if(s.T>=OverviewStart) {
                var final=EventOffset(e,true);double u=Smoother((s.T-OverviewStart)/(OverviewEnd-OverviewStart-1.0/Fps));
                offset=((float)(offset.x+(final.x-offset.x)*u),(float)(offset.y+(final.y-offset.y)*u));
            }
            boxes.Add(new LabelBox{Mark=e,X=Math.Clamp(p.X+offset.x,110+width,1780),Y=Math.Clamp(p.Y+offset.y,338,718),Width=width});
        }
        LabelLayouts[key]=boxes;return boxes;
    }
    static List<LabelBox> ComputeLabelLayout(State s) {
        List<PointF> curve=Daily.Skip(s.First).Take(Math.Max(0,s.Completed-s.First+1)).Select(d=>P(d.Date,d.Close,s)).ToList();
        if(s.RevealAlpha>0)curve.Add(HeadPoint(s));
        List<LabelBox> boxes=new List<LabelBox>();
        foreach(Mark e in LabelMarks(s)) {
            PointF p=P(e.Date,e.Close,s);bool high=e.Close/s.AxisMax>.67;
            float width=LabelWidth(e),preferredX=p.X-42,preferredY=high?p.Y+90:p.Y-139;
            if(e.Id=="E02")preferredY=p.Y-190;
            if(e.Id=="E03") {preferredX=p.X-62;preferredY=p.Y+135;}
            if(e.Id=="E04")preferredY=p.Y+65;
            if(s.T>=OverviewStart&&e.Id==OverviewLowId) {preferredX=p.X+155;preferredY=p.Y-150;}
            preferredX=Math.Clamp(preferredX,110+width,1780);preferredY=Math.Clamp(preferredY,338,718);
            float bestX=preferredX,bestY=preferredY;double best=double.MaxValue;
            float[] xs={preferredX,p.X-145,p.X-260,p.X+width+45,p.X+width+160};
            float[] ys={preferredY,p.Y-260,p.Y+135,p.Y-180,p.Y+45,345,420,535,650,718};
            foreach(float rawX in xs)foreach(float rawY in ys) {
                float x=Math.Clamp(rawX,110+width,1780),y=Math.Clamp(rawY,338,718);
                RectangleF rect=new RectangleF(x-width-15,y-12,width+30,108);
                double collision=0;
                // Penalize the visible line and its glow, including intersections between vertices.
                for(int i=0;i<curve.Count;i++) {
                    PointF q=curve[i];if(rect.Contains(q))collision+=1;
                    if(i>0) {
                        PointF prev=curve[i-1];float mx=(q.X+prev.X)*.5f,my=(q.Y+prev.Y)*.5f;
                        if(rect.Contains(mx,my))collision+=.5;
                    }
                }
                foreach(LabelBox other in boxes)if(rect.IntersectsWith(new RectangleF(other.X-other.Width-18,other.Y-15,other.Width+36,114)))collision+=1000;
                // The luminous endpoint needs breathing room from nearby text.
                PointF head=HeadPoint(s);if(rect.Contains(head.X,head.Y))collision+=600;
                double score=collision*100000+Math.Pow((x-preferredX)*.6,2)+Math.Pow(y-preferredY,2);
                if(score<best){best=score;bestX=x;bestY=y;}
            }
            boxes.Add(new LabelBox{Mark=e,X=bestX,Y=bestY,Width=width});
        }
        return boxes;
    }
    static readonly Dictionary<string,float> TextWidths=new Dictionary<string,float>();
    static float TextWidth(string text,float size,bool bold,bool num) {
        string key=text+":"+size+":"+bold+":"+num;
        if(!TextWidths.ContainsKey(key))using(Bitmap b=new Bitmap(1,1))using(Graphics g=Graphics.FromImage(b))using(StringFormat sf=new StringFormat(StringFormat.GenericTypographic))
            TextWidths[key]=g.MeasureString(text,Font(size,bold,num),int.MaxValue,sf).Width;
        return TextWidths[key];
    }
    static float LabelWidth(Mark e) {return Math.Max(TextWidth(e.Label,23,true,false),Math.Max(TextWidth(e.Date.ToString("yyyy.MM.dd",Inv),18,false,true),TextWidth(e.Close.ToString("N2",Inv),26,false,true)));}
    static object LabelState(LabelBox box) {
        return new {event_id=box.Mark.Id,x=box.X,y=box.Y,left=box.X-box.Width,top=box.Y,width=box.Width,height=92};
    }
    static double NodeOpacity(Mark e,State s) {
        double fade=e.Arrival==0?Smoother(s.T/LabelFade):Smoother((s.T-e.Arrival)/LabelFade);
        if(s.T>=OverviewStart)return fade;
        double expire=e.Arrival+Math.Max(7,e.Hold+4);
        Mark secondNewer=Events.Where(m=>m.Arrival>e.Arrival).Skip(1).FirstOrDefault();
        if(secondNewer!=null)expire=Math.Min(expire,secondNewer.Arrival);
        if(s.T>=72&&s.T<92) {
            if(e.Id=="E01")expire=78;
            else if(e.Id=="E02")expire=91;
            else expire=120;
        }
        return fade*(1-Smoother((s.T-expire+LabelFade)/LabelFade));
    }
    static void Nodes(Graphics g,State s) {
        List<Mark> visible=Events.Where(e=>e.Index<=s.Completed&&e.Index>=s.First&&s.T>=e.Arrival).ToList();
        foreach(Mark e in visible) {
            PointF p=P(e.Date,e.Close,s);Color co=e.Low?Teal:Gold;
            double fade=e.Arrival==0?1:Smoother((s.T-e.Arrival)/LabelFade);
            Ring(g,p,5.5f,A(co,(int)(180*fade)),1);Circle(g,p,2.2f,A(co,(int)(255*fade)));
        }
        List<LabelBox> labels=LayoutLabels(s);
        foreach(LabelBox box in labels) {
            Mark e=box.Mark;
            PointF p=P(e.Date,e.Close,s);Color co=e.Low?Teal:Gold;
            double fade=NodeOpacity(e,s);
            float x=box.X,y=box.Y;
            // Labels remain flat and only the connector uses projected coordinates.
            Text(g,e.Date.ToString("yyyy.MM.dd",Inv),x,y,18,A(co,(int)(220*fade)),false,true,1);
            Text(g,e.Label,x,y+27,23,A(White,(int)(255*fade)),true,false,1);
            Text(g,e.Close.ToString("N2",Inv),x,y+60,26,A(co,(int)(255*fade)),false,true,1);
            if(p.Y>y+99)Line(g,new PointF(x-12,y+91),new PointF(p.X,p.Y-11),A(co,(int)(95*fade)),.8f);
            else if(y>p.Y+20)Line(g,new PointF(p.X,p.Y+13),new PointF(x-12,y-8),A(co,(int)(95*fade)),.8f);
        }
    }
    static void Navigator(Graphics g,State s) {
        const float left=153,right=1732,top=884,bottom=933;
        Text(g,"长期参照",95,892,15,A(Muted,180));
        double days=Math.Max(1,(s.ViewEnd-Daily[0].Date).TotalDays);
        List<PointF> points=Daily.Take(s.Completed+1).Select(d=>new PointF((float)(left+(d.Date-Daily[0].Date).TotalDays/days*(right-left)),(float)(bottom-d.Close/s.AxisMax*(bottom-top)))).ToList();
        if(s.RevealAlpha>0)points.Add(new PointF(right,(float)(bottom-s.RevealClose/s.AxisMax*(bottom-top))));
        PointF[] pts=points.ToArray();
        if(pts.Length>1)using(Pen p=new Pen(A(Gold,145),1)) {p.LineJoin=LineJoin.Round;g.DrawLines(p,pts);}
        float windowX=Math.Clamp((float)(left+(s.ViewStart-Daily[0].Date).TotalDays/days*(right-left)),left,right);
        using(Brush b=new SolidBrush(A(Gold,10)))g.FillRectangle(b,windowX,top,right-windowX,bottom-top+5);
        Line(g,new PointF(left,bottom+6),new PointF(right,bottom+6),A(Muted,54),.6f);
        if(pts.Length>0)Circle(g,pts[pts.Length-1],2.5f,Cream);
        if(s.Completed>0)Line(g,new PointF(right,top-6),new PointF(right,bottom+9),A(Gold,140),.8f);
        Text(g,Daily[0].Date.Year.ToString(Inv),left,946,14,A(Muted,175),false,true);
        int spanYears=Daily[Daily.Count-1].Date.Year-Daily[0].Date.Year;
        int yearStep=spanYears>25?10:spanYears>12?5:spanYears>5?2:1;
        int firstYear=(Daily[0].Date.Year/yearStep+1)*yearStep;
        for(int yr=firstYear;yr<Daily[Daily.Count-1].Date.Year;yr+=yearStep) {
            DateTime tick=new DateTime(yr,1,1);if(tick>=s.ViewEnd)continue;
            float x=(float)(left+(tick-Daily[0].Date).TotalDays/days*(right-left));
            if(x>left+60&&x<right-90)Text(g,yr.ToString(),x,946,14,A(Muted,175),false,true,2);
        }
        Text(g,Daily[s.Index].Date.ToString("yyyy.MM",Inv),right,946,14,Gold,false,true,1);
        Caption caption=Captions.LastOrDefault(c=>s.T>=c.Start)??Captions[0];
        Text(g,s.T>=OverviewStart&&!string.IsNullOrWhiteSpace(Ending)?Ending:caption.Body,960,985,28,A(White,(int)(255*CaptionOpacity(s.T))),true,false,2);
        Text(g,Symbol+" · 日收盘（"+Unit+"） · "+DateRange+" · "+SourceLabel,960,1039,15,A(Muted,180),false,false,2);
        string window=s.T>=OverviewEnd-.05?"主图 完整历史":s.T>=OverviewStart?"主图 展开完整历史":"主图 "+s.Window.ToString("0",Inv)+" 个月";
        Text(g,window+"  ·  纵轴 0—"+s.AxisMax.ToString("N0",Inv)+" "+Unit,95,839,15,A(Muted,180));
        Text(g,"光幕：较 "+Lookback+" 个交易样本前  ·  红升 / 青降 · 样本不足不显示",1829,839,15,A(Muted,180),false,false,1);
        Context context=Contexts.LastOrDefault(c=>Daily[s.Index].Date>=c.Date&&Daily[s.Index].Date<=c.Until);
        if(context!=null&&s.T<OverviewStart)Text(g,context.Text,960,307,18,A(Muted,205),false,false,2);
    }
    static Bitmap Render(State s) {
        Stopwatch profile=Stopwatch.StartNew();long previous=0;
        bool timing=Environment.GetEnvironmentVariable("STOCKS_PROFILE")=="1";
        Action<string> lap=label=>{if(timing){long now=profile.ElapsedMilliseconds;Console.WriteLine(label+": "+(now-previous)+" ms");previous=now;}};
        using(Bitmap large=Background.Clone(new Rectangle(0,0,W*SS,H*SS),PixelFormat.Format32bppArgb)) {
            using(Graphics g=Configure(large)) {
                lap("background");
                GraphicsState saved=g.Save();g.SetClip(new RectangleF(90,286,1745,545),CombineMode.Intersect);
                Grid(g,s);lap("grid");Curtains(g,s);lap("curtains");Particles(g,s);lap("particles");Curve(g,s);lap("curve");Nodes(g,s);lap("nodes");g.Restore(saved);
                // Unclipped bloom fades naturally at the plot boundary; HUD is painted last.
                Head(g,s);Header(g,s);
                lap("head+HUD");
                Navigator(g,s);
                lap("navigator");
            }
            Bitmap final=new Bitmap(W,H,PixelFormat.Format32bppArgb);
            using(Graphics g=Graphics.FromImage(final)) {
                g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                g.CompositingQuality=CompositingQuality.HighQuality;g.CompositingMode=CompositingMode.SourceCopy;
                g.DrawImage(large,new Rectangle(0,0,W,H),0,0,W*SS,H*SS,GraphicsUnit.Pixel);
                lap("downsample");
            }
            return final;
        }
    }
    static void SaveStates(IEnumerable<int> frames) {
        using(StreamWriter writer=new StreamWriter(Path.Combine(OutDir,"frame_states.jsonl"),false,new System.Text.UTF8Encoding(false)))
        foreach(int f in frames) {
            State s=At(f);
            writer.WriteLine(JsonSerializer.Serialize(new {
                animation_version=3,frame=f,t=s.T,global_t=s.T,date=Daily[s.Index].Date.ToString("yyyy-MM-dd",Inv),
                close=Daily[s.Index].Close,prev_close=s.Index==0?(double?)null:Daily[s.Index-1].Close,
                quote_index=s.Index,completed_index=s.Completed,cursor=s.Cursor,
                reveal_from_index=s.RevealFrom,reveal_to_index=s.RevealTo,reveal_alpha=s.RevealAlpha,
                reveal_date=s.ViewEnd.ToString("O",Inv),reveal_close=s.RevealClose,
                head_x=HeadPoint(s).X,head_y=HeadPoint(s).Y,
                visible_first_date=Daily[Math.Min(s.First,s.Completed)].Date.ToString("yyyy-MM-dd",Inv),visible_last_date=Daily[s.Completed].Date.ToString("yyyy-MM-dd",Inv),
                visible_count=Math.Max(0,s.Completed-s.First+1),navigator_count=s.Completed+1,
                view_start=s.ViewStart.ToString("O",Inv),view_end=s.ViewEnd.ToString("O",Inv),zoom=s.Zoom,
                axis_max=s.AxisMax,window_months=s.Window,
                caption_start=(Captions.LastOrDefault(c=>s.T>=c.Start)??Captions[0]).Start,
                active_event_ids=LabelMarks(s).Select(e=>e.Id).ToArray(),labels=LayoutLabels(s).Select(box=>LabelState(box)).ToArray(),
                particles=ParticleStates(s).ToArray()
            }));
        }
    }
    static void Encode(State[] states,int first,int count,string ffmpeg,string audio,bool videoOnly,string encoder) {
        string output=Path.Combine(OutDir,videoOnly?"chunk_"+first.ToString("D6",Inv)+".mp4":count==TotalFrames?"video.mp4":"preview.mp4");
        ProcessStartInfo psi=new ProcessStartInfo(ffmpeg){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardError=true,CreateNoWindow=true};
        var args=new List<string>{"-hide_banner","-loglevel","warning","-y","-f","rawvideo","-pixel_format","bgra","-video_size",W+"x"+H,
            "-framerate",Fps.ToString(),"-i","pipe:0"};
        if(!videoOnly)args.AddRange(new[]{"-i",audio});
        args.AddRange(new[]{"-map","0:v:0"});
        if(!videoOnly)args.AddRange(new[]{"-map","1:a:0"});
        args.AddRange(new[]{"-frames:v",count.ToString(),"-c:v",encoder});
        if(encoder=="h264_nvenc")args.AddRange(new[]{"-preset","p5","-rc","vbr","-cq","17","-b:v","14M","-maxrate","24M","-bufsize","28M"});
        else if(encoder=="libx264")args.AddRange(new[]{"-preset","fast","-crf","18"});
        else throw new ArgumentException("Supported encoders: libx264, h264_nvenc");
        args.AddRange(new[]{"-pix_fmt","yuv420p","-r",Fps.ToString(),"-fps_mode","cfr"});
        if(videoOnly)args.Add("-an");else args.AddRange(new[]{"-c:a","aac","-b:a","192k","-ar","48000","-ac","2"});
        args.AddRange(new[]{"-t",(count/(double)Fps).ToString("F6",Inv),"-movflags","+faststart",output});
        foreach(string a in args)psi.ArgumentList.Add(a);
        using(Process process=Process.Start(psi)) {
            var errors=process.StandardError.ReadToEndAsync();byte[] bytes=new byte[W*H*4];Stopwatch clock=Stopwatch.StartNew();
            try {
                for(int frame=first;frame<first+count;frame++) {
                    using(Bitmap bitmap=Render(states[frame])) {
                        BitmapData bits=bitmap.LockBits(new Rectangle(0,0,W,H),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
                        if(bits.Stride!=W*4)throw new InvalidDataException("Unexpected BGRA stride");
                        Marshal.Copy(bits.Scan0,bytes,0,bytes.Length);bitmap.UnlockBits(bits);
                        process.StandardInput.BaseStream.Write(bytes,0,bytes.Length);
                    }
                    if((frame-first)%60==0)Console.WriteLine("Rendered "+(frame-first)+" / "+count+" frames; "+clock.Elapsed.TotalSeconds.ToString("F1",Inv)+" s");
                }
            } finally {process.StandardInput.Close();}
            process.WaitForExit();string log=errors.GetAwaiter().GetResult();File.WriteAllText(Path.Combine(OutDir,"encode.log"),log);
            if(process.ExitCode!=0)throw new Exception("FFmpeg: "+log);
            Console.WriteLine("Encoded "+output+" in "+clock.Elapsed.TotalSeconds.ToString("F1",Inv)+" s");
        }
    }
    public static void Run(string data,string project,string outputDir,string ffmpeg,string audio,string frames,int renderFrames,int startFrame,bool videoOnly,string encoder) {
        Reset();OutDir=outputDir;Directory.CreateDirectory(OutDir);Directory.CreateDirectory(Path.Combine(OutDir,"keyframes"));
        using(JsonDocument doc=JsonDocument.Parse(File.ReadAllText(data)))
            foreach(JsonElement d in doc.RootElement.GetProperty("daily").EnumerateArray())
                Daily.Add(new Sample{Date=Date(d.GetProperty("date").GetString()),Close=d.GetProperty("close").GetDouble()});
        if(Daily.Count<2||Daily.Any(d=>!double.IsFinite(d.Close)||d.Close<=0)||Daily.Zip(Daily.Skip(1),(a,b)=>a.Date>=b.Date).Any(x=>x))throw new InvalidDataException("Daily data needs at least two increasing, unique trading dates with finite positive closes");
        using(JsonDocument doc=JsonDocument.Parse(File.ReadAllText(project))) {
            JsonElement root=doc.RootElement,video=root.GetProperty("video"),chart=root.GetProperty("chart");
            if(video.GetProperty("width").GetInt32()!=W||video.GetProperty("height").GetInt32()!=H)throw new Exception("Unsupported canvas");
            Fps=video.GetProperty("fps").GetInt32();TotalFrames=video.GetProperty("frames").GetInt32();Duration=TotalFrames/(double)Fps;
            if(Fps!=30||TotalFrames!=3600)throw new InvalidDataException("cinematic120 requires 1920x1080, 30 fps and 3600 frames");
            JsonElement instrument=root.GetProperty("instrument");
            InstrumentName=StringValue(instrument,"name","指数");Symbol=StringValue(instrument,"symbol","INDEX");Unit=StringValue(instrument,"unit","点");
            root.TryGetProperty("display",out JsonElement display);
            InstrumentLabel=StringValue(display,"instrument_label",InstrumentName);
            RangeLabel=StringValue(display,"range_label",Daily[0].Date.Year+"—"+Daily[Daily.Count-1].Date.Year);
            SourceLabel=StringValue(display,"source","来源见项目说明");Ending=StringValue(display,"ending","");
            MaxPrice=chart.GetProperty("y_max").GetDouble();
            Lookback=chart.GetProperty("phase_lookback").GetInt32();
            foreach(JsonElement x in chart.GetProperty("axis_transitions").EnumerateArray())Axes.Add(new AxisTransition{Start=x.GetProperty("start").GetDouble(),End=x.GetProperty("end").GetDouble(),From=x.GetProperty("from").GetDouble(),To=x.GetProperty("to").GetDouble()});
            JsonElement window=chart.GetProperty("window_transition"),overview=chart.GetProperty("overview_transition");
            WindowTransitionStart=window.GetProperty("start").GetDouble();WindowTransitionEnd=window.GetProperty("end").GetDouble();WindowFrom=window.GetProperty("from_months").GetDouble();WindowTo=window.GetProperty("to_months").GetDouble();
            OverviewStart=overview.GetProperty("start").GetDouble();OverviewEnd=overview.GetProperty("end").GetDouble();
            foreach(JsonElement x in root.GetProperty("chapters").EnumerateArray())Chapters.Add(new Chapter{Start=x.GetProperty("start").GetDouble(),End=x.GetProperty("end").GetDouble(),Number=x.GetProperty("number").GetInt32(),Title=x.GetProperty("title").GetString()});
            foreach(JsonElement x in root.GetProperty("captions").EnumerateArray())Captions.Add(new Caption{Start=x.GetProperty("start").GetDouble(),End=x.GetProperty("end").GetDouble(),Title=x.GetProperty("title").GetString(),Sub=x.GetProperty("sub").GetString(),Body=x.GetProperty("caption").GetString()});
            foreach(JsonElement x in root.GetProperty("contexts").EnumerateArray())Contexts.Add(new Context{Date=Date(x.GetProperty("date").GetString()),Until=Date(x.GetProperty("until_date").GetString()),Text=x.GetProperty("text").GetString()});
            if(root.TryGetProperty("motion",out JsonElement motion)) {
                RampSeconds=motion.GetProperty("ramp_seconds").GetDouble();LabelFade=motion.GetProperty("label_fade_seconds").GetDouble();
            }
            if(root.TryGetProperty("particles",out JsonElement particles)) {
                ParticleSeed=particles.GetProperty("seed").GetInt32();ParticleRate=particles.GetProperty("rate").GetInt32();LowRate=particles.GetProperty("low_rate").GetInt32();
                LifeMin=particles.GetProperty("lifetime_min").GetDouble();LifeMax=particles.GetProperty("lifetime_max").GetDouble();
                SpeedMin=particles.GetProperty("speed_world_min").GetDouble();SpeedMax=particles.GetProperty("speed_world_max").GetDouble();TrailSeconds=particles.GetProperty("trail_seconds").GetDouble();
                if(ParticleRate<=0||LowRate<=0||LifeMin<=0||LifeMax<LifeMin||SpeedMin<0||SpeedMax<SpeedMin||TrailSeconds<0)throw new InvalidDataException("Invalid particle configuration");
            }
            if(RampSeconds<=0||LabelFade<=0)throw new InvalidDataException("Invalid motion configuration");
            foreach(JsonElement seg in root.GetProperty("timeline").EnumerateArray())Timeline.Add(new Segment {
                Start=seg.GetProperty("start").GetDouble(),End=seg.GetProperty("end").GetDouble(),
                From=Date(seg.GetProperty("from").GetString()),To=Date(seg.GetProperty("to").GetString())});
            foreach(JsonElement e in root.GetProperty("events").EnumerateArray()) {
                DateTime date=Date(e.GetProperty("date").GetString());int index=Lower(date);
                Mark m=new Mark{Id=e.GetProperty("id").GetString(),Label=e.GetProperty("label").GetString(),Date=date,
                    Close=e.GetProperty("close").GetDouble(),Arrival=e.GetProperty("arrival").GetDouble(),Hold=e.GetProperty("hold").GetDouble(),Low=e.GetProperty("low").GetBoolean(),Index=index};
                if(index>=Daily.Count||Daily[index].Date!=date||Math.Abs(Daily[index].Close-m.Close)>.0001)throw new InvalidDataException("Event mismatch "+date);
                Events.Add(m);
            }
            if(Events.Count==0||Timeline.Count==0||Captions.Count==0||Chapters.Count==0)throw new InvalidDataException("Events, timeline, captions and chapters cannot be empty");
            Mark reference=Events.FirstOrDefault(e=>e.Id=="E01")??Events[0];
            Mark thresholdEvent=Events.FirstOrDefault(e=>e.Id=="E03")??Events[Events.Count-1];
            root.TryGetProperty("comparisons",out JsonElement comparisons);
            DemoReferenceDate=Date(StringValue(comparisons,"demo_reference_date",reference.Date.ToString("yyyy-MM-dd",Inv)));
            ThresholdDate=Date(StringValue(comparisons,"threshold_date",thresholdEvent.Date.ToString("yyyy-MM-dd",Inv)));
            Threshold=comparisons.ValueKind==JsonValueKind.Object&&comparisons.TryGetProperty("threshold",out JsonElement threshold)?threshold.GetDouble():thresholdEvent.Close;
            int referenceIndex=Lower(DemoReferenceDate);
            if(referenceIndex>=Daily.Count||Daily[referenceIndex].Date!=DemoReferenceDate)throw new InvalidDataException("Comparison reference must be a trading date");
            DemoReferenceClose=Daily[referenceIndex].Close;
            OverviewLowId=Events.OrderBy(e=>e.Close).First().Id;OverviewHighId=Events.OrderByDescending(e=>e.Close).First().Id;
        }
        if(startFrame<0||renderFrames<=0||startFrame+renderFrames>TotalFrames)throw new ArgumentException("Frame range exceeds preset timeline");
        foreach(Segment seg in Timeline)if(seg.End<=seg.Start||seg.To<seg.From||Lower(seg.From)>=Daily.Count||Lower(seg.To)>=Daily.Count||Daily[Lower(seg.From)].Date!=seg.From||Daily[Lower(seg.To)].Date!=seg.To)throw new InvalidDataException("Timeline dates must exist in the daily series");
        BuildArcs();BuildSparks();
        State[] states=Enumerable.Range(0,TotalFrames).Select(f=>At(f)).ToArray();
        int[] requested=string.IsNullOrWhiteSpace(frames)?Enumerable.Range(startFrame,renderFrames).ToArray():frames.Split(',').Select(int.Parse).ToArray();
        if(requested.Any(f=>f<0||f>=TotalFrames))throw new ArgumentException("Requested keyframe outside timeline");
        SaveStates(requested);BuildBackground();
        try {
            if(!string.IsNullOrWhiteSpace(frames)) {
                foreach(int frame in frames.Split(',').Select(int.Parse)) {
                    using(Bitmap b=Render(states[frame])) {
                        string path=Path.Combine(OutDir,"keyframes","frame_"+frame.ToString("D6",Inv)+".png");b.Save(path,ImageFormat.Png);Console.WriteLine(path);
                    }
                }
            } else {
                if(startFrame+renderFrames>TotalFrames)throw new ArgumentException("Frame range exceeds the full timeline");
                if(!videoOnly&&startFrame!=0)throw new ArgumentException("A nonzero start frame requires video-only chunk rendering");
                Encode(states,startFrame,renderFrames,ffmpeg,audio,videoOnly,encoder);
            }
        } finally {
            Background.Dispose();foreach(Bitmap b in GlowCache.Values)b.Dispose();foreach(Font f in FontCache.Values)f.Dispose();
        }
    }
}
