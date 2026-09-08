using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private readonly string _fontsDir;
    private readonly List<GCodeGenerator.Box> _boxes = new();
    private string? _lastGCode;
    private Point? _dragStart;
    private Rectangle? _dragGhost;

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

        foreach (var box in _boxes) AddBoxVisual(box);
    }

    private void AddBoxVisual(GCodeGenerator.Box box)
    {
        var rect = new Rectangle
        {
            Width = box.Width * PixelsPerMm,
            Height = box.Height * PixelsPerMm,
            Stroke = Brushes.Black,
            StrokeThickness = 1.5,
        };
        Canvas.SetLeft(rect, box.X * PixelsPerMm);
        Canvas.SetTop(rect, box.Y * PixelsPerMm);
        PreviewCanvas.Children.Add(rect);
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
        AddBoxVisual(box);
        _lastGCode = null;
        SaveButton.IsEnabled = false;
    }

    private void OnClearBoxesClick(object? sender, RoutedEventArgs e)
    {
        _boxes.Clear();
        UpdateCanvasFrame();
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
