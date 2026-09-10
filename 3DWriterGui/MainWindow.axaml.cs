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
using Point = Avalonia.Point; // WriterCore also has a Point (shape coordinates) - disambiguate
using Line = Avalonia.Controls.Shapes.Line;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Polygon = Avalonia.Controls.Shapes.Polygon;
using Polyline = Avalonia.Controls.Shapes.Polyline;
using ShapeControl = Avalonia.Controls.Shapes.Shape;
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
    private static readonly IBrush BlockedZoneBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128));
    private static readonly IBrush BlockedZoneTextBrush = new SolidColorBrush(Color.FromArgb(200, 60, 60, 60));
    private static readonly IBrush SelectedBoxBrush = Brushes.DodgerBlue;
    private static readonly JsonSerializerOptions BoxesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new ShapeJsonConverter() },
    };

    private const int MaxHistory = 10;
    private readonly string _fontsDir;
    private readonly List<IShape> _shapes = new();
    private readonly List<int> _boxGroups = new(); // parallel to _shapes: shapes from one load/draw move together
    private int _nextGroupId;
    private int? _selectedGroup; // group id of the last clicked shape; Delete removes every shape in it -
                                  // a lone hand-drawn box is its own group, a JSON-loaded set shares one
    private readonly List<BoxesSnapshot> _undoStack = new();
    private readonly List<BoxesSnapshot> _redoStack = new();
    private string? _lastGCode;
    private Point? _dragStart;
    private Rectangle? _dragGhost;
    private List<(ShapeControl Control, double Left, double Top, int Index)>? _dragGroup;
    private Point _dragStartPointer;
    private WriterSettings _blockedMargins = new(); // BlockedMargin* only, loaded from settings.json (no UI controls for these)

    private readonly record struct BoxesSnapshot(List<IShape> Shapes, List<int> Groups);

    public MainWindow()
    {
        InitializeComponent();

        _fontsDir = Path.Combine(AppContext.BaseDirectory, "fonts");
        var fonts = Directory.Exists(_fontsDir)
            ? Directory.GetFiles(_fontsDir, "*.cmf").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToList()
            : new List<string?>();

        FontBox.ItemsSource = fonts;
        FontBox.SelectedItem = fonts.Contains("cursive") ? "cursive" : fonts.FirstOrDefault();

        // settings.json next to the binary (created there on first run) supplies the
        // starting values below - edit that file instead of these controls to change
        // the defaults permanently. Controls can still be adjusted per-run afterwards.
        var defaults = WriterSettings.Load(Path.Combine(AppContext.BaseDirectory, "settings.json"));
        _blockedMargins = defaults;
        ScaleSlider.Value = defaults.Scale;
        BedWidthBox.Value = (decimal)defaults.BedWidth;
        BedHeightBox.Value = (decimal)defaults.BedHeight;
        OffsetXBox.Value = (decimal)defaults.OffsetX;
        OffsetYBox.Value = (decimal)defaults.OffsetY;
        ToolOffsetXBox.Value = (decimal)defaults.ToolOffsetX;
        ToolOffsetYBox.Value = (decimal)defaults.ToolOffsetY;
        InitialClearanceBox.Value = (decimal)defaults.InitialClearance;
        if (double.TryParse(defaults.PenUp, NumberStyles.Float, CultureInfo.InvariantCulture, out var penUp))
            PenUpBox.Value = (decimal)penUp;
        if (double.TryParse(defaults.PenDown, NumberStyles.Float, CultureInfo.InvariantCulture, out var penDown))
            PenDownBox.Value = (decimal)penDown;

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
            List<IShape>? shapes;
            try
            {
                shapes = JsonSerializer.Deserialize<List<IShape>>(File.ReadAllText(file), BoxesJsonOptions);
            }
            catch (JsonException)
            {
                continue; // skip files that aren't valid boxes JSON
            }
            if (shapes is null || shapes.Count == 0) continue;

            FormsPanel.Children.Add(BuildFormListItem(Path.GetFileNameWithoutExtension(file), shapes, file));
        }
    }

    private const double FormThumbSize = 96;

    private Control BuildFormListItem(string name, List<IShape> shapes, string filePath)
    {
        var allPts = shapes.SelectMany(s => s.GetPoints()).ToList();
        double minX = allPts.Min(p => p.X), minY = allPts.Min(p => p.Y);
        double w = Math.Max(allPts.Max(p => p.X) - minX, 0.001);
        double h = Math.Max(allPts.Max(p => p.Y) - minY, 0.001);
        double scale = Math.Min(FormThumbSize / w, FormThumbSize / h);

        var canvas = new Canvas { Width = FormThumbSize, Height = FormThumbSize };
        foreach (var shape in shapes)
        {
            var control = BuildShapeControl(shape, scale, minX, minY);
            control.Fill = new SolidColorBrush(Colors.LightSteelBlue, 0.4);
            canvas.Children.Add(control);
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

    private double BedWidth => (double)(BedWidthBox.Value ?? 210m);
    private double BedHeight => (double)(BedHeightBox.Value ?? 210m);
    private double OffsetX => (double)(OffsetXBox.Value ?? 45m);
    private double OffsetY => (double)(OffsetYBox.Value ?? 45m);
    private double ToolOffsetX => (double)(ToolOffsetXBox.Value ?? -10m);
    private double ToolOffsetY => (double)(ToolOffsetYBox.Value ?? 10m);

    /// <summary>Grays out the bed margins the pen mount physically can't reach (see
    /// WriterSettings.BlockedMargin* / _blockedMargins - loaded from settings.json so the
    /// preview and the actual generation-blocking check never drift apart), labeled "Blocked
    /// area", rotated on the narrow left/right bands so the text reads sideways instead of overflowing.</summary>
    private void AddBlockedZoneVisuals()
    {
        double w = PreviewCanvas.Width, h = PreviewCanvas.Height;
        double left = _blockedMargins.BlockedMarginLeft * PixelsPerMm;
        double right = _blockedMargins.BlockedMarginRight * PixelsPerMm;
        double top = _blockedMargins.BlockedMarginTop * PixelsPerMm;
        double bottom = _blockedMargins.BlockedMarginBottom * PixelsPerMm;

        AddBlockedZoneRect(0, 0, left, h, vertical: true);
        AddBlockedZoneRect(w - right, 0, right, h, vertical: true);
        AddBlockedZoneRect(0, 0, w, top, vertical: false);
        AddBlockedZoneRect(0, h - bottom, w, bottom, vertical: false);
    }

    private void AddBlockedZoneRect(double x, double y, double width, double height, bool vertical)
    {
        if (width <= 0 || height <= 0) return;

        var rect = new Rectangle { Width = width, Height = height, Fill = BlockedZoneBrush, IsHitTestVisible = false };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        PreviewCanvas.Children.Add(rect);

        var label = new TextBlock { Text = "Blocked area", FontSize = 10, Foreground = BlockedZoneTextBrush, IsHitTestVisible = false };
        if (vertical)
        {
            label.RenderTransform = new RotateTransform(90); // rotate around its own center, positioned below at the band's center either way
            label.RenderTransformOrigin = RelativePoint.Center;
        }
        label.Measure(Size.Infinity);
        Canvas.SetLeft(label, x + width / 2 - label.DesiredSize.Width / 2);
        Canvas.SetTop(label, y + height / 2 - label.DesiredSize.Height / 2);
        PreviewCanvas.Children.Add(label);
    }

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

        AddBlockedZoneVisuals(); // gray out bed margins the pen mount can't reach, drawn first so shapes/crosshair sit on top

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

        for (int i = 0; i < _shapes.Count; i++) AddShapeVisual(_shapes[i], i);
    }

    /// <summary>Builds an Avalonia shape control from a shape's points - a filled Polygon for
    /// closed shapes (box, triangle, star, arrow, ...), a plain Polyline for open ones (line).
    /// Points are scaled and made relative to (originX, originY); shared by the form thumbnails
    /// and the main preview canvas, which then position/style the result differently.</summary>
    private static ShapeControl BuildShapeControl(IShape shape, double scale, double originX, double originY)
    {
        var pts = new AvaloniaList<Point>(shape.GetPoints().Select(p => new Point((p.X - originX) * scale, (p.Y - originY) * scale)));
        ShapeControl control = shape.Closed ? new Polygon { Points = pts } : new Polyline { Points = pts };
        control.Stroke = Brushes.Black;
        control.StrokeThickness = 1;
        return control;
    }

    private void AddShapeVisual(IShape shape, int index)
    {
        var pts = shape.GetPoints();
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
        double maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);

        bool selected = _selectedGroup == _boxGroups[index];
        var control = BuildShapeControl(shape, PixelsPerMm, minX, minY);
        control.Width = (maxX - minX) * PixelsPerMm;
        control.Height = (maxY - minY) * PixelsPerMm;
        control.Stroke = selected ? SelectedBoxBrush : Brushes.Black;
        control.StrokeThickness = selected ? 2.5 : 1.5;
        if (shape.Closed) control.Fill = Brushes.Transparent; // hit-testable across the whole interior, not just the outline
        control.Cursor = new Cursor(StandardCursorType.SizeAll);
        control.Tag = index;
        Canvas.SetLeft(control, minX * PixelsPerMm);
        Canvas.SetTop(control, minY * PixelsPerMm);
        control.PointerPressed += OnBoxPointerPressed;
        PreviewCanvas.Children.Add(control);
    }

    /// <summary>Re-strokes existing shape visuals to match _selectedGroup without a full
    /// redraw (cheap enough to call on every click, and avoids replacing the control
    /// instances a drag is about to reference).</summary>
    private void RefreshSelectionHighlight()
    {
        foreach (var r in PreviewCanvas.Children.OfType<ShapeControl>())
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
        int group = _boxGroups[(int)((ShapeControl)sender!).Tag!];
        _selectedGroup = group;

        if (props.IsRightButtonPressed)
        {
            RotateGroup(group);
            e.Handled = true;
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        RefreshSelectionHighlight();
        _dragGroup = PreviewCanvas.Children.OfType<ShapeControl>()
            .Where(r => r.Tag is int idx && _boxGroups[idx] == group)
            .Select(r => (Control: r, Left: Canvas.GetLeft(r), Top: Canvas.GetTop(r), Index: (int)r.Tag!))
            .ToList();
        _dragStartPointer = e.GetPosition(PreviewCanvas);
        e.Pointer.Capture(PreviewCanvas);
        e.Handled = true;
    }

    /// <summary>Rotates every shape sharing <paramref name="group"/> by 90° as one rigid unit:
    /// each shape's own centroid orbits the group's average center by 90°. A Box stays a Box
    /// (its two centers coincide, so a lone box just swaps Width/Height in place); anything
    /// else becomes a Polygon of its rotated points - still fully editable, just no longer
    /// expressible in its original compact form once off-axis.</summary>
    private void RotateGroup(int group)
    {
        var indices = Enumerable.Range(0, _shapes.Count).Where(i => _boxGroups[i] == group).ToList();
        double gcx = indices.Average(i => _shapes[i].GetPoints().Average(p => p.X));
        double gcy = indices.Average(i => _shapes[i].GetPoints().Average(p => p.Y));

        SaveUndoState();
        foreach (int i in indices) _shapes[i] = Rotate90(_shapes[i], gcx, gcy);
        UpdateCanvasFrame();
    }

    private static IShape Rotate90(IShape shape, double cx, double cy)
    {
        if (shape is Box b)
        {
            double bcx = b.X + b.Width / 2, bcy = b.Y + b.Height / 2;
            double ncx = cx - (bcy - cy), ncy = cy + (bcx - cx);
            return new Box(ncx - b.Height / 2, ncy - b.Width / 2, b.Height, b.Width);
        }

        var pts = shape.GetPoints().Select(p => new WriterCore.Point(cx - (p.Y - cy), cy + (p.X - cx))).ToList();
        return new WriterCore.Polygon(pts, shape.Closed);
    }

    private static IShape Translate(IShape shape, double dx, double dy) => shape switch
    {
        Box b => b with { X = b.X + dx, Y = b.Y + dy },
        WriterCore.Line l => l with { X = l.X + dx, Y = l.Y + dy, X2 = l.X2 + dx, Y2 = l.Y2 + dy },
        Triangle t => t with { X = t.X + dx, Y = t.Y + dy },
        Star st => st with { X = st.X + dx, Y = st.Y + dy },
        Arrow a => a with { X = a.X + dx, Y = a.Y + dy },
        WriterCore.Polygon p => p with { Points = p.Points.Select(pt => new WriterCore.Point(pt.X + dx, pt.Y + dy)).ToList() },
        _ => shape,
    };

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
            // stays on the bed and none of its shapes get left behind or resized.
            double minLeft = _dragGroup.Min(i => i.Left);
            double minTop = _dragGroup.Min(i => i.Top);
            double maxRight = _dragGroup.Max(i => i.Left + i.Control.Width);
            double maxBottom = _dragGroup.Max(i => i.Top + i.Control.Height);
            dx = Math.Clamp(dx, -minLeft, PreviewCanvas.Width - maxRight);
            dy = Math.Clamp(dy, -minTop, PreviewCanvas.Height - maxBottom);

            foreach (var item in _dragGroup)
            {
                Canvas.SetLeft(item.Control, item.Left + dx);
                Canvas.SetTop(item.Control, item.Top + dy);
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
                double dx = (Canvas.GetLeft(item.Control) - item.Left) / PixelsPerMm;
                double dy = (Canvas.GetTop(item.Control) - item.Top) / PixelsPerMm;
                _shapes[item.Index] = Translate(_shapes[item.Index], dx, dy);
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
        var box = new Box(x / PixelsPerMm, y / PixelsPerMm, w / PixelsPerMm, h / PixelsPerMm);
        _shapes.Add(box);
        _boxGroups.Add(_nextGroupId++);
        AddShapeVisual(box, _shapes.Count - 1);
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
        for (int i = _shapes.Count - 1; i >= 0; i--)
        {
            if (_boxGroups[i] == group) { _shapes.RemoveAt(i); _boxGroups.RemoveAt(i); }
        }
        _selectedGroup = null;
        UpdateCanvasFrame();
        e.Handled = true;
    }

    private void OnClearBoxesClick(object? sender, RoutedEventArgs e)
    {
        if (_shapes.Count == 0) return;
        SaveUndoState();
        _shapes.Clear();
        _boxGroups.Clear();
        _selectedGroup = null;
        UpdateCanvasFrame();
    }

    /// <summary>Snapshots the current shapes before a mutation, capped at the last MaxHistory
    /// commands. Any pending redo is discarded, since it no longer follows from this state.</summary>
    private void SaveUndoState()
    {
        _undoStack.Add(new BoxesSnapshot(new List<IShape>(_shapes), new List<int>(_boxGroups)));
        if (_undoStack.Count > MaxHistory) _undoStack.RemoveAt(0);
        _redoStack.Clear();
        UpdateUndoRedoButtons();
    }

    private void RestoreState(List<BoxesSnapshot> from, List<BoxesSnapshot> to)
    {
        to.Add(new BoxesSnapshot(new List<IShape>(_shapes), new List<int>(_boxGroups)));
        if (to.Count > MaxHistory) to.RemoveAt(0);

        var state = from[^1];
        from.RemoveAt(from.Count - 1);
        _shapes.Clear();
        _shapes.AddRange(state.Shapes);
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
        if (_shapes.Count == 0)
        {
            StatusText.Foreground = Brushes.Crimson;
            StatusText.Text = "No shapes to save.";
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

        var saveOptions = new JsonSerializerOptions(BoxesJsonOptions) { WriteIndented = true };
        await using var stream = await file.OpenWriteAsync();
        await JsonSerializer.SerializeAsync(stream, _shapes, saveOptions);
        StatusText.Foreground = Brushes.Gray;
        StatusText.Text = $"Saved {_shapes.Count} shape(s) to {file.Name}.";
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
            var shapes = JsonSerializer.Deserialize<List<IShape>>(json, BoxesJsonOptions);
            if (shapes is null || shapes.Count == 0)
            {
                StatusText.Foreground = Brushes.Crimson;
                StatusText.Text = "Boxes JSON was empty or invalid.";
                return;
            }

            SaveUndoState();
            int group = _nextGroupId++;
            _shapes.AddRange(shapes);
            _boxGroups.AddRange(Enumerable.Repeat(group, shapes.Count));
            UpdateCanvasFrame();
            StatusText.Foreground = Brushes.Gray;
            StatusText.Text = $"Loaded {shapes.Count} shape(s) from {file.Name}.";
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
            ToolOffsetX = ToolOffsetX,
            ToolOffsetY = ToolOffsetY,
            PenUp = ((double)(PenUpBox.Value ?? 12m)).ToString(CultureInfo.InvariantCulture),
            PenDown = ((double)(PenDownBox.Value ?? 8m)).ToString(CultureInfo.InvariantCulture),
            InitialClearance = (double)(InitialClearanceBox.Value ?? 30m),
            BlockedMarginLeft = _blockedMargins.BlockedMarginLeft,
            BlockedMarginRight = _blockedMargins.BlockedMarginRight,
            BlockedMarginTop = _blockedMargins.BlockedMarginTop,
            BlockedMarginBottom = _blockedMargins.BlockedMarginBottom,
        };

        try
        {
            var font = FontData.Load(Path.Combine(_fontsDir, fontName + ".cmf"));
            var result = GCodeGenerator.Generate(TextInputBox.Text ?? "", fontName, font, settings, _shapes);

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
        catch (BlockedAreaException ex)
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
