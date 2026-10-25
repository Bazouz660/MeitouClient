using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Meitou.Launcher;

/// <summary>
/// A news post's <c>.webm</c> video: its first picture with a play button; a click plays it once with its sound (<see cref="VideoAudio"/>),
/// another click stops it. One video plays at a time, and it stops when its post leaves the screen.
/// </summary>
sealed class VideoView : Border
{
    static VideoView? playing;

    readonly WebmFile file;
    readonly Image image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Control button;
    readonly WriteableBitmap poster;
    WriteableBitmap? frame;
    CancellationTokenSource? run;

    VideoView(WebmFile file, byte[] firstPicture, int width, int height, double maxHeight)
    {
        this.file = file;
        CornerRadius = new CornerRadius(6);
        ClipToBounds = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        Cursor = new Cursor(StandardCursorType.Hand);
        image.MaxHeight = maxHeight;
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        poster = Bitmap(width, height);
        Copy(firstPicture, poster);
        image.Source = poster;
        button = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = new SolidColorBrush(Color.FromArgb(0xB0, 0x11, 0x0F, 0x0D)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                Data = Geometry.Parse("M 0,0 L 0,22 L 19,11 Z"), Fill = MarkdownView.Brush("PaperLight"), Margin = new Thickness(5, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Child = new Panel { Children = { image, button } };
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (run is null) Play(); else Stop();
            e.Handled = true;
        };
        DetachedFromVisualTree += (_, _) => Stop();
    }

    /// <summary>A video loaded in the background; until then (or when it cannot be played) it takes no room, like an image.</summary>
    public static Control Create(string url, NewsFeed feed, double maxHeight)
    {
        var host = new Panel { HorizontalAlignment = HorizontalAlignment.Left };
        _ = Task.Run(async () =>
        {
            var bytes = await feed.ImageAsync(url);
            if (bytes is null || !VpxDecoder.Available) return;
            try
            {
                var file = WebmFile.Read(bytes);
                if (file.VideoCodec is null) return;
                using var decoder = new VpxDecoder(file.VideoCodec);
                byte[] pixels = [];
                foreach (var f in file.VideoFrames)
                    if (decoder.Decode(f.Data.Span, ref pixels) is var (width, height))
                    {
                        Dispatcher.UIThread.Post(() => host.Children.Add(new VideoView(file, pixels, width, height, maxHeight)));
                        return;
                    }
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException) { }
        });
        return host;
    }

    void Play()
    {
        playing?.Stop();
        playing = this;
        button.IsVisible = false;
        run = new CancellationTokenSource();
        var token = run.Token;
        _ = Task.Run(() => Loop(token));
    }

    void Stop()
    {
        if (run is null) return;
        run.Cancel();
        run = null;
        if (playing == this) playing = null;
        image.Source = poster;
        button.IsVisible = true;
    }

    /// <summary>Decodes every frame in order and shows each at its time, on the sound's clock when there is sound, else a stopwatch.</summary>
    void Loop(CancellationToken token)
    {
        try
        {
            using var decoder = new VpxDecoder(file.VideoCodec!);
            using var audio = VideoAudio.TryOpen(file);
            var watch = Stopwatch.StartNew();
            TimeSpan heard = TimeSpan.Zero, heardAt = TimeSpan.Zero; // the sound's last position, and when it was read
            TimeSpan Clock()
            {
                if (audio is null) return watch.Elapsed;
                // Once the sound has ended (or runs shorter than the pictures), the clock carries on from where it left off.
                if (!audio.Finished) (heard, heardAt) = (audio.Position, watch.Elapsed);
                return heard + (watch.Elapsed - heardAt);
            }
            audio?.Start();
            byte[] pixels = [];
            var frames = file.VideoFrames;
            for (int i = 0; i < frames.Count && !token.IsCancellationRequested; i++)
            {
                // A picture whose successor is already due is decoded (the next ones depend on it) but not drawn.
                bool late = i + 1 < frames.Count && frames[i + 1].Time <= Clock();
                if (decoder.Decode(frames[i].Data.Span, ref pixels, convert: !late) is not var (width, height)) continue;
                for (TimeSpan wait; (wait = frames[i].Time - Clock()) > TimeSpan.Zero && !token.IsCancellationRequested;)
                    Thread.Sleep(Math.Clamp((int)wait.TotalMilliseconds, 1, 15));
                Dispatcher.UIThread.Invoke(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (!IsEffectivelyVisible) { Stop(); return; } // the Home page was left
                    if (frame is null || frame.PixelSize.Width != width || frame.PixelSize.Height != height) frame = Bitmap(width, height);
                    Copy(pixels, frame);
                    // A new bitmap object each time is not needed; the Image only has to redraw.
                    if (image.Source != frame) image.Source = frame;
                    image.InvalidateVisual();
                });
            }
            while (audio is { Finished: false } && !token.IsCancellationRequested) Thread.Sleep(20);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or TaskCanceledException) { }
        Dispatcher.UIThread.Post(() =>
        {
            if (!token.IsCancellationRequested) Stop();
        });
    }

    static WriteableBitmap Bitmap(int width, int height) =>
        new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    static void Copy(byte[] bgra, WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        int row = bitmap.PixelSize.Width * 4;
        for (int y = 0; y < bitmap.PixelSize.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(bgra, y * row, buffer.Address + y * buffer.RowBytes, row);
    }
}
