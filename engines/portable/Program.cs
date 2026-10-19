using System.Text.Json;
using StockVideo.Drawing;
using SkiaSharp;

try {
    if(args.Length==1 && args[0]=="doctor") {
        var families=SKFontManager.Default.FontFamilies.ToHashSet();
        using var font=new Font("Microsoft YaHei",24,FontStyle.Regular,GraphicsUnit.Pixel);
        using var bitmap=new Bitmap(64,64);
        using(var canvas=Graphics.FromImage(bitmap)) canvas.Clear(System.Drawing.Color.Black);
        Console.WriteLine(JsonSerializer.Serialize(new { backend="skia", version=typeof(SKCanvas).Assembly.GetName().Version.ToString(),
            chinese_font=families.Contains(Font.TextFamily), number_font=families.Contains(Font.NumberFamily),
            text_family=Font.TextFamily, number_family=Font.NumberFamily }));
        return 0;
    }
    if(args.Length<4) throw new ArgumentException("render DATA PROJECT OUTPUT FFMPEG ENCODER START COUNT | covers DATA PROJECT OUTPUT");
    var command=args[0];var data=args[1];var project=args[2];var output=args[3];
    using var doc=JsonDocument.Parse(File.ReadAllText(project));var root=doc.RootElement;
    if(root.TryGetProperty("fonts",out var fonts)) {
        string Get(string key)=>fonts.TryGetProperty(key,out var v)?v.GetString():null;
        Font.TextFamily=Get("text_family")??Font.TextFamily;Font.NumberFamily=Get("number_family")??Font.NumberFamily;
        Font.TextFile=Get("text_file");Font.NumberFile=Get("number_file");
    }
    if(command=="covers") KospiPublishCovers.Render(data,output,project);
    else if(command=="render" && args.Length==8) {
        Directory.CreateDirectory(output);
        int first=int.Parse(args[6]),count=int.Parse(args[7]);
        double resolution=root.TryGetProperty("render",out var settings)&&settings.TryGetProperty("preview_scale",out var value)?value.GetDouble():1;
        if(resolution!=1 && resolution!=.5 && resolution!=.25) throw new ArgumentException("Invalid preview scale");
        ReusableCinema.RenderScale=resolution;ReusableClassicRenderer.RenderScale=resolution;
        if(root.GetProperty("preset").GetString()=="cinematic120")
            ReusableCinema.Run(data,project,output,args[4],"","",count,first,true,args[5]);
        else ReusableClassicRenderer.Run(data,Path.Combine(output,$"chunk_{first:D6}.mp4"),"",output,"",false,first,count,args[4],args[5],
            root.TryGetProperty("render",out var render)&&render.TryGetProperty("content_scale",out var scale)?scale.GetDouble():1.0);
    } else throw new ArgumentException("Unknown renderer command");
    return 0;
} catch(Exception e) {Console.Error.WriteLine(e);return 1;}
