using System.Drawing.Imaging;

namespace PrismOS.Rendering;

/// <summary>Writes a captured still so a reader can never see half a file — and so a reader
/// holding the previous still open cannot make the write fail.
///
/// GDI+'s <c>Image.Save</c> straight onto the target throws "a generic error occurred in GDI+"
/// when anything has the file open (the lock-screen converter and the settings preview both read
/// these stills). Saving beside it and moving over the top is atomic for readers and immune to
/// that lock.</summary>
internal static class StillWriter
{
    public static void SavePng(System.Drawing.Bitmap bitmap, string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        bitmap.Save(temp, ImageFormat.Png);
        // A reader may hold the previous still for a few milliseconds (the settings backdrop
        // decodes it on open). Give it that long rather than failing the capture.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                System.IO.File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                System.Threading.Thread.Sleep(40);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                System.Threading.Thread.Sleep(40);
            }
        }
    }
}
