using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace DesktopHub.Windows;

internal static class PreviewDecoder
{
    private const int EncodedLimit = 16 * 1024 * 1024;
    private static readonly Lazy<nint> Webp = new(() => NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "ThirdParty", "WebP", "desktop_hub_webp.dll")));
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WebpInfo(byte[] input, nuint length, out int width, out int height);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WebpRender(byte[] input, nuint length, int width, int height, byte[] output, nuint lengthOut);
    internal static async Task<PreviewResult> RenderAsync(Stream input, string extension, bool thumbnail, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); extension = extension.ToLowerInvariant();
        if (extension == ".pdf") return await PdfAsync(input, thumbnail, token).ConfigureAwait(false);
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")) throw new NotSupportedException("此格式暂不支持内容预览，可在外部应用打开。");
        if (input.Length is <= 0 or > EncodedLimit) throw new IOException("文件超过预览读取预算，请在外部应用打开。");
        if (extension == ".webp") return WebP(input, thumbnail, token);
        var header = Dimensions(input, extension); input.Position = 0;
        var bytesPerPixel = SourceBytesPerPixel(input, extension);
        // WIC source rows are DWORD aligned; RGB48 odd widths include two padding bytes per row.
        var sourceStride = checked(((long)header.Width * bytesPerPixel + 3) / 4 * 4);
        if (sourceStride > (ResourcePreviewService.DecodedBudget - 32) / header.Height) throw new IOException("图片源尺寸或像素位深超过解码预算，请在外部应用打开。");
        var sourceBytes = sourceStride * header.Height;
        input.Position = 0;
        var size = Fit(header.Width, header.Height, thumbnail ? 256 : 1600);
        // Reserve 64-bit scaled pixels plus converted region, managed region/canvas and encoding copy.
        // This covers all known simultaneous buffers; decoder-internal allocations are separately reported.
        while (sourceBytes + checked((long)size.Width * size.Height * 24) > ResourcePreviewService.DecodedBudget)
        { size = Fit(size.Width, size.Height, Math.Max(1, Math.Max(size.Width, size.Height) - 32)); if (size.Width == 1 && size.Height == 1) break; }
        var decoder = CreateRasterDecoder(input);
        if (decoder.Frames.Count == 0) throw new IOException("图片没有可预览的首帧。");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || (long)frame.PixelWidth * frame.PixelHeight > sourceBytes / 4) throw new IOException("图片帧尺寸不符合安全头信息。");
        var actualStride = checked(((long)frame.PixelWidth * frame.Format.BitsPerPixel + 31) / 32 * 4);
        if (actualStride > sourceBytes / frame.PixelHeight) throw new IOException("图片实际像素位深或行对齐不符合安全头信息。");
        token.ThrowIfCancellationRequested();
        var offsetX = 0; var offsetY = 0; var background = Colors.Transparent;
        if (extension == ".gif" && frame.Metadata is BitmapMetadata metadata)
        {
            offsetX = QueryInt(metadata, "/imgdesc/Left"); offsetY = QueryInt(metadata, "/imgdesc/Top");
            if ((long)offsetX + frame.PixelWidth > header.Width || (long)offsetY + frame.PixelHeight > header.Height) throw new IOException("GIF 首帧超出逻辑画布。");
            var transparent = metadata.ContainsQuery("/grctlext/TransparencyFlag") && Convert.ToBoolean(metadata.GetQuery("/grctlext/TransparencyFlag"));
            input.Position = 11; var backgroundIndex = input.ReadByte();
            if (!transparent && decoder.Palette?.Colors is { } colors && backgroundIndex >= 0 && backgroundIndex < colors.Count) background = colors[backgroundIndex];
        }
        var left = (int)((long)offsetX * size.Width / header.Width); var top = (int)((long)offsetY * size.Height / header.Height);
        var right = Math.Min(size.Width, (int)(((long)(offsetX + frame.PixelWidth) * size.Width + header.Width - 1) / header.Width));
        var bottom = Math.Min(size.Height, (int)(((long)(offsetY + frame.PixelHeight) * size.Height + header.Height - 1) / header.Height));
        var target = Compose(frame, size.Width, size.Height, left, top, right - left, bottom - top, background, token);
        return Encode(target, extension[1..], extension == ".gif" ? "首帧" : "图片", token);
    }
    private static BitmapSource Compose(BitmapSource frame, int width, int height, int left, int top, int regionWidth, int regionHeight, Color background, CancellationToken token)
    {
        if (regionWidth <= 0 || regionHeight <= 0) throw new IOException("图片首帧尺寸无效。");
        BitmapSource scaled = frame;
        if (regionWidth != frame.PixelWidth || regionHeight != frame.PixelHeight) scaled = new TransformedBitmap(frame, new ScaleTransform((double)regionWidth / frame.PixelWidth, (double)regionHeight / frame.PixelHeight));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var copyWidth = Math.Min(regionWidth, converted.PixelWidth); var copyHeight = Math.Min(regionHeight, converted.PixelHeight);
        var region = new byte[checked(copyWidth * copyHeight * 4)]; converted.CopyPixels(new Int32Rect(0,0,copyWidth,copyHeight),region,copyWidth*4,0);
        var canvas = new byte[checked(width * height * 4)];
        for(var i=0;i<canvas.Length;i+=4) {canvas[i]=background.B;canvas[i+1]=background.G;canvas[i+2]=background.R;canvas[i+3]=background.A;}
        for(var y=0;y<copyHeight;y++)
        {
            token.ThrowIfCancellationRequested();
            for(var x=0;x<copyWidth;x++)
            {
                var s=(y*copyWidth+x)*4;var d=((top+y)*width+left+x)*4;var sa=region[s+3];var da=canvas[d+3];var alpha=sa*255+da*(255-sa);
                for(var channel=0;channel<3;channel++)canvas[d+channel]=alpha==0?(byte)0:(byte)((region[s+channel]*sa*255+canvas[d+channel]*da*(255-sa)+alpha/2)/alpha);
                canvas[d+3]=(byte)((alpha+127)/255);
            }
        }
        var result=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,canvas,width*4);result.Freeze();return result;
    }
    private static int QueryInt(BitmapMetadata metadata, string key) => metadata.ContainsQuery(key) ? Convert.ToInt32(metadata.GetQuery(key)) : 0;
    // Keep the validated stream alive through composition. OnLoad eagerly caches every GIF frame.
    internal static BitmapDecoder CreateRasterDecoder(Stream input) => BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
    private static PreviewResult WebP(Stream input, bool thumbnail, CancellationToken token)
    {
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var info = Marshal.GetDelegateForFunctionPointer<WebpInfo>(NativeLibrary.GetExport(Webp.Value, "dh_webp_info"));
        if (info(bytes, (nuint)bytes.Length, out var width, out var height) != 0) throw new IOException("WebP 文件损坏或源帧超过解码预算。");
        var size = Fit(width, height, thumbnail ? 256 : 1180);
        var output = new byte[checked(size.Width * size.Height * 4)];
        var render = Marshal.GetDelegateForFunctionPointer<WebpRender>(NativeLibrary.GetExport(Webp.Value, "dh_webp_render"));
        token.ThrowIfCancellationRequested();
        if (render(bytes, (nuint)bytes.Length, size.Width, size.Height, output, (nuint)output.Length) != 0) throw new IOException("WebP 首帧无法安全解码。");
        token.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null, output, size.Width * 4); bitmap.Freeze();
        return Encode(bitmap, "webp", "首帧", token);
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CancelCheck();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PdfRender(SafeFileHandle input, int edge, CancelCheck cancelled, out nint png, out nuint count, out int width, out int height, out uint pages);
    private static Task<PreviewResult> PdfAsync(Stream input, bool thumbnail, CancellationToken token)
    {
        // The native RAII boundary receives only the same validated read-only handle. No path reopen,
        // managed WinRT projection or COM object survives into CLR finalization.
        if (input is not FileStream file) throw new IOException("PDF 预览需要已验证的只读文件句柄。");
        var render = Marshal.GetDelegateForFunctionPointer<PdfRender>(NativeLibrary.GetExport(Webp.Value, "dh_pdf_render"));
        CancelCheck cancelled = () => token.IsCancellationRequested ? 1 : 0;
        nint png = 0;
        try
        {
            var status = render(file.SafeFileHandle, thumbnail ? 256 : 1200, cancelled, out png, out var length, out var width, out var height, out var pages);
            token.ThrowIfCancellationRequested();
            if (status < 0) throw new IOException("PDF 已损坏、加密或暂不可读取，可在外部应用打开。", Marshal.GetExceptionForHR(status));
            if (png == 0 || length is 0 or > EncodedLimit / 2 || width <= 0 || height <= 0 || Math.Max(width,height) > (thumbnail ? 256 : 1200) || checked((long)width * height * 8) > ResourcePreviewService.DecodedBudget) throw new IOException("PDF 首页超出预览预算。");
            var bytes = new byte[checked((int)length)]; Marshal.Copy(png, bytes, 0, bytes.Length);
            var actual = PngSize(bytes);
            if (actual.Width != width || actual.Height != height) throw new IOException("PDF 预览尺寸不一致。");
            return Task.FromResult(new PreviewResult(bytes, width, height, "pdf", $"首页 · 共 {pages} 页"));
        }
        finally { if (png != 0) Marshal.FreeCoTaskMem(png); }
    }
    private static PreviewResult Encode(BitmapSource bitmap, string kind, string detail, CancellationToken token)
    {
        using var output = new LimitedMemoryStream(EncodedLimit);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(output); token.ThrowIfCancellationRequested();
        return new(output.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight, kind, detail);
    }
    internal static (int Width, int Height) Fit(int width, int height, int edge)
    { if (width <= 0 || height <= 0) throw new IOException("图片尺寸无效。"); var scale = Math.Min(1, (double)edge / Math.Max(width, height)); return (Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale))); }
    private static (int Width, int Height) PngSize(ReadOnlySpan<byte> bytes)
    { if (bytes.Length < 24 || !bytes[..8].SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) throw new IOException("PNG 头信息无效。"); return (BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]), BinaryPrimitives.ReadInt32BigEndian(bytes[20..24])); }
    private static int SourceBytesPerPixel(Stream input, string extension)
    {
        input.Position = 0; Span<byte> header = stackalloc byte[32]; input.ReadExactly(header);
        if (extension == ".png")
        {
            var channels = header[25] switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new IOException("PNG 颜色类型无效。") };
            if (header[24] is not (1 or 2 or 4 or 8 or 16)) throw new IOException("PNG 像素位深无效。");
            return Math.Max(4, (channels * header[24] + 7) / 8);
        }
        if (extension == ".bmp")
        {
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(header[(BinaryPrimitives.ReadInt32LittleEndian(header[14..18]) == 12 ? 24 : 28)..]);
            if (bits is 0 or > 64) throw new IOException("BMP 像素位深不受支持。");
            return Math.Max(4, (bits + 7) / 8);
        }
        return 4;
    }
    private static (int Width, int Height) Dimensions(Stream stream, string extension)
    {
        Span<byte> head = stackalloc byte[32]; stream.ReadExactly(head);
        (int Width, int Height) size;
        if (extension == ".png") size = PngSize(head);
        else if (extension == ".gif") { if (!head[..6].SequenceEqual("GIF87a"u8) && !head[..6].SequenceEqual("GIF89a"u8)) throw new IOException("GIF 头信息无效。"); size = (BinaryPrimitives.ReadUInt16LittleEndian(head[6..8]), BinaryPrimitives.ReadUInt16LittleEndian(head[8..10])); }
        else if (extension == ".bmp") { if (head[0] != 'B' || head[1] != 'M') throw new IOException("BMP 头信息无效。"); var dib = BinaryPrimitives.ReadInt32LittleEndian(head[14..18]); var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(head[22..26]); if (signedHeight == int.MinValue) throw new IOException("BMP 尺寸无效。"); size = dib == 12 ? (BinaryPrimitives.ReadUInt16LittleEndian(head[18..20]), BinaryPrimitives.ReadUInt16LittleEndian(head[20..22])) : dib >= 40 ? (BinaryPrimitives.ReadInt32LittleEndian(head[18..22]), Math.Abs(signedHeight)) : throw new IOException("BMP 头信息无效。"); }
        else
        {
            if (head[0] != 255 || head[1] != 216) throw new IOException("JPEG 头信息无效。"); stream.Position = 2; size = default;
            while (stream.Position < stream.Length)
            {
                if (stream.ReadByte() != 255) throw new IOException("JPEG 标记损坏。"); int marker; do marker = stream.ReadByte(); while (marker == 255);
                if (marker is < 0 or 0xD9 or 0xDA) break;
                if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) continue;
                var high = stream.ReadByte(); var low = stream.ReadByte(); if (high < 0 || low < 0) break; var length = high * 256 + low;
                if (length < 2 || length - 2 > stream.Length - stream.Position) throw new IOException("JPEG 段长度无效。");
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC)) { Span<byte> frame = stackalloc byte[5]; stream.ReadExactly(frame); if (frame[0] != 8) throw new IOException("JPEG 像素位深不受支持。"); size = (BinaryPrimitives.ReadUInt16BigEndian(frame[3..5]), BinaryPrimitives.ReadUInt16BigEndian(frame[1..3])); break; }
                stream.Position += length - 2;
            }
        }
        if (size.Width <= 0 || size.Height <= 0) throw new IOException("图片尺寸无效。"); return size;
    }
    private sealed class LimitedMemoryStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { if (Position + count > limit) throw new IOException("预览编码超过预算。"); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { if (Position + buffer.Length > limit) throw new IOException("预览编码超过预算。"); base.Write(buffer); }
        public override void SetLength(long value) { if (value > limit) throw new IOException("预览编码超过预算。"); base.SetLength(value); }
    }
}

