using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FCAT.Services;

/// <summary>
/// Renders a WPF element that was never shown on screen to an image on the clipboard.
///
/// Screenshotting the live panel would only capture what fits inside its ScrollViewer, so an
/// export builds its own copy of the content at a fixed width with unbounded height instead.
/// </summary>
public static class VisualExporter
{
    /// <summary>
    /// Lays <paramref name="element"/> out at a fixed width and renders it. <paramref name="scale"/>
    /// oversamples so the result still looks sharp after a chat client scales it down.
    /// </summary>
    public static BitmapSource Render(FrameworkElement element, double width, double scale = 2.0)
    {
        // Two passes. The first gives wrapped text its final width; only then does the element
        // report the height it actually needs, and arranging to a stale height crops the bottom.
        element.Measure(new Size(width, double.PositiveInfinity));
        element.Arrange(new Rect(0, 0, width, element.DesiredSize.Height));
        element.UpdateLayout();

        element.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Max(1, element.DesiredSize.Height);
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();

        var dpi = 96 * scale;
        var target = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            dpi, dpi, PixelFormats.Pbgra32);
        target.Render(element);
        target.Freeze();
        return target;
    }

    /// <summary>
    /// Puts the image on the clipboard as both a bitmap and a real PNG. The clipboard's legacy
    /// bitmap format drops the alpha channel, which some apps then paste on a black background,
    /// so offering PNG alongside lets the target pick the one that survives.
    /// </summary>
    public static void CopyToClipboard(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(image);

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            var png = new MemoryStream();
            encoder.Save(png);
            png.Position = 0;
            // Not disposed on purpose - SetDataObject(copy: true) reads it as it flushes, and the
            // clipboard keeps the contents after FCAT closes.
            data.SetData("PNG", png, false);
        }
        catch
        {
            // PNG is the nicety, the bitmap is the guarantee. If this format can't be attached,
            // pasting still works everywhere that takes a plain clipboard image.
        }

        Clipboard.SetDataObject(data, true);
    }
}
