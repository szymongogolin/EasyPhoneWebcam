using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhoneCam.Desktop.Video;

/// <summary>The frame a virtual camera shows while its phone does not send.</summary>
internal static class NoSignalCard
{
    /// <summary>Renders a top-down BGR24 frame. Uses WPF rendering, so call it on the UI thread.</summary>
    public static byte[] Render(int width, int height, string title, string message)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x20)), null, new Rect(0, 0, width, height));
            FormattedText titleText = Text(title, FontWeights.SemiBold, height / 13.0, Color.FromRgb(0xF2, 0xF4, 0xF7));
            FormattedText messageText = Text(message, FontWeights.Normal, height / 24.0, Color.FromRgb(0xAA, 0xB2, 0xBF));
            dc.DrawText(titleText, new Point((width - titleText.Width) / 2, height / 2.0 - titleText.Height));
            dc.DrawText(messageText, new Point((width - messageText.Width) / 2, height / 2.0 + height / 60.0));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bgra = new byte[width * height * 4];
        bitmap.CopyPixels(bgra, width * 4, 0);
        var bgr = new byte[width * height * 3];
        for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3)
        {
            bgr[j] = bgra[i];
            bgr[j + 1] = bgra[i + 1];
            bgr[j + 2] = bgra[i + 2];
        }
        return bgr;
    }

    private static FormattedText Text(string text, FontWeight weight, double size, Color color) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, new SolidColorBrush(color), pixelsPerDip: 1.0);
}
