using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace FindIt.Runtime;

internal static class PdfDocument
{
    internal sealed record Page(int Number,string Text,byte[] Image,byte[] Thumbnail);
    private static readonly object Gate=new();
    private static bool initialized;
    internal static IEnumerable<Page> Read(string path,CancellationToken token)
    {
        lock(Gate){if(!initialized){FPDF_InitLibrary();initialized=true;}}
        using var file=MemoryMappedFile.CreateFromFile(path,FileMode.Open,null,0,MemoryMappedFileAccess.Read);
        using var accessor=file.CreateViewAccessor(0,0,MemoryMappedFileAccess.Read);
        IntPtr address=Acquire(accessor);
        var document=FPDF_LoadMemDocument64(address,(nuint)accessor.Capacity,null);
        try
        {
            if(document==IntPtr.Zero)throw new InvalidDataException("PDFium cannot open this PDF (it may be encrypted or damaged).");
            var pages=FPDF_GetPageCount(document);
            for(var number=0;number<pages;number++)
            {
                token.ThrowIfCancellationRequested();var page=FPDF_LoadPage(document,number);
                try
                {
                    if(page==IntPtr.Zero)throw new InvalidDataException($"Cannot read PDF page {number+1}.");
                    var words="";var text=FPDFText_LoadPage(page);
                    try{if(text!=IntPtr.Zero){var length=FPDFText_CountChars(text);var buffer=new ushort[length+1];var count=FPDFText_GetText(text,0,length,buffer);if(count>1)words=System.Text.Encoding.Unicode.GetString(MemoryMarshal.AsBytes(buffer.AsSpan(0,count-1)));}}
                    finally{if(text!=IntPtr.Zero)FPDFText_ClosePage(text);}
                    var width=FPDF_GetPageWidthF(page);var height=FPDF_GetPageHeightF(page);var scale=1600d/Math.Max(width,height);
                    var w=Math.Max(1,(int)(width*scale));var h=Math.Max(1,(int)(height*scale));var bitmap=FPDFBitmap_Create(w,h,0);
                    try
                    {
                        if(bitmap==IntPtr.Zero)throw new OutOfMemoryException("Cannot allocate PDF rendering bitmap.");
                        FPDFBitmap_FillRect(bitmap,0,0,w,h,0xffffffff);FPDF_RenderPageBitmap(bitmap,page,0,0,w,h,0,1);
                        using var bgra=Mat.FromPixelData(h,w,MatType.CV_8UC4,FPDFBitmap_GetBuffer(bitmap),FPDFBitmap_GetStride(bitmap));
                        using var bgr=new Mat();Cv2.CvtColor(bgra,bgr,ColorConversionCodes.BGRA2BGR);Cv2.ImEncode(".jpg",bgr,out var full,[new ImageEncodingParam(ImwriteFlags.JpegQuality,85)]);
                        using var thumb=new Mat();Cv2.Resize(bgr,thumb,new Size(Math.Max(1,w*384/Math.Max(w,h)),Math.Max(1,h*384/Math.Max(w,h))));Cv2.ImEncode(".jpg",thumb,out var small);
                        yield return new(number+1,words,full,small);
                    }
                    finally{if(bitmap!=IntPtr.Zero)FPDFBitmap_Destroy(bitmap);}
                }
                finally{if(page!=IntPtr.Zero)FPDF_ClosePage(page);}
            }
        }
        finally{if(document!=IntPtr.Zero)FPDF_CloseDocument(document);accessor.SafeMemoryMappedViewHandle.ReleasePointer();}
    }
    private static unsafe IntPtr Acquire(MemoryMappedViewAccessor accessor){byte* pointer=null;accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);return (IntPtr)(pointer+accessor.PointerOffset);}
    private const string Dll="pdfium";
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void FPDF_InitLibrary();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr FPDF_LoadMemDocument64(IntPtr buffer,nuint size,[MarshalAs(UnmanagedType.LPUTF8Str)]string? password);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern int FPDF_GetPageCount(IntPtr document);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr FPDF_LoadPage(IntPtr document,int number);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern int FPDFText_CountChars(IntPtr text);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern int FPDFText_GetText(IntPtr text,int start,int count,[Out]ushort[] buffer);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void FPDFText_ClosePage(IntPtr text);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr FPDFBitmap_Create(int width,int height,int alpha);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void FPDFBitmap_FillRect(IntPtr bitmap,int left,int top,int width,int height,uint color);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)]private static extern void FPDF_RenderPageBitmap(IntPtr bitmap,IntPtr page,int x,int y,int width,int height,int rotate,int flags);
}
