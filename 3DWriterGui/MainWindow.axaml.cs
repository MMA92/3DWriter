using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WriterCore;
using Line = Avalonia.Controls.Shapes.Line;
using Path = System.IO.Path;

namespace WriterGui;

/// <summary>
/// Minimal GUI wrapper around WriterCore.GCodeGenerator - the same logic the CLI
/// (3DWriterCli) uses. Bed size / offsets / speeds stay at WriterSettings defaults;
/// only text, font and scale are exposed here. Add more fields later if needed.
/// </summary>
public partial class MainWindow : Window
{
    private const double PixelsPerMm = 2.0; // matches the original app's default preview magnification

    private readonly string _fontsDir;
    private string? _lastGCode;

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
    }

    private void OnRenderClick(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "";
        SaveButton.IsEnabled = false;
        PreviewCanvas.Children.Clear();

        if (FontBox.SelectedItem is not string fontName)
        {
            StatusText.Text = "No font selected.";
            return;
        }

        var settings = new WriterSettings { Scale = ScaleSlider.Value };

        try
        {
            var font = FontData.Load(Path.Combine(_fontsDir, fontName + ".cmf"));
            var result = GCodeGenerator.Generate(TextInputBox.Text ?? "", fontName, font, settings);

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
