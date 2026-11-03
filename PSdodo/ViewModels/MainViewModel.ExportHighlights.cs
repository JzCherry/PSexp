using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsExporter.Models;

namespace PsExporter.ViewModels;

public sealed class ExportHighlightChoice(ExportHighlight value, string name, string color)
{
    public ExportHighlight Value { get; } = value;
    public string Name { get; } = name;
    public IBrush Fill { get; } = new ImmutableSolidColorBrush(Color.Parse(color));
    public IBrush Swatch { get; } = new ImmutableSolidColorBrush(Blend(Color.Parse(color), 255, 0.15));
    public IBrush DarkInk { get; } = new ImmutableSolidColorBrush(Blend(Color.Parse(color), 255, 0.52));
    public IBrush LightInk { get; } = new ImmutableSolidColorBrush(Blend(Color.Parse(color), 0, 0.17));
    public IBrush PressedFill { get; } = new ImmutableSolidColorBrush(Blend(Color.Parse(color), 0, 0.28));
    public IBrush Tint { get; } = new ImmutableSolidColorBrush(Color.Parse(color), 0.20);
    public IBrush Outline { get; } = new ImmutableSolidColorBrush(Color.Parse(color), 0.45);
    public IBrush Hover { get; } = new ImmutableSolidColorBrush(Color.Parse(color), 0.08);

    private static Color Blend(Color color, byte target, double amount)
    {
        byte Channel(byte value) => (byte)Math.Round(value + (target - value) * amount);
        return Color.FromRgb(Channel(color.R), Channel(color.G), Channel(color.B));
    }
}

public sealed class ExportHighlightSetting : ObservableObject
{
    public static IReadOnlyList<ExportHighlightChoice> Palette { get; } =
    [
        new(ExportHighlight.Blue, "品牌蓝", "#1E65F2"),
        new(ExportHighlight.Sky, "晴空蓝", "#3379AA"),
        new(ExportHighlight.Teal, "海水青", "#007C87"),
        new(ExportHighlight.Green, "翡翠绿", "#14815F"),
        new(ExportHighlight.Leaf, "新叶绿", "#577A16"),
        new(ExportHighlight.Amber, "琥珀金", "#9B6900"),
        new(ExportHighlight.Orange, "柑橘橙", "#BD4F15"),
        new(ExportHighlight.Clay, "珊瑚红", "#BE4860"),
        new(ExportHighlight.Rose, "玫瑰粉", "#B14988"),
        new(ExportHighlight.Violet, "兰花紫", "#9259BC"),
        new(ExportHighlight.Iris, "鸢尾蓝", "#6264C3"),
        new(ExportHighlight.Slate, "银灰蓝", "#63748A")
    ];

    private ExportHighlightChoice _selected;

    internal ExportHighlightSetting(ExportMode mode, string name, ExportHighlight saved)
    {
        Mode = mode;
        Name = name;
        saved = saved switch
        {
            ExportHighlight.Denim => ExportHighlight.Sky,
            ExportHighlight.Lake => ExportHighlight.Teal,
            ExportHighlight.Plum => ExportHighlight.Violet,
            _ => saved
        };
        _selected = Palette.FirstOrDefault(choice => choice.Value == saved) ?? Palette[0];
    }

    public ExportMode Mode { get; }
    public string Name { get; }
    public IReadOnlyList<ExportHighlightChoice> Choices => Palette;
    public ExportHighlightChoice Selected
    {
        get => _selected;
        set
        {
            if (Palette.Contains(value)) SetProperty(ref _selected, value);
        }
    }
}

public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportHighlightEditorLabel))]
    private bool _isExportHighlightEditorOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditedExportHighlight))]
    private ExportMode _highlightEditorMode = ExportMode.QuickExport;

    public string ExportHighlightEditorLabel => IsExportHighlightEditorOpen ? "收起" : "自定义";
    public IReadOnlyList<ExportHighlightSetting> ExportHighlightSettings { get; }
    public ExportHighlightSetting EditedExportHighlight => ExportHighlightSettings.First(setting => setting.Mode == HighlightEditorMode);
    public ExportHighlightChoice CurrentExportHighlight
    {
        get
        {
            var mode = SelectedMode == ExportMode.SelectedArtboards ? ExportMode.AllArtboards : SelectedMode;
            return ExportHighlightSettings.FirstOrDefault(setting => setting.Mode == mode)?.Selected
                ?? ExportHighlightSetting.Palette[0];
        }
    }

    [RelayCommand]
    private void ToggleExportHighlightEditor() => IsExportHighlightEditorOpen = !IsExportHighlightEditorOpen;

    [RelayCommand]
    private void SelectHighlightEditorMode(ExportMode mode) => HighlightEditorMode = mode;

    private IReadOnlyList<ExportHighlightSetting> CreateExportHighlightSettings(AppSettings settings)
    {
        ExportHighlightSetting Create(ExportMode mode, string name)
        {
            var saved = settings.ExportHighlights?.GetValueOrDefault(mode) ?? ExportHighlight.Blue;
            var row = new ExportHighlightSetting(mode, name, saved);
            row.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(CurrentExportHighlight));
                SaveBehaviorSettings();
            };
            return row;
        }
        return
        [
            Create(ExportMode.QuickExport, "快速导出"),
            Create(ExportMode.AllArtboards, "画板导出"),
            Create(ExportMode.Layers, "图层导出"),
            Create(ExportMode.Slices, "切片导出")
        ];
    }
}
