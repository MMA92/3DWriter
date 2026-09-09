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
    private static readonly IBrush SelectedBoxBrush = Brushes.DodgerBlue;
    private static readonly JsonSerializerOptions BoxesJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private const int MaxHistory = 10;
    private readonly string _fontsDir;
    private readonly List<GCodeGenerator.Box> _boxes = new();
    private readonly List<int> _boxGroups = new(); // parallel to _boxes: boxes from one load/draw move together
    private int _nextGroupId;
    private int? _selectedGroup; // group id of the last clicked box; Delete removes every box in it -
                                  // a lone hand-drawn box is its own group, a JSON-loaded set shares one
    private readonly List<BoxesSnapshot> _undoStack = new();
    private readonly List<BoxesSnapshot> _redoStack = new();
    private string? _lastGCode;
    private Point? _dragStart;
    private Rectangle? _dragGhost;
    private List<(Rectangle Rect, double Left, double Top, int Index)>? _dragGroup;
    private Point _dragStartPointer;

    private readonly record struct BoxesSnapshot(List<GCodeGenerator.Box> Boxes, List<int> Groups);

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

        // Tunnel (not the default Bubble) so this runs before a focused TextBox/NumericUpDown
        // can swallow Ctrl+Z/Ctrl+Y/Delete for its own text editing.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        UpdateCanvasFrame();
        PopulateFormsSidebar();
    }

    /// <summary>Scans fonts-dir-sibling "forms" for *.json box files and lists each as a
    /// dynamically drawn thumbnail in the left sidebar; dragging one onto the preview loads
    /// it the same way an OS file drop does (see OnFormItemPointerPressed).</summary>
    private void PopulateFormsSidebar()
    {
        var formsDir = Path.Combine(AppContext.BaseDirectory, "forms");
        if (!Directory.Exists(formsDir)) return;

        foreach (var file in Directory.GetFiles(formsDir, "*.json").OrderBy(f => f))
        {
            List<GCodeGenerator.Box>? boxes;
            try
            {
                boxes = JsonSerializer.Deserialize<List<GCodeGenerator.Box>>(File.ReadAllText(file), BoxesJsonOptions);
            }
            catch (JsonException)
            {
                continue; // skip files that aren't valid boxes JSON
            }
            if (boxes is null || boxes.Count == 0) continue;

            FormsPanel.Children.Add(BuildFormListItem(Path.GetFileNameWithoutExtension(file), boxes, file));
        }
    }

    private const double FormThumbSize = 96;

    private Control BuildFormListItem(string name, List<GCodeGenerator.Box> boxes, string filePath)
    {
        double minX = boxes.Min(b => b.X), minY = boxes.Min(b => b.Y);
        double w = Math.Max(boxes.Max(b => b.X + b.Width) - minX, 0.001);
        double h = Math.Max(boxes.Max(b => b.Y + b.Height) - minY, 0.001);
        double scale = Math.Min(FormThumbSize / w, FormThumbSize / h);

        var canvas = new Canvas { Width = FormThumbSize, Height = FormThumbSize };
        foreach (var b in boxes)
        {
            var rect = new Rectangle
            {
                Width = b.Width * scale,
                Height = b.Height * scale,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Colors.LightSteelBlue, 0.4),
            };
            Canvas.SetLeft(rect, (b.X - minX) * scale);
            Canvas.SetTop(rect, (b.Y - minY) * scale);
            canvas.Children.Add(rect);
        }

        var thumb = new Border
        {
            Width = FormThumbSize,
            Height = FormThumbSize,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = filePath,
            Child = canvas,
        };
        thumb.PointerPressed += OnFormItemPointerPressed;

        var item = new StackPanel { Spacing = 4 };
        item.Children.Add(thumb);
        item.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 10,
            Width = FormThumbSize,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        return item;
    }

    /// <summary>Starts an OS-level drag carrying the form's file, so dropping it on the
    /// preview reuses the exact same OnPreviewDrop -> LoadBoxesFromFileAsync path as dragging
    /// a file in from a file manager.</summary>
    private async void OnFormItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint((Control)sender!).Properties.IsLeftButtonPressed) return;
        var path = (string)((Border)sender!).Tag!;

        var storage = GetTopLevel(this)?.StorageProvider;
        var file = storage is null ? null : await storage.TryGetFileFromPathAsync(path);
        if (file is null) return;

        var data = new DataObject();
        data.Set(DataFormats.Files, new IStorageItem[] { file });
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy);
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
        bool selected = _selectedGroup == _boxGroups[index];
        var rect = new Rectangle
        {
            Width = box.Width * PixelsPerMm,
            Height = box.Height * PixelsPerMm,
            Stroke = selected ? SelectedBoxBrush : Brushes.Black,
            StrokeThickness = selected ? 2.5 : 1.5,
            Fill = Brushes.Transparent, // makes the whole box (not just its outline) hit-testable for dragging
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Tag = index,
        };
        Canvas.SetLeft(rect, box.X * PixelsPerMm);
        Canvas.SetTop(rect, box.Y * PixelsPerMm);
        rect.PointerPressed += OnBoxPointerPressed;
        PreviewCanvas.Children.Add(rect);
    }

    /// <summary>Re-strokes existing box rectangles to match _selectedGroup without a full
    /// redraw (cheap enough to call on every click, and avoids replacing the Rectangle
    /// instances a drag is about to reference).</summary>
    private void RefreshSelectionHighlight()
    {
        foreach (var r in PreviewCanvas.Children.OfType<Rectangle>())
        {
            if (r.Tag is not int idx) continue;
            bool selected = _selectedGroup == _boxGroups[idx];
            r.Stroke = selected ? SelectedBoxBrush : Brushes.Black;
            r.StrokeThickness = selected ? 2.5 : 1.5;
        }
    }

    /// <summary>Starts moving an existing box instead of drawing a new one; e.Handled stops
    /// the click from also reaching OnCanvasPointerPressed (bubbles from rect to canvas). Boxes
    /// loaded from the same JSON file (or drawn as one box) share a group id and move together
    /// as a rigid unit, whichever one of them was grabbed. Right-click instead rotates that
    /// same rigid unit by 90°.</summary>
    private void OnBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(PreviewCanvas).Properties;
        int group = _boxGroups[(int)((Rectangle)sender!).Tag!];
        _selectedGroup = group;

        if (props.IsRightButtonPressed)
        {
            RotateGroup(group);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        RefreshSelectionHighlight();
        _dragGroup = PreviewCanvas.Children.OfType<Rectangle>()
            .Where(r => r.Tag is int idx && _boxGroups[idx] == group)
            .Select(r => (Rect: r, Left: Canvas.GetLeft(r), Top: Canvas.GetTop(r), Index: (int)r.Tag!))
            .ToList();
        _dragStartPointer = e.GetPosition(PreviewCanvas);
        e.Pointer.Capture(PreviewCanvas);
        e.Handled = true;
    }

    /// <summary>Rotates every box sharing <paramref name="group"/> by 90° as one rigid unit:
    /// each box's own center orbits the group's combined bounding-box center by 90°, and each
    /// box swaps Width/Height. A lone box's center equals the group center, so it just rotates
    /// in place - no separate single-box case needed.</summary>
    private void RotateGroup(int group)
    {
        var indices = Enumerable.Range(0, _boxes.Count).Where(i => _boxGroups[i] == group).ToList();
        double gcx = indices.Average(i => _boxes[i].X + _boxes[i].Width / 2);
        double gcy = indices.Average(i => _boxes[i].Y + _boxes[i].Height / 2);

        SaveUndoState();
        foreach (int i in indices)
        {
            var b = _boxes[i];
            double bcx = b.X + b.Width / 2, bcy = b.Y + b.Height / 2;
            double dx = bcx - gcx, dy = bcy - gcy;
            double ncx = gcx - dy, ncy = gcy + dx; // rotate center 90° around group center
            _boxes[i] = new GCodeGenerator.Box(ncx - b.Height / 2, ncy - b.Width / 2, b.Height, b.Width);
        }
        UpdateCanvasFrame();
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewCanvas).Properties.IsLeftButtonPressed) return;
        _selectedGroup = null; // clicking empty preview deselects
        RefreshSelectionHighlight();
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
            SaveUndoState();
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

        SaveUndoState();
        var box = new GCodeGenerator.Box(x / PixelsPerMm, y / PixelsPerMm, w / PixelsPerMm, h / PixelsPerMm);
        _boxes.Add(box);
        _boxGroups.Add(_nextGroupId++);
        AddBoxVisual(box, _boxes.Count - 1);
        _lastGCode = null;
        SaveButton.IsEnabled = false;
    }

    /// <summary>Delete removes the whole selected group: a hand-drawn box is its own
    /// group (deletes just that one), a JSON-loaded set shares a group (deletes the unit).
    /// Ctrl+Z/Ctrl+Y mirror the Undo/Redo buttons. Text controls consume these keys for their
    /// own edit-undo first (setting e.Handled), so this only fires for the box layout.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z) { OnUndoClick(sender, e); e.Handled = true; return; }
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Y) { OnRedoClick(sender, e); e.Handled = true; return; }

        if (e.Key != Key.Delete || _selectedGroup is not { } group) return;

        SaveUndoState();
        for (int i = _boxes.Count - 1; i >= 0; i--)
        {
            if (_boxGroups[i] == group) { _boxes.RemoveAt(i); _boxGroups.RemoveAt(i); }
        }
        _selectedGroup = null;
        UpdateCanvasFrame();
        e.Handled = true;
    }

    private void OnClearBoxesClick(object? sender, RoutedEventArgs e)
    {
        if (_boxes.Count == 0) return;
        SaveUndoState();
        _boxes.Clear();
        _boxGroups.Clear();
        _selectedGroup = null;
        UpdateCanvasFrame();
    }

    /// <summary>Snapshots the current boxes before a mutation, capped at the last MaxHistory
    /// commands. Any pending redo is discarded, since it no longer follows from this state.</summary>
    private void SaveUndoState()
    {
        _undoStack.Add(new BoxesSnapshot(new List<GCodeGenerator.Box>(_boxes), new List<int>(_boxGroups)));
        if (_undoStack.Count > MaxHistory) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        UpdateUndoRedoButtons();
    }

    private void RestoreState(List<BoxesSnapshot> from, List<BoxesSnapshot> to)
    {
        to.Add(new BoxesSnapshot(new List<GCodeGenerator.Box>(_boxes), new List<int>(_boxGroups)));
        if (to.Count > MaxHistory) to.RemoveAt(0);

        var state = from[^1];
        from.RemoveAt(from.Count - 1);
        _boxes.Clear();
        _boxes.AddRange(state.Boxes);
        _boxGroups.Clear();
        _boxGroups.AddRange(state.Groups);

        UpdateCanvasFrame();
        UpdateUndoRedoButtons();
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0) return;
        RestoreState(_undoStack, _redoStack);
    }

    private void OnRedoClick(object? sender, RoutedEventArgs e)
    {
        if (_redoStack.Count == 0) return;
        RestoreState(_redoStack, _undoStack);
    }

    private void UpdateUndoRedoButtons()
    {
        UndoButton.IsEnabled = _undoStack.Count > 0;
        RedoButton.IsEnabled = _redoStack.Count > 0;
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

    private async void OnSaveBoxesClick(object? sender, RoutedEventArgs e)
    {
        if (_boxes.Count == 0)
        {
            StatusText.Foreground = Brushes.Crimson;
            StatusText.Text = "No boxes to save.";
            return;
        }

        var storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "boxes.json",
            FileTypeChoices = new[] { new FilePickerFileType("Boxes JSON") { Patterns = new[] { "*.json" } } },
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await JsonSerializer.SerializeAsync(stream, _boxes, new JsonSerializerOptions { WriteIndented = true });
        StatusText.Foreground = Brushes.Gray;
        StatusText.Text = $"Saved {_boxes.Count} box(es) to {file.Name}.";
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

            SaveUndoState();
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
