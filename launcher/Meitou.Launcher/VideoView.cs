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
/// A news post's <c>.webm</c> video: its first picture with a play button. A click plays it with its sound (<see cref="VideoAudio"/>);
/// then a click (or Space) pauses and resumes, the bar at the bottom skips 10 s back or forward (or Left/Right) and seeks anywhere.
/// One video plays at a time; it pauses when its post leaves the screen and returns to the poster at the end.
/// </summary>
sealed class VideoView : Border
{
    /// <summary>Bytes fetched for the poster: enough for the first frame of a 720p or 1080p clip.</summary>
    const int PosterBytes = 1 << 20;
    static readonly TimeSpan Skip = TimeSpan.FromSeconds(10);
    static readonly Geometry PlayIcon = Geometry.Parse("M 0,0 L 0,14 L 12,7 Z"), PauseIcon = Geometry.Parse("M 0,0 H 4 V 14 H 0 Z M 8,0 H 12 V 14 H 8 Z");

    static VideoView? playing;
    /// <summary>The last playback session of any video: the next waits for it, so two never hold OpenAL's current context at once.</summary>
    static Task lastSession = Task.CompletedTask;

    enum State { Poster, Loading, Playing, Paused }

    readonly string url;
    readonly NewsFeed feed;
    WebmFile? file; // the whole video, downloaded on the first Play
    readonly Image image = new() { Stretch = Stretch.Uniform };
    readonly Control button;
    readonly Border bar;
    readonly Path playPause;
    readonly Border track, fill;
    readonly TextBlock time;
    readonly WriteableBitmap poster;
    WriteableBitmap? frame;
    CancellationTokenSource? run;
    State state = State.Poster;
    TimeSpan position;
    bool hovered;
    double? dragging; // the seek bar's fraction while it is held

    /// <summary>Fills the post's width, its height following the picture's shape.</summary>
    VideoView(string url, NewsFeed feed, WebmFile? file, byte[] firstPicture, int width, int height)
    {
        (this.url, this.feed, this.file) = (url, feed, file);
        CornerRadius = new CornerRadius(6);
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        poster = Bitmap(width, height);
        Copy(firstPicture, poster);
        image.Source = poster;
        var shade = new SolidColorBrush(Color.FromArgb(0xB0, 0x11, 0x0F, 0x0D));
        var ink = MarkdownView.Brush("PaperLight");
        button = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28), Background = shade,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new Path
            {
                Data = Geometry.Parse("M 0,0 L 0,22 L 19,11 Z"), Fill = ink, Margin = new Thickness(5, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        playPause = new Path { Data = PauseIcon, Fill = ink, VerticalAlignment = VerticalAlignment.Center };
        fill = new Border { Background = MarkdownView.Brush("BrassLight"), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3) };
        track = new Border
        {
            Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x60, 0xD9, 0xCE, 0xB8)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), Child = fill,
        };
        // A taller transparent strip around the thin track, so it is easy to hit.
        var seek = new Border { Background = Brushes.Transparent, Height = 22, Child = track };
        seek.PointerPressed += (_, e) => { e.Handled = true; e.Pointer.Capture(seek); dragging = Fraction(e); UpdateBar(); };
        seek.PointerMoved += (_, e) => { if (dragging is not null) { dragging = Fraction(e); UpdateBar(); } };
        seek.PointerReleased += (_, e) =>
        {
            if (dragging is not { } f) return;
            e.Handled = true;
            dragging = null;
            e.Pointer.Capture(null);
            SeekTo(Duration * f);
        };
        track.SizeChanged += (_, _) => UpdateBar();
        time = new TextBlock { Foreground = ink, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        var row = new DockPanel();
        foreach (var control in new[]
                 {
                     BarButton("M 9,0 L 9,14 L 0,7 Z M 18,0 L 18,14 L 9,7 Z", () => SeekTo(position - Skip)),
                     BarButton(null, TogglePause, playPause),
                     BarButton("M 0,0 L 0,14 L 9,7 Z M 9,0 L 9,14 L 18,7 Z", () => SeekTo(position + Skip)),
                 })
        {
            DockPanel.SetDock(control, Dock.Left);
            row.Children.Add(control);
        }
        DockPanel.SetDock(time, Dock.Right);
        row.Children.Add(time);
        row.Children.Add(seek);
        bar = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(8, 4), IsVisible = false, Child = row,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0x11, 0x0F, 0x0D), 0), new GradientStop(Color.FromArgb(0xD0, 0x11, 0x0F, 0x0D), 1) },
            },
        };
        bar.PointerPressed += (_, e) => e.Handled = true; // the bar's gaps are not clicks on the picture

        Child = new Panel { Children = { image, button, bar } };
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus();
            if (state == State.Poster) Play(); else TogglePause();
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (state is State.Poster or State.Loading) return;
            if (e.Key == Key.Space) TogglePause();
            else if (e.Key == Key.Left) SeekTo(position - Skip);
            else if (e.Key == Key.Right) SeekTo(position + Skip);
            else return;
            e.Handled = true;
        };
        PointerEntered += (_, _) => { hovered = true; UpdateBar(); };
        PointerExited += (_, _) => { hovered = false; UpdateBar(); };
        DetachedFromVisualTree += (_, _) => ToPoster();
    }

    Control BarButton(string? icon, Action click, Path? path = null)
    {
        var control = new Border
        {
            Width = 30, Height = 26, Background = Brushes.Transparent,
            Child = path ?? new Path { Data = Geometry.Parse(icon!), Fill = MarkdownView.Brush("PaperLight"), VerticalAlignment = VerticalAlignment.Center },
        };
        if (control.Child is Path p) p.HorizontalAlignment = HorizontalAlignment.Center;
        control.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Focus();
            click();
        };
        return control;
    }

    /// <summary>
    /// A video whose first picture loads in the background from the file's start (<see cref="NewsFeed.HeadAsync"/>); the rest downloads
    /// only when it is played. Until then (or when it cannot be played) it takes no room, like an image.
    /// </summary>
    public static Control Create(string url, NewsFeed feed)
    {
        var host = new Panel();
        _ = Task.Run(async () =>
        {
            if (!VpxDecoder.Available) return;
            var bytes = await feed.HeadAsync(url, PosterBytes);
            bool whole = bytes is not null && bytes.Length < PosterBytes; // a short file comes whole
            if (bytes is null) return;
            if (Poster(bytes, head: !whole) is not var (head, pixels, width, height))
            {
                // A first frame larger than the head: take the whole file after all.
                if (whole || await feed.ImageAsync(url) is not { } all || Poster(all, head: false) is not var (full, p, w, h)) return;
                (head, pixels, width, height, whole) = (full, p, w, h, true);
            }
            Dispatcher.UIThread.Post(() => host.Children.Add(new VideoView(url, feed, whole ? head : null, pixels, width, height)));
        });
        return host;
    }

    static (WebmFile File, byte[] Pixels, int Width, int Height)? Poster(byte[] bytes, bool head)
    {
        try
        {
            var file = WebmFile.Read(bytes, head);
            if (file.VideoCodec is null) return null;
            using var decoder = new VpxDecoder(file.VideoCodec);
            byte[] pixels = [];
            foreach (var f in file.VideoFrames)
                if (decoder.Decode(f.Data.Span, ref pixels) is var (width, height)) return (file, pixels, width, height);
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException) { }
        return null;
    }

    /// <summary>The last picture's time: what the seek bar spans.</summary>
    TimeSpan Duration => file is { VideoFrames.Count: > 0 } f ? f.VideoFrames[^1].Time : TimeSpan.Zero;

    /// <summary>From the poster: the whole file is downloaded the first time (the play button dimmed meanwhile), then it plays from the start.</summary>
    void Play()
    {
        playing?.Pause();
        playing = this;
        if (file is not null)
        {
            Start(TimeSpan.Zero, play: true);
            return;
        }
        state = State.Loading;
        button.Opacity = 0.5;
        _ = Task.Run(async () =>
        {
            var bytes = await feed.ImageAsync(url);
            WebmFile? loaded = null;
            try { if (bytes is not null) loaded = WebmFile.Read(bytes); }
            catch (InvalidDataException) { }
            Dispatcher.UIThread.Post(() =>
            {
                button.Opacity = 1;
                file ??= loaded;
                if (state != State.Loading) return; // stopped meanwhile
                if (file is null) ToPoster(); // could not be downloaded
                else Start(TimeSpan.Zero, play: true);
            });
        });
    }

    void TogglePause()
    {
        if (state == State.Playing) Pause();
        else if (state == State.Paused)
        {
            playing?.Pause();
            playing = this;
            Start(position, play: true);
        }
    }

    void Pause()
    {
        if (state != State.Playing) return;
        Cancel();
        state = State.Paused;
        UpdateBar();
    }

    /// <summary>Jumps to a time: playing on from there, or showing its picture when paused.</summary>
    void SeekTo(TimeSpan target)
    {
        if (file is null || state is State.Poster or State.Loading) return;
        target = target < TimeSpan.Zero ? TimeSpan.Zero : target > Duration ? Duration : target;
        position = target;
        Start(target, play: state == State.Playing);
    }

    void ToPoster()
    {
        Cancel();
        if (playing == this) playing = null;
        state = State.Poster;
        position = TimeSpan.Zero;
        image.Source = poster;
        button.IsVisible = true;
        UpdateBar();
    }

    void Cancel()
    {
        run?.Cancel();
        run = null;
    }

    /// <summary>A new session from <paramref name="from"/> (the previous one cancelled): it shows that moment's picture, then plays on if asked.</summary>
    void Start(TimeSpan from, bool play)
    {
        Cancel();
        run = new CancellationTokenSource();
        var token = run.Token;
        state = play ? State.Playing : State.Paused;
        button.IsVisible = false;
        UpdateBar();
        var video = file!;
        var previous = lastSession;
        lastSession = Task.Run(async () =>
        {
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); // a failed session must not block the next
            if (!token.IsCancellationRequested) Session(video, from, play, token);
        });
    }

    /// <summary>
    /// Decodes from the keyframe before <paramref name="from"/> up to it without drawing, shows that picture, and when playing shows each
    /// next one at its time, on the sound's clock when there is sound, else a stopwatch. At the end the poster returns.
    /// </summary>
    void Session(WebmFile file, TimeSpan from, bool play, CancellationToken token)
    {
        bool ended = false;
        try
        {
            using var decoder = new VpxDecoder(file.VideoCodec!);
            var frames = file.VideoFrames;
            int target = Math.Max(0, frames.FindLastIndex(f => f.Time <= from));
            int key = Math.Max(0, frames.FindLastIndex(target, f => f.Key));
            byte[] pixels = [];
            for (int i = key; i < target && !token.IsCancellationRequested; i++) decoder.Decode(frames[i].Data.Span, ref pixels, convert: false);
            if (decoder.Decode(frames[target].Data.Span, ref pixels) is var (w, h)) Show(pixels, w, h, frames[target].Time, token);
            if (!play || token.IsCancellationRequested) return;

            using var audio = VideoAudio.TryOpen(file, frames[target].Time);
            var start = frames[target].Time;
            var watch = Stopwatch.StartNew();
            TimeSpan heard = start, heardAt = TimeSpan.Zero; // the sound's last position, and when it was read
            TimeSpan Clock()
            {
                if (audio is null) return start + watch.Elapsed;
                // Before the sound's first frame, and once it has ended (or runs shorter than the pictures), the clock runs on its own.
                if (!audio.Finished && watch.Elapsed > TimeSpan.FromMilliseconds(50)) (heard, heardAt) = (audio.Position, watch.Elapsed);
                return heard + (watch.Elapsed - heardAt);
            }
            audio?.Start();
            for (int i = target + 1; i < frames.Count && !token.IsCancellationRequested; i++)
            {
                // A picture whose successor is already due is decoded (the next ones depend on it) but not drawn.
                bool late = i + 1 < frames.Count && frames[i + 1].Time <= Clock();
                if (decoder.Decode(frames[i].Data.Span, ref pixels, convert: !late) is not var (width, height)) continue;
                for (TimeSpan wait; (wait = frames[i].Time - Clock()) > TimeSpan.Zero && !token.IsCancellationRequested;)
                    Thread.Sleep(Math.Clamp((int)wait.TotalMilliseconds, 1, 15));
                Show(pixels, width, height, frames[i].Time, token);
            }
            while (audio is { Finished: false } && !token.IsCancellationRequested) Thread.Sleep(20);
            ended = !token.IsCancellationRequested;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or TaskCanceledException) { }
        if (ended) Dispatcher.UIThread.Post(() => { if (!token.IsCancellationRequested) ToPoster(); });
    }

    void Show(byte[] pixels, int width, int height, TimeSpan at, CancellationToken token) =>
        Dispatcher.UIThread.Invoke(() =>
        {
            if (token.IsCancellationRequested) return;
            if (!IsEffectivelyVisible) { Pause(); return; } // the Home page was left
            if (frame is null || frame.PixelSize.Width != width || frame.PixelSize.Height != height) frame = Bitmap(width, height);
            Copy(pixels, frame);
            // A new bitmap object each time is not needed; the Image only has to redraw.
            if (image.Source != frame) image.Source = frame;
            image.InvalidateVisual();
            position = at;
            UpdateBar();
        });

    /// <summary>The bar shows while paused, or while playing with the pointer over the video.</summary>
    void UpdateBar()
    {
        bar.IsVisible = state == State.Paused || state == State.Playing && (hovered || dragging is not null);
        playPause.Data = state == State.Playing ? PauseIcon : PlayIcon;
        var duration = Duration;
        double f = dragging ?? (duration > TimeSpan.Zero ? position / duration : 0);
        fill.Width = Math.Clamp(f, 0, 1) * track.Bounds.Width;
        time.Text = $"{Format(duration * f)} / {Format(duration)}";
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    double Fraction(PointerEventArgs e) => track.Bounds.Width > 0 ? Math.Clamp(e.GetPosition(track).X / track.Bounds.Width, 0, 1) : 0;

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
