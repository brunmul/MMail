using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MMail.App;

/// Arcos: warm paper and one touch of clay (the look of Claude), light and dark.
/// The one source of the app's colors: XAML reads them as {ThemeResource Arcos…Brush},
/// the list rows from <see cref="Current"/>.
public sealed record Palette(
    Color Paper, Color Panel, Color ReadRow, Color Fg1, Color Fg2, Color Fg3, Color Fg4,
    Color Clay, Color ClayText, Color ClayWash, Color Border, Color Hover, Color CurrentPill,
    Color Star, Color Slate, Color Danger, bool IsDark)
{
    public static readonly Palette Light = new(
        Paper: Hex("F5F4ED"), Panel: Hex("FFFFFF"), ReadRow: Hex("FAF9F5"),
        Fg1: Hex("262624"), Fg2: Hex("5E5D59"), Fg3: Hex("7D7C76"), Fg4: Hex("A9A79F"),
        Clay: Hex("D97757"), ClayText: Hex("C15F3C"), ClayWash: Hex("F3E4DA"),
        Border: Hex("14000000"), Hover: Hex("0F000000"), CurrentPill: Hex("C7FFFFFF"),
        Star: Hex("D97757"), Slate: Hex("4A698C"), Danger: Hex("C4453A"), IsDark: false);

    public static readonly Palette Dark = new(
        Paper: Hex("1A1918"), Panel: Hex("2A2928"), ReadRow: Hex("262524"),
        Fg1: Hex("E8E6E1"), Fg2: Hex("B3B1AA"), Fg3: Hex("8C8A84"), Fg4: Hex("6B6965"),
        Clay: Hex("D97757"), ClayText: Hex("E08A6A"), ClayWash: Hex("38D97757"),
        Border: Hex("14FFFFFF"), Hover: Hex("0FFFFFFF"), CurrentPill: Hex("2EFFFFFF"),
        Star: Hex("D97757"), Slate: Hex("96A8BC"), Danger: Hex("E5847A"), IsDark: true);

    public static Palette Current { get; set; } = Light;

    public static Palette For(ElementTheme theme) => theme == ElementTheme.Dark ? Dark : Light;

    /// "RRGGBB" or "AARRGGBB".
    public static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        var v = Convert.ToUInt32(hex, 16);
        return hex.Length == 8
            ? ColorHelper.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : ColorHelper.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static Color WithAlpha(Color c, double alpha) => ColorHelper.FromArgb((byte)(alpha * 255), c.R, c.G, c.B);

    private static readonly Dictionary<Color, SolidColorBrush> Brushes = [];

    public static SolidColorBrush Brush(Color c)
    {
        if (!Brushes.TryGetValue(c, out var b)) Brushes[c] = b = new SolidColorBrush(c);
        return b;
    }

    /// Adds the Arcos brushes, and the list hover/selection colors, as theme resources.
    public static void Install(ResourceDictionary resources)
    {
        resources.ThemeDictionaries["Light"] = Dictionary(Light);
        resources.ThemeDictionaries["Dark"] = Dictionary(Dark);
    }

    private static ResourceDictionary Dictionary(Palette p)
    {
        var d = new ResourceDictionary();
        void Add(string name, Color c) => d[$"Arcos{name}Brush"] = new SolidColorBrush(c);
        Add("Paper", p.Paper); Add("Panel", p.Panel); Add("ReadRow", p.ReadRow);
        Add("Fg1", p.Fg1); Add("Fg2", p.Fg2); Add("Fg3", p.Fg3); Add("Fg4", p.Fg4);
        Add("Clay", p.Clay); Add("ClayText", p.ClayText); Add("ClayWash", p.ClayWash);
        Add("Border", p.Border); Add("Hover", p.Hover); Add("Current", p.CurrentPill); Add("Danger", p.Danger);
        // Rows: a soft fill on hover and on the keyboard's current row, no accent wash.
        foreach (var key in new[] { "ListViewItemBackgroundPointerOver", "ListViewItemBackgroundSelected",
                                    "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundPressed",
                                    "ListViewItemBackgroundSelectedPressed" })
            d[key] = new SolidColorBrush(p.Hover);
        return d;
    }
}
