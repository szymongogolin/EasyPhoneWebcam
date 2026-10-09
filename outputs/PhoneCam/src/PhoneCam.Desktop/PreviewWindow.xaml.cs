using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhoneCam.Desktop.Video;

namespace PhoneCam.Desktop;

/// <summary>Video-only window for one slot. Shows the newest frame of the slot's feed; decoding lives in <see cref="SlotFeed"/>.</summary>
public partial class PreviewWindow : Window
{
    private readonly SlotFeed _feed;
    private WriteableBitmap? _bitmap;

    internal PreviewWindow(string title, SlotFeed feed)
    {
        InitializeComponent();
        Title = title;
        _feed = feed;
        ShowOverlay();
        _feed.StatusChanged += ShowOverlay;
        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            _feed.StatusChanged -= ShowOverlay;
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_feed.Decoder is not { } decoder || decoder.TakeLatest() is not { } frame) return;
        try
        {
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
                VideoImage.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgr24, frame.Width * 3, 0);
            Overlay.Visibility = Visibility.Collapsed;
        }
        finally { decoder.Return(frame); }
    }

    private void ShowOverlay()
    {
        Overlay.Text = _feed.Status;
        Overlay.Visibility = Visibility.Visible;
    }
}
