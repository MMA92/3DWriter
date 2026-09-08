using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WriterCore;
using Line = Avalonia.Controls.Shapes.Line;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Path = System.IO.Path;

namespace WriterGui;

/// <summary>
/// Minimal GUI wrapper around WriterCore.GCodeGenerator - the same logic the CLI
/// (3DWriterCli) uses. Speeds/pen-up-down/home/laser stay at WriterSettings defaults;
/// text, font, scale, bed size and offset are exposed here. Add more fields later if needed.
/// </summary>
public partial class MainWindow : Window
{
    private const double PixelsPerMm = 2.0; // matches the original app's default preview magnification
    private static readonly IBrush OffsetLineBrush = new SolidColorBrush(Color.FromArgb(110, 255, 0, 0));
    private static readonly JsonSerializerOptions BoxesJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _fontsDir;
    private readonly List<GCodeGenerator.Box> _boxes = new();
    private readonly List<int> _boxGroups = new(); // parallel to _boxes: boxes from one load/draw move together
    private int _nextGroupId;
    private string? _lastGCode;
    private Point? _dragStart;
    private Rectangle? _dragGhost;
    private List<(Rectangle Rect, double Left, double Top, int Index)>? _dragGroup;
    private Point _dragStartPointer;

    public MainWindow()
    {
        InitializeComponent();

        _fontsDir = Path.Combine(AppContext.BaseDirectory, "fonts");
        var fonts = Directory.Exists(_fontsDir)
            ? Directory.GetFiles(_fontsDir, "*.cmf").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToList()
            : new List<string?>();

        FontBox.ItemsSource = fonts;
        FontBox.SelectedItem = fonts.Contains("cursive") ? "cursive" : fonts.FirstOrDefault();

        ScaleSlider.ValueChanged += (_, e) => ScaleValueText.Text = e.NewValue.ToString("0.00");
        ScaleValueText.Text = ScaleSlider.Value.ToString("0.00");

        BedWidthBox.ValueChanged += (_, _) => UpdateCanvasFrame();
        BedHeightBox.ValueChanged += (_, _) => UpdateCanvasFrame();
        OffsetXBox.ValueChanged += (_, _) => UpdateCanvasFrame();
        OffsetYBox.ValueChanged += (_, _) => UpdateCanvasFrame();

        // Zoom only scales the preview visually (LayoutTransform in XAML) - it never
        // touches WriterSettings.Scale, which is the actual font/GCode size.
        ZoomSlider.ValueChanged += (_, e) => ZoomValueText.Text = $"{e.NewValue * 100:0}%";
        ZoomValueText.Text = $"{ZoomSlider.Value * 100:0}%";

        PreviewBorder.PointerWheelChanged += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            ZoomSlider.Value += e.Delta.Y * 0.1;
            e.Handled = true;
        };

        PreviewCanvas.PointerPressed += OnCanvasPointerPressed;
        PreviewCanvas.PointerMoved += OnCanvasPointerMoved;
        PreviewCanvas.PointerReleased += OnCanvasPointerReleased;

        PreviewBorder.AddHandler(DragDrop.DragOverEvent, OnPreviewDragOver);
        PreviewBorder.AddHandler(DragDrop.DropEvent, OnPreviewDrop);

        UpdateCanvasFrame();
    }

    private double BedWidth => (double)(BedWidthBox.Value ?? 200m);
    private double BedHeight => (double)(BedHeightBox.Value ?? 200m);
    private double OffsetX => (double)(OffsetXBox.Value ?? 45m);
    private double OffsetY => (double)(OffsetYBox.Value ?? 45m);

    /// <summary>Resizes the canvas to the current bed size and draws the red offset
    /// crosshair marking where text placement (origin) would start - matches the
    /// semi-transparent red margin lines from the original app's preview.</summary>
    private void UpdateCanvasFrame()
    {
        PreviewCanvas.Width = BedWidth * PixelsPerMm;
        PreviewCanvas.Height = BedHeight * PixelsPerMm;
        PreviewCanvas.Children.Clear();
        _lastGCode = null;
        SaveButton.IsEnabled = false;

        PreviewCanvas.Children.Add(new Line
        {
            StartPoint = new Point(0, OffsetY * PixelsPerMm),
            EndPoint = new Point(PreviewCanvas.Width, OffsetY * PixelsPerMm),
            Stroke = OffsetLineBrush,
            StrokeThickness = 1.5,
        });
        PreviewCanvas.Children.Add(new Line
        {
            StartPoint = new Point(OffsetX * PixelsPerMm, 0),
            EndPoint = new Point(OffsetX * PixelsPerMm, PreviewCanvas.Height),
            Stroke = OffsetLineBrush,
            StrokeThickness = 1.5,
        });

        for (int i = 0; i < _boxes.Count; i++) AddBoxVisual(_boxes[i], i);
    }

    private void AddBoxVisual(GCodeGenerator.Box box, int index)
    {
        var rect = new Rectangle
        {
            Width = box.Width * PixelsPerMm,
            Height = box.Height * PixelsPerMm,
            Stroke = Brushes.Black,
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent, // makes the whole box (not just its outline) hit-testable for dragging
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Tag = index,
        };
        Canvas.SetLeft(rect, box.X * PixelsPerMm);
        Canvas.SetTop(rect, box.Y * PixelsPerMm);
        rect.PointerPressed += OnBoxPointerPressed;
        PreviewCanvas.Children.Add(rect);
    }

    /// <summary>Starts moving an existing box instead of drawing a new one; e.Handled stops
    /// the click from also reaching OnCanvasPointerPressed (bubbles from rect to canvas). Boxes
    /// loaded from the same JSON file (or drawn as one box) share a group id and move together
    /// as a rigid unit, whichever one of them was grabbed.</summary>
    private void OnBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewCanvas).Properties.IsLeftButtonPressed) return;
        int group = _boxGroups[(int)((Rectangle)sender!).Tag!];
        _dragGroup = PreviewCanvas.Children.OfType<Rectangle>()
            .Where(r => r.Tag is int idx && _boxGroups[idx] == group)
            .Select(r => (Rect: r, Left: Canvas.GetLeft(r), Top: Canvas.GetTop(r), Index: (int)r.Tag!))
            .ToList();
        _dragStartPointer = e.GetPosition(PreviewCanvas);
        e.Pointer.Capture(PreviewCanvas);
        e.Handled = true;
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewCanvas).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(PreviewCanvas);
        _dragGhost = new Rectangle
        {
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1.5,
            StrokeDashArray = new AvaloniaList<double> { 4, 2 },
        };
        PreviewCanvas.Children.Add(_dragGhost);
        e.Pointer.Capture(PreviewCanvas);
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragGroup is not null)
        {
            var pos = e.GetPosition(PreviewCanvas);
            double dx = pos.X - _dragStartPointer.X;
            double dy = pos.Y - _dragStartPointer.Y;

            // Clamp the delta against the group's combined bounding box so the whole unit
            // stays on the bed and none of its boxes get left behind or resized.
            double minLeft = _dragGroup.Min(i => i.Left);
            double minTop = _dragGroup.Min(i => i.Top);
            double maxRight = _dragGroup.Max(i => i.Left + i.Rect.Width);
            double maxBottom = _dragGroup.Max(i => i.Top + i.Rect.Height);
            dx = Math.Clamp(dx, -minLeft, PreviewCanvas.Width - maxRight);
            dy = Math.Clamp(dy, -minTop, PreviewCanvas.Height - maxBottom);

            foreach (var item in _dragGroup)
            {
                Canvas.SetLeft(item.Rect, item.Left + dx);
                Canvas.SetTop(item.Rect, item.Top + dy);
            }
            return;
        }
        if (_dragStart is not { } start || _dragGhost is null) return;
        var p = e.GetPosition(PreviewCanvas);
        double x = Math.Min(start.X, p.X), y = Math.Min(start.Y, p.Y);
        Canvas.SetLeft(_dragGhost, x);
        Canvas.SetTop(_dragGhost, y);
        _dragGhost.Width = Math.Abs(p.X - start.X);
        _dragGhost.Height = Math.Abs(p.Y - start.Y);
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragGroup is not null)
        {
            foreach (var item in _dragGroup)
            {
                _boxes[item.Index] = _boxes[item.Index] with
                {
                    X = Canvas.GetLeft(item.Rect) / PixelsPerMm,
                    Y = Canvas.GetTop(item.Rect) / PixelsPerMm,
                };
            }
            _dragGroup = null;
            e.Pointer.Capture(null);
            _lastGCode = null;
            SaveButton.IsEnabled = false;
            return;
        }
        if (_dragStart is not { } start || _dragGhost is null) return;
        PreviewCanvas.Children.Remove(_dragGhost);
        _dragGhost = null;
        e.Pointer.Capture(null);

        var p = e.GetPosition(PreviewCanvas);
        double x = Math.Min(start.X, p.X), y = Math.Min(start.Y, p.Y);
        double w = Math.Abs(p.X - start.X), h = Math.Abs(p.Y - start.Y);
        _dragStart = null;
        if (w < 3 || h < 3) return; // ignore accidental clicks

        var box = new GCodeGenerator.Box(x / PixelsPerMm, y / PixelsPerMm, w / PixelsPerMm, h / PixelsPerMm);
        _boxes.Add(box);
        _boxGroups.Add(_nextGroupId++);
        AddBoxVisual(box, _boxes.Count - 1);
        _lastGCode = null;
        SaveButton.IsEnabled = false;
    }

    private void OnClearBoxesClick(object? sender, RoutedEventArgs e)
    {
        _boxes.Clear();
        _boxGroups.Clear();
        UpdateCanvasFrame();
    }

    private async void OnLoadBoxesClick(object? sender, RoutedEventArgs e)
    {
        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            FileTypeFilter = new[] { new FilePickerFileType("Boxes JSON") { Patterns = new[] { "*.json" } } },
        });

        if (files.Count > 0) await LoadBoxesFromFileAsync(files[0]);
    }

    private void OnPreviewDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnPreviewDrop(object? sender, DragEventArgs e)
    {
        var file = e.Data.GetFiles()?.OfType<IStorageFile>().FirstOrDefault();
        if (file is not null) await LoadBoxesFromFileAsync(file);
    }

    private async System.Threading.Tasks.Task LoadBoxesFromFileAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            var boxes = JsonSerializer.Deserialize<List<GCodeGenerator.Box>>(json, BoxesJsonOptions);
            if (boxes is null || boxes.Count == 0)
            {
                StatusText.Foreground = Brushes.Crimson;
                StatusText.Text = "Boxes JSON was empty or invalid.";
                return;
            }

            int group = _nextGroupId++;
            _boxes.AddRange(boxes);
            _boxGroups.AddRange(Enumerable.Repeat(group, boxes.Count));
            UpdateCanvasFrame();
            StatusText.Foreground = Brushes.Gray;
            StatusText.Text = $"Loaded {boxes.Count} box(es) from {file.Name}.";
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            StatusText.Foreground = Brushes.Crimson;
            StatusText.Text = $"Failed to load boxes: {ex.Message}";
        }
    }

    private void OnRenderClick(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "";
        UpdateCanvasFrame(); // resize + redraw the offset crosshair, then layer strokes on top

        if (FontBox.SelectedItem is not string fontName)
        {
            StatusText.Text = "No font selected.";
            return;
        }

        var settings = new WriterSettings
        {
            Scale = ScaleSlider.Value,
            BedWidth = BedWidth,
            BedHeight = BedHeight,
            OffsetX = OffsetX,
            OffsetY = OffsetY,
            PenUp = ((double)(PenUpBox.Value ?? 12m)).ToString(CultureInfo.InvariantCulture),
            PenDown = ((double)(PenDownBox.Value ?? 8m)).ToString(CultureInfo.InvariantCulture),
            InitialClearance = (double)(InitialClearanceBox.Value ?? 30m),
        };

        try
        {
            var font = FontData.Load(Path.Combine(_fontsDir, fontName + ".cmf"));
            var result = GCodeGenerator.Generate(TextInputBox.Text ?? "", fontName, font, settings, _boxes);

            foreach (var stroke in result.Strokes)
            {
                PreviewCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(stroke.X1 * PixelsPerMm, stroke.Y1 * PixelsPerMm),
                    EndPoint = new Point(stroke.X2 * PixelsPerMm, stroke.Y2 * PixelsPerMm),
                    Stroke = Brushes.Black,
                    StrokeThickness = 1.5,
                });
            }

            _lastGCode = result.GCode;
            SaveButton.IsEnabled = true;
            StatusText.Foreground = result.OutOfBounds ? Brushes.Crimson : Brushes.Gray;
            StatusText.Text = result.OutOfBounds
                ? "Warning: text goes out of the bed bounds."
                : $"Rendered {result.Strokes.Count} strokes.";
        }
        catch (UnsupportedCharacterException ex)
        {
            StatusText.Foreground = Brushes.Crimson;
            StatusText.Text = ex.Message;
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_lastGCode is null) return;

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "3dwriter.gcode",
            FileTypeChoices = new[] { new FilePickerFileType("GCode file") { Patterns = new[] { "*.gcode" } } },
        });

        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(_lastGCode);
    }
}
