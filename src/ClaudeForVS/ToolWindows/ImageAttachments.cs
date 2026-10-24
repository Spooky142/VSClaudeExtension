using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeForVS.Agent;

namespace ClaudeForVS.ToolWindows;

/// <summary>Turns pasted bitmaps and dropped image files into attachments small enough to send.</summary>
internal static class ImageAttachments
{
    // An image costs about width × height / 750 tokens, and newer models take larger ones without scaling them down.
    // At this size a full-screen screenshot is about 2-3K tokens and still readable.
    private const int MaxEdge = 1568;

    public static Attachment FromBitmap(BitmapSource bitmap, string name)
    {
        BitmapSource scaled = Fit(bitmap, MaxEdge);
        byte[] png = Encode(new PngBitmapEncoder(), scaled);
        PromptImage image = png.Length <= PromptImage.MaxBytes
            ? new PromptImage("image/png", png)
            : new PromptImage("image/jpeg", ShrinkToJpeg(scaled));
        return Attachment.ForImage(image, Thumbnail(scaled), name);
    }

    /// <summary>An attachment for an image file, or null if the file isn't a supported image or can't be read.</summary>
    public static Attachment? FromFile(string path)
    {
        if (PromptImage.MediaTypeFor(path) is not string mediaType)
        {
            return null;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            BitmapSource decoded = Decode(bytes);
            string name = Path.GetFileName(path);

            // Small enough and not oversized: send the file as it is, in its own format.
            if (bytes.Length <= PromptImage.MaxBytes && Math.Max(decoded.PixelWidth, decoded.PixelHeight) <= MaxEdge)
            {
                return Attachment.ForImage(new PromptImage(mediaType, bytes), Thumbnail(decoded), name);
            }

            return FromBitmap(decoded, name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource Fit(BitmapSource bitmap, int maxEdge)
    {
        int edge = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        if (edge <= maxEdge)
        {
            return bitmap;
        }

        double scale = (double)maxEdge / edge;
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static byte[] ShrinkToJpeg(BitmapSource bitmap)
    {
        for (int edge = Math.Max(bitmap.PixelWidth, bitmap.PixelHeight); ; edge = edge * 3 / 4)
        {
            byte[] jpeg = Encode(new JpegBitmapEncoder { QualityLevel = 85 }, Fit(bitmap, edge));
            if (jpeg.Length <= PromptImage.MaxBytes || edge < 400)
            {
                return jpeg;
            }
        }
    }

    private static byte[] Encode(BitmapEncoder encoder, BitmapSource bitmap)
    {
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static ImageSource Thumbnail(BitmapSource bitmap)
    {
        BitmapSource thumb = Fit(bitmap, 72);
        if (!thumb.IsFrozen && thumb.CanFreeze)
        {
            thumb.Freeze();
        }

        return thumb;
    }
}
