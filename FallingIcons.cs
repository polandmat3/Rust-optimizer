using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace ProjectOpti;

/// <summary>
/// Falling icons with a purple glow, drawn on a Canvas.
/// - one lane per ~80 px, so icons are spread evenly across the width
/// - icons come from a shuffled "bag", so every picture is used about equally often
/// </summary>
public sealed class FallingIcons
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".ico", ".gif" };

    // ---- tweak these ----
    private const int MinSize = 38;
    private const int MaxSize = 64;
    private const double MinSpeed = 60;   // pixels per second
    private const double MaxSpeed = 130;
    private const string GlowColor = "#A855F7";
    private const double GlowBlur = 18;

    private sealed class Item
    {
        public Image Img = null!;
        public TranslateTransform Move = null!;
        public RotateTransform Rot = null!;
        public double Speed, Spin, Size;
        public int Lane;
    }

    private readonly Canvas _canvas;
    private readonly List<BitmapImage> _images = new();
    private readonly List<Item> _items = new();
    private readonly Queue<BitmapImage> _bag = new();
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();

    private BitmapImage? _lastUsed;
    private double _w, _h, _laneW, _lastTime;
    private bool _running;

    public int ImageCount => _images.Count;
    public string SearchedFolder { get; private set; } = "";

    public FallingIcons(Canvas canvas)
    {
        _canvas = canvas;
    }

    public void Start()
    {
        LoadImages();
        if (_images.Count == 0) return;

        _w = _canvas.ActualWidth;
        _h = _canvas.ActualHeight;
        if (_w < 50 || _h < 50) return;

        int lanes = Math.Max(4, (int)(_w / 80));
        _laneW = _w / lanes;

        var glow = new DropShadowEffect
        {
            Color = (Color)ColorConverter.ConvertFromString(GlowColor),
            BlurRadius = GlowBlur,
            ShadowDepth = 0,
            Opacity = 0.95
        };
        glow.Freeze();

        // Spread the starting heights evenly so icons don't arrive in clumps.
        var slots = Enumerable.Range(0, lanes).OrderBy(_ => _rng.Next()).ToList();

        for (int lane = 0; lane < lanes; lane++)
        {
            var item = new Item { Lane = lane, Size = _rng.Next(MinSize, MaxSize + 1) };
            item.Rot = new RotateTransform(_rng.Next(-25, 26));
            item.Move = new TranslateTransform();
            item.Img = new Image
            {
                Source = NextIcon(),
                Stretch = Stretch.Uniform,
                Effect = glow,
                Opacity = 0.92,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { item.Rot, item.Move } }
            };

            Place(item);
            item.Move.Y = (_h + MaxSize) * slots[lane] / lanes - MaxSize;

            _canvas.Children.Add(item.Img);
            _items.Add(item);
        }

        _clock.Start();
        _lastTime = 0;
        _running = true;
        CompositionTarget.Rendering += OnRender;
    }

    public void Stop()
    {
        if (!_running) return;
        CompositionTarget.Rendering -= OnRender;
        _running = false;
    }

    // ---------- animation ----------

    private void OnRender(object? sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - _lastTime, 0.1);
        _lastTime = now;

        foreach (var item in _items)
        {
            item.Move.Y += item.Speed * dt;
            item.Rot.Angle += item.Spin * dt;

            if (item.Move.Y > _h + 10)
                Respawn(item);
        }
    }

    private void Respawn(Item item)
    {
        item.Size = _rng.Next(MinSize, MaxSize + 1);
        item.Img.Source = NextIcon();
        Place(item);
        item.Move.Y = -item.Size - _rng.Next(0, 220);
    }

    // size, speed, spin and horizontal position inside the item's lane
    private void Place(Item item)
    {
        item.Img.Width = item.Size;
        item.Img.Height = item.Size;
        item.Speed = MinSpeed + _rng.NextDouble() * (MaxSpeed - MinSpeed);
        item.Spin = (_rng.NextDouble() - 0.5) * 40; // degrees per second

        double left = item.Lane * _laneW + (_laneW - item.Size) / 2 + (_rng.NextDouble() - 0.5) * _laneW * 0.2;
        left = Math.Clamp(left, 0, Math.Max(0, _w - item.Size));
        Canvas.SetLeft(item.Img, left);
        Canvas.SetTop(item.Img, 0);
    }

    // Shuffled bag: every icon is used once per round, so usage stays even.
    private BitmapImage NextIcon()
    {
        if (_bag.Count == 0)
        {
            var round = _images.OrderBy(_ => _rng.Next()).ToList();
            if (round.Count > 1 && ReferenceEquals(round[0], _lastUsed))
                (round[0], round[^1]) = (round[^1], round[0]);
            foreach (var img in round) _bag.Enqueue(img);
        }
        _lastUsed = _bag.Dequeue();
        return _lastUsed;
    }

    // ---------- loading ----------

    private void LoadImages()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "icons"),
            Path.Combine(Directory.GetCurrentDirectory(), "icons"),
            Path.Combine(AppPaths.Root, "icons")
        };
        SearchedFolder = candidates[^1];

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;

            var files = Directory.GetFiles(dir)
                .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToList();
            if (files.Count == 0) continue;

            SearchedFolder = dir;
            foreach (var file in files)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(file);
                    bmp.DecodePixelWidth = 128; // keeps memory low for big images
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    _images.Add(bmp);
                }
                catch { /* skip files WPF can't read */ }
            }
            if (_images.Count > 0) return;
        }
    }
}
