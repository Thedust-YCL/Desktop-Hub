using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopHub.Core;
using DesktopHub.Windows;

internal static class Program
{
    private static int assertions;
    [STAThread] private static void Main()
    {
        var application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        Exception? failure = null;
        application.Startup += async (_, _) =>
        {
            var window = new System.Windows.Window { Width=100,Height=100,Left=-20000,Top=-20000,ShowInTaskbar=false,WindowStyle=System.Windows.WindowStyle.None,AllowsTransparency=true,Opacity=0 };
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
            try { await RunAsync(); } catch(Exception ex) { failure=ex; }
            finally { window.Close(); application.Shutdown(); }
        };
        application.Run();
        if (Environment.GetEnvironmentVariable("TASK7_FINALIZE_CHECK") == "1") { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Console.WriteLine("DIAGNOSTIC FINALIZATION COMPLETED"); }
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts/usability-development/task-7-fixtures", Guid.NewGuid().ToString("N")));
        if (!root.Contains("task-7-fixtures", StringComparison.Ordinal)) throw new Exception("Unsafe fixture root");
        Directory.CreateDirectory(root);
        if (Environment.GetEnvironmentVariable("TASK7_MEMORY_PROBE") == "1") { await MemoryProbe(root); return; }
        if (Environment.GetEnvironmentVariable("TASK7_PDF_PROBE") is { } probe) { await PdfProbe(root,probe.Replace("-allgc",""));if(probe.EndsWith("-allgc")){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Console.WriteLine("PROBE AFTER SCOPE GC");} return; }
        using var library = new ResourceLibraryService(Path.Combine(root, "metadata"));
        using var previews = new ResourcePreviewService(library);
        await StrideBudgetRegression(library, previews);
        if (Environment.GetEnvironmentVariable("TASK7_STRIDE_ONLY") == "1") { Console.WriteLine($"PASS {assertions} stride assertions"); return; }
        RasterBudgetRegression();
        var animatedGif=await previews.RenderAsync(Register(library,Path.GetFullPath("tests/DesktopHub.Windows.Preview.Tests/Fixtures/many.gif")).Id,false,default);
        var firstGifPixel=Pixel(animatedGif,0,0);
        Check(animatedGif.Kind=="gif" && firstGifPixel.R>180 && firstGifPixel.G is >80 and <120 && firstGifPixel.B<80,"forty-frame GIF still previews its real first frame with demand decoding");
        await Reject(() => previews.RenderAsync(Register(library,Path.GetFullPath("tests/DesktopHub.Windows.Preview.Tests/Fixtures/highdepth.png")).Id,false,default),"64-bit PNG source is rejected before its 25.6 MB decoded buffer");
        foreach (var json in new[] { "{}", "{\"resourceId\":\"a\",\"requestId\":\"r\",\"thumbnail\":1}", "{\"resourceId\":\"a\",\"requestId\":\"r\",\"thumbnail\":false,\"path\":\"C:/x\"}", "{\"resourceId\":\"a\",\"requestId\":\"r\",\"thumbnail\":false,\"thumbnail\":true}" })
            Throws<ArgumentException>(() => PreviewRequestParser.Content(JsonDocument.Parse(json).RootElement), "strict preview fields");
        await Reject(() => previews.RenderAsync("unknown", false, default), "unregistered ID");
        var file = Path.Combine(root, "known.png");
        WriteImage(file, new PngBitmapEncoder());
        var resource = Register(library, file);
        var requestingThread = Environment.CurrentManagedThreadId;
        using(var workerProbe = new ResourcePreviewService(library,(stream,extension,thumbnail,token)=> {
            Check(Environment.CurrentManagedThreadId != requestingThread,"native decoding runs off requesting UI thread");
            return Task.FromResult(new PreviewResult([137],1,1,"png","worker scheduling probe"));
        })) await workerProbe.StartRenderAsync(new object(),"worker",resource.Id,false);
        var image = await previews.RenderAsync(resource.Id, false, default);
        Check(image.Width == 40 && image.Height == 20 && image.Png[0] == 137, "known PNG dimensions/signature");
        Check(Pixel(image, 0, 0) == Colors.Red, "known PNG pixel");
        var first = await previews.RenderAsync(resource.Id, true, default);
        Check(previews.CacheBytes > 0 && previews.CacheBytes <= 33554432, "retained byte budget");
        WriteImage(file, new PngBitmapEncoder(), Colors.Blue);
        var changed = await previews.RenderAsync(resource.Id, true, default);
        Check(Pixel(changed, 0, 0) == Colors.Blue, "cache uses live mtime/length");
        library.Store.Update(data => data.Resources.Single(r => r.Id == resource.Id).Status = "Missing");
        await Reject(() => previews.RenderAsync(resource.Id, true, default), "cache does not grant stale authorization");
        library.Store.Update(data => data.Resources.Single(r => r.Id == resource.Id).Status = "Active");
        File.Move(file, file + ".moved");
        await Reject(() => previews.RenderAsync(resource.Id, true, default), "moved file cache rejected");
        File.Move(file + ".moved", file);
        foreach (var (extension, encoder) in new (string, BitmapEncoder)[] { ("jpg", new JpegBitmapEncoder()), ("bmp", new BmpBitmapEncoder()), ("gif", new GifBitmapEncoder()) })
        {
            var path = Path.Combine(root, "known." + extension); WriteImage(path, encoder);
            var rendered = await previews.RenderAsync(Register(library, path).Id, false, default);
            Check(rendered.Width == 40 && rendered.Height == 20, "real " + extension);
            var pixel = Pixel(rendered, 0, 0); Check(pixel.R > 240 && pixel.G < 20 && pixel.B < 20, extension + " color");
        }
        var corrupt = Path.Combine(root, "corrupt.png"); File.WriteAllText(corrupt, "not a PNG");
        await Reject(() => previews.RenderAsync(Register(library, corrupt).Id, false, default), "corrupt rejected");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Reject(() => previews.RenderAsync(resource.Id, false, cancelled.Token), "cancelled request");
        var pdf = Path.Combine(root, "two-pages.pdf"); WritePdf(pdf);
        if (Environment.GetEnvironmentVariable("TASK7_SKIP_PDF") != "1") {
        var pdfResult = await previews.RenderAsync(Register(library, pdf).Id, false, default);
        Console.WriteLine($"PDF actual PNG: {pdfResult.Width}x{pdfResult.Height}; {pdfResult.Detail}");
        Check(pdfResult.Kind == "pdf" && pdfResult.Width <= 1200 && pdfResult.Height <= 1200 && Math.Abs((double)pdfResult.Width / pdfResult.Height - 2) < 0.02 && pdfResult.Detail.Contains("2"), "native PDF first page size/page count");
        Check(Pixel(pdfResult, 200, 100).R > 245 && Pixel(pdfResult, 200, 100).B < 10, "native PDF first page red marker (page two blue)");
        var pdfThumbnail = await previews.RenderAsync(Register(library, CopyFixture(pdf,root,"thumbnail.pdf")).Id,true,default);
        Check(pdfThumbnail.Width<=256 && pdfThumbnail.Height<=256 && Pixel(pdfThumbnail,pdfThumbnail.Width/2,pdfThumbnail.Height/2).R>245,"PDF thumbnail obeys actual pixel budget");
        var damagedPdf=Path.Combine(root,"damaged.pdf");File.WriteAllText(damagedPdf,"%PDF-1.7 damaged without objects");
        await Reject(()=>previews.RenderAsync(Register(library,damagedPdf).Id,false,default),"damaged PDF rejected without native lifetime failure");
        var encryptedPdf=CopyFixture(Path.GetFullPath("tests/DesktopHub.Windows.Preview.Tests/Fixtures/encrypted.pdf"),root,"encrypted.pdf");
        await Reject(()=>previews.RenderAsync(Register(library,encryptedPdf).Id,false,default),"encrypted PDF rejected without asking for password");
        await Task.Run(()=>NativePdfCancellation(ResourceScanner.Read(pdf)));
        }
        if (Environment.GetEnvironmentVariable("TASK7_SKIP_WEBP") != "1") {
        foreach (var lossless in new[] { true, false })
        {
            var webp = Path.Combine(root, lossless ? "lossless.webp" : "lossy.webp");
            RunTool("cwebp.exe", (lossless ? new[] { "-lossless", file, "-o", webp } : new[] { "-q", "95", file, "-o", webp }));
            var output = await previews.RenderAsync(Register(library, webp).Id, false, default);
            Check(output.Width == 40 && output.Height == 20 && Pixel(output, 0, 0).B > 240, "native WebP " + lossless);
        }
        var alphaPng = Path.Combine(root,"alpha.png"); WriteImage(alphaPng,new PngBitmapEncoder(),Color.FromArgb(128,255,0,0));
        var alphaWebp = Path.Combine(root,"alpha.webp");RunTool("cwebp.exe","-lossless",alphaPng,"-o",alphaWebp);
        var alpha = await previews.RenderAsync(Register(library,alphaWebp).Id,false,default);Check(Pixel(alpha,1,1).A == 128 && Pixel(alpha,1,1).R == 255,"WebP alpha preserved");
        foreach(var blend in new[]{"+b","-b"})
        {
            var animation=Path.Combine(root,blend=="+b"?"blend.webp":"replace.webp");
            RunTool("webpmux.exe","-frame",alphaWebp,"+100+10+4+0"+blend,"-frame",Path.Combine(root,"lossless.webp"),"+100+0+0+0+b","-bgcolor","255,0,255,0","-o",animation);
            var frame=await previews.RenderAsync(Register(library,animation).Id,false,default);
            Check(frame.Width==50 && frame.Height==24,"animated WebP logical canvas/offset");Check(Pixel(frame,0,0)==Colors.Lime,"animated WebP background");
            var pixel=Pixel(frame,20,10);Check(blend=="+b"?pixel.A==255&&pixel.R is >=127 and <=129&&pixel.G is >=126 and <=128:pixel.A==128&&pixel.R==255&&pixel.G==0,"animated WebP alpha blend/no-blend first frame");
        }
        }
        File.SetAttributes(file,FileAttributes.Offline);
        await Reject(()=>previews.RenderAsync(resource.Id,true,default),"live cloud offline attribute rejects hot cache without read");File.SetAttributes(file,FileAttributes.Normal);
        var huge = Path.Combine(root,"huge.png");var hugeBytes=File.ReadAllBytes(file);System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hugeBytes.AsSpan(16,4),int.MaxValue);System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hugeBytes.AsSpan(20,4),int.MaxValue);File.WriteAllBytes(huge,hugeBytes);
        await Reject(()=>previews.RenderAsync(Register(library,huge).Id,false,default),"source dimension overflow rejected before allocation");
        if(Environment.GetEnvironmentVariable("TASK7_SKIP_POLICY")!="1")await PolicyTests(library, resource);
        File.Delete(pdf); Check(!File.Exists(pdf), "PDF handle released at task completion");
        Console.WriteLine($"PASS {assertions} preview assertions; fixtures: {root}; cache={previews.CacheBytes}");
    }
    internal static Resource Register(ResourceLibraryService library, string file)
    { var r = ResourceScanner.Read(file); r.ManualTarget = true; library.Store.Update(data => data.Resources.Add(r)); return r; }
    internal static void WriteImage(string path, BitmapEncoder encoder, Color? color = null)
    { var pixels = new byte[40 * 20 * 4]; var c = color ?? Colors.Red; for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = c.A; } encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(40, 20, 96, 96, PixelFormats.Bgra32, null, pixels, 160))); using var output = File.Create(path); encoder.Save(output); }
    private static Color Pixel(PreviewResult result, int x, int y)
    { using var stream = new MemoryStream(result.Png); var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); byte[] bytes = new byte[4]; converted.CopyPixels(new System.Windows.Int32Rect(x,y,1,1), bytes,4,0); return Color.FromArgb(bytes[3],bytes[2],bytes[1],bytes[0]); }
    private static string CopyFixture(string source,string root,string name) {var target=Path.Combine(root,name);File.Copy(source,target);return target;}
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativeCancel();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NativePdf(SafeFileHandle input,int edge,NativeCancel cancel,out nint bytes,out nuint count,out int width,out int height,out uint pages);
    private static void NativePdfCancellation(Resource resource)
    {
        using var input=NativePreviewFile.Open(resource);
        var module=NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory,"ThirdParty","WebP","desktop_hub_webp.dll"));
        try
        {
            var render=Marshal.GetDelegateForFunctionPointer<NativePdf>(NativeLibrary.GetExport(module,"dh_pdf_render"));int checks=0;
            NativeCancel cancel=()=>++checks>=2?1:0;
            var status=render(input.Stream.SafeFileHandle,1200,cancel,out var bytes,out _,out _,out _,out _);
            try {Check(status<0 && checks>=2 && bytes==0,"native PDF cancellation after load starts completes with no escaped buffer");}
            finally {if(bytes!=0)Marshal.FreeCoTaskMem(bytes);}
            input.Verify();
        }
        finally {NativeLibrary.Free(module);}
    }
    private static async Task MemoryProbe(string root)
    {
        using var library=new ResourceLibraryService(Path.Combine(root,"metadata"));using var previews=new ResourcePreviewService(library);
        var pdf=Path.Combine(root,"memory.pdf");WritePdf(pdf);
        foreach(var path in new[]{Path.GetFullPath("tests/DesktopHub.Windows.Preview.Tests/Fixtures/highentropy.png"),pdf})
        {
            var resource=Register(library,path);using var process=Process.GetCurrentProcess();process.Refresh();var before=process.PrivateMemorySize64;var sampledPeak=before;
            var watch=Stopwatch.StartNew();var task=previews.RenderAsync(resource.Id,false,default);
            while(!task.IsCompleted){process.Refresh();sampledPeak=Math.Max(sampledPeak,process.PrivateMemorySize64);await Task.Delay(10);}
            var result=await task;process.Refresh();sampledPeak=Math.Max(sampledPeak,process.PrivateMemorySize64);
            Console.WriteLine(JsonSerializer.Serialize(new{format=Path.GetExtension(path),encodedInputBytes=new FileInfo(path).Length,result.Width,result.Height,encodedOutputBytes=result.Png.Length,beforePrivate=before,sampledPeakPrivate=sampledPeak,afterPrivate=process.PrivateMemorySize64,sampledDelta=sampledPeak-before,pollMs=10,elapsedMs=watch.ElapsedMilliseconds,totalPrivateHardCap=false,controlledDecodedBudget=16777216}));
        }
    }
    private static async Task Reject(Func<Task<PreviewResult>> action, string name) { try { await action(); } catch (Exception ex) when (ex is ArgumentException or IOException or OperationCanceledException or NotSupportedException or InvalidOperationException) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
    private static async Task StrideBudgetRegression(ResourceLibraryService library, ResourcePreviewService previews)
    {
        var tall = Register(library, Path.GetFullPath("tests/DesktopHub.Windows.Preview.Tests/Fixtures/rgb48-odd-overbudget.png"));
        try { await previews.RenderAsync(tall.Id, false, default); throw new Exception("Expected aligned RGB48 source-buffer budget rejection"); }
        catch (IOException error) when (error.Message.Contains("预算", StringComparison.Ordinal)) { Check(true,"DWORD-aligned 43x65000 RGB48 source is rejected before decode"); }
        var smallPath = Path.GetFullPath("tests/DesktopHub.Windows.Preview.Tests/Fixtures/rgb48-small.png");
        using (var input = File.OpenRead(smallPath))
        {
            var frame = PreviewDecoder.CreateRasterDecoder(input).Frames[0];
            Check(frame.Format.BitsPerPixel == 48 && frame.PixelWidth == 43 && frame.PixelHeight == 65,"positive fixture is actual RGB48 with an odd row width");
        }
        var small = await previews.RenderAsync(Register(library, smallPath).Id, false, default);
        var pixel = Pixel(small, 0, 0);
        Check(small.Width == 43 && small.Height == 65 && pixel.R == 255 && pixel.G == 0 && pixel.B == 0,"within-budget RGB48 PNG remains a real red preview");
    }
    private static void RasterBudgetRegression()
    {
        using var input = File.OpenRead("tests/DesktopHub.Windows.Preview.Tests/Fixtures/many.gif");
        var decoder = PreviewDecoder.CreateRasterDecoder(input);
        var frames = decoder.Frames;
        var cached = frames.Count(frame => {
            for(Type? type=frame.GetType();type is not null;type=type.BaseType)
            {
                var property=type.GetProperty("IsSourceCached",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.DeclaredOnly);
                if(property is not null)return property.GetValue(frame) is true;
            }
            throw new Exception("WPF cache diagnostic property unavailable");
        });
        var decodedLowerBound=frames.Where(frame => {
            for(Type? type=frame.GetType();type is not null;type=type.BaseType)
            {
                var property=type.GetProperty("IsSourceCached",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.DeclaredOnly);
                if(property is not null)return property.GetValue(frame) is true;
            }
            throw new Exception("WPF cache diagnostic property unavailable");
        }).Sum(frame => checked((long)frame.PixelWidth*frame.PixelHeight*frame.Format.BitsPerPixel/8));
        Console.WriteLine($"GIF actual frames={frames.Count}, cached={cached}, decoded pixel lower bound={decodedLowerBound}");
        Check(frames.Count==40 && cached<=1 && decodedLowerBound<=16777216,"animated GIF demand decodes at most the requested first frame");
    }
    private static void Throws<T>(Action action, string name) where T : Exception { try { action(); } catch(T) { Check(true,name); return; } throw new Exception("Expected rejection: " + name); }
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); ++assertions; }
    private static void RunTool(string tool, params string[] args)
    { var start = new ProcessStartInfo(Path.GetFullPath(Path.Combine("artifacts/usability-development/task-7-research/libwebp-1.6.0-windows-x64/bin", tool))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true }; foreach (var arg in args) start.ArgumentList.Add(arg); using var process = Process.Start(start)!; var error = process.StandardError.ReadToEnd(); process.WaitForExit(); if (process.ExitCode != 0) throw new Exception(error); }
    private static void WritePdf(string path)
    {
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] /Contents 5 0 R >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] /Contents 6 0 R >>", "<< /Length 26 >>\nstream\n1 0 0 rg 0 0 400 200 re f\nendstream", "<< /Length 26 >>\nstream\n0 0 1 rg 0 0 400 200 re f\nendstream" };
        using var stream = File.Create(path); void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text)); Write("%PDF-1.4\n"); var offsets = new List<long> { 0 }; for (var i = 0; i < objects.Length; ++i) { offsets.Add(stream.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); } var xref = stream.Position; Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n"); foreach (var offset in offsets.Skip(1)) Write($"{offset:0000000000} 00000 n \n"); Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
    private static async Task PdfProbe(string root,string mode)
    {
        var path=Path.Combine(root,"probe.pdf");WritePdf(path);
        if(mode is "encode-then-render" or "png-then-render" or "gif-then-render" or "jpg-then-render") {
            var ext=mode=="gif-then-render"?"gif":mode=="jpg-then-render"?"jpg":"png";
            var imagePath=Path.Combine(root,"before."+ext);WriteImage(imagePath,ext=="gif"?new GifBitmapEncoder():ext=="jpg"?new JpegBitmapEncoder():new PngBitmapEncoder());
            if(mode!="encode-then-render") {using var library=new ResourceLibraryService(Path.Combine(root,"before-meta"));using var previews=new ResourcePreviewService(library);var result=await previews.RenderAsync(Register(library,imagePath).Id,false,default);Console.WriteLine("PROBE IMAGE "+Pixel(result,0,0));}
        }
        if(mode=="raster-then-render") {
            var visual=new System.Windows.Media.DrawingVisual();using(var drawing=visual.RenderOpen())drawing.DrawRectangle(Brushes.Red,null,new System.Windows.Rect(0,0,40,20));
            var target=new RenderTargetBitmap(40,20,96,96,PixelFormats.Pbgra32);target.Render(visual);target.Freeze();Console.WriteLine("PROBE WPF RASTER PASS");
        }
        if(mode is "service" or "pixels" or "service-delete" or "service-gc") {
            using var library=new ResourceLibraryService(Path.Combine(root,"meta"));using var previews=new ResourcePreviewService(library);
            var result=await previews.RenderAsync(Register(library,path).Id,false,default);Console.WriteLine($"PROBE SERVICE PASS {result.Width}x{result.Height}");
            if(mode=="pixels")Console.WriteLine("PROBE PIXELS "+Pixel(result,200,100));
            if(mode=="service-delete"){File.Delete(path);Console.WriteLine("PROBE DELETE PASS");}
            if(mode=="service-gc"){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Console.WriteLine("PROBE GC PASS");}return;
        }
        using var stream=File.OpenRead(path);using var input=stream.AsRandomAccessStream();
        var document=await Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(input);
        if(mode=="inspect") {foreach(var field in document.GetType().GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public))Console.WriteLine("DOCFIELD "+field.Name+" "+field.FieldType+" "+field.GetValue(document)?.GetType());foreach(var property in typeof(WinRT.IWinRTObject).GetProperties())Console.WriteLine("WINRTPROP "+property.Name+" "+property.PropertyType);return;}
        if(mode=="load"){Console.WriteLine("PROBE LOAD PASS");return;}
        using var page=document.GetPage(0);using var output=new Windows.Storage.Streams.InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output,new Windows.Data.Pdf.PdfPageRenderOptions {DestinationWidth=400,DestinationHeight=200,BitmapEncoderId=Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId});
        Console.WriteLine("PROBE RENDER PASS "+output.Size);
        if(mode=="reader") {var bytes=new byte[(int)output.Size];using var read=output.GetInputStreamAt(0);using var reader=new Windows.Storage.Streams.DataReader(read);await reader.LoadAsync((uint)bytes.Length);reader.ReadBytes(bytes);Console.WriteLine("PROBE READER PASS "+bytes.Length);}
    }
    private static async Task PolicyTests(ResourceLibraryService library, Resource resource)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int active = 0, peak = 0, calls = 0;
        async Task<PreviewResult> Decode(Stream stream, string extension, bool thumbnail, CancellationToken token)
        { var count = Interlocked.Increment(ref active); Interlocked.Increment(ref calls); int prior; do { prior = peak; } while (count > prior && Interlocked.CompareExchange(ref peak, count, prior) != prior); if (count == 2) started.TrySetResult(); try { await release.Task; token.ThrowIfCancellationRequested(); return new(new byte[8388608], 1, 1, "png", "test controlled decoder"); } finally { Interlocked.Decrement(ref active); } }
        using var service = new ResourcePreviewService(library, Decode); var ownerA = new object(); var ownerB = new object();
        var a = service.StartRenderAsync(ownerA, "a", resource.Id, true); var b = service.StartRenderAsync(ownerB, "b", resource.Id, true); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = service.StartRenderAsync(ownerA, "queue", resource.Id, true); service.CancelOwner(ownerA); release.SetResult();
        await Reject(() => a, "owner cancelled active request"); await Reject(() => queued, "owner cancelled queued request"); await b;
        Check(peak == 2 && calls == 2, "global concurrent two and queued cancellation");
        var c = service.StartRenderAsync(ownerB, "reuse", resource.Id, false); await c; service.Cancel(ownerB, "reuse"); await service.StartRenderAsync(ownerB, "reuse", resource.Id, false); Check(true,"completed IDs reusable without disposed CTS race");
        // Each retained value is 8 MiB. Five distinct live keys must evict below 32 MiB.
        for (var i = 0; i < 5; i++) { File.SetLastWriteTimeUtc(resource.CanonicalPath, DateTime.UtcNow.AddMinutes(i + 1)); await service.RenderAsync(resource.Id, true, default); }
        Check(service.CacheBytes == 33554432, "LRU evicts actual retained PNG bytes");
        library.Store.Update(data => data.Resources.Single(r => r.Id == resource.Id).ManualTarget = false);
        await Reject(() => service.RenderAsync(resource.Id, true, default), "source removal revokes hot cache");
    }
}

