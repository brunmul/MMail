using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MMail.Core.Models;
using MMail.Core.State;

namespace MMail.App.ViewModels;

/// Base for the small display objects the XAML binds to.
public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    /// Everything changed: the row re-reads all its bindings.
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public sealed class SectionHeaderItem(string title)
{
    public string Title { get; } = title;
}

/// One Gmail-style row: tile, star, sender, subject - preview, time (or actions on hover).
public sealed class MessageItem(Email email, MailController model) : Bindable
{
    public Email Email { get; } = email;
    private Palette P => Palette.Current;

    public bool IsHovered
    {
        get => _hovered;
        set { if (_hovered != value) { _hovered = value; Refresh(); } }
    }
    private bool _hovered;

    public bool ShowAccountDot { get; set; }
    public string AccountColorHex { get; set; } = "7D7C76";

    private bool Outgoing => Email.Folder is "sent" or "drafts";

    public string SenderText
    {
        get
        {
            if (!Outgoing) return Email.SenderName;
            var to = Email.To.FirstOrDefault();
            if (string.IsNullOrEmpty(to)) return "(no recipient)";
            var lt = to.IndexOf('<');
            var name = lt > 0 ? to[..lt].Trim().Trim('"') : to;
            return $"To: {name}";
        }
    }

    public string Subject => Email.Subject;
    public string PreviewText => Email.Preview.Length == 0 ? "" : $" - {Email.Preview}";
    public string TimeText => FormatTime(Email.Date);
    public Windows.UI.Text.FontWeight Weight => Email.Unread ? FontWeights.Bold : FontWeights.Normal;

    public Brush RowBackground => Palette.Brush(Email.Unread ? P.Panel : P.ReadRow);
    public Brush Fg1 => Palette.Brush(P.Fg1);
    public Brush Fg3 => Palette.Brush(P.Fg3);
    public Brush TimeForeground => Palette.Brush(Email.Unread ? P.Fg1 : P.Fg3);

    public string StarGlyph => Email.Starred ? "" : "";
    public Brush StarForeground => Palette.Brush(Email.Starred ? P.Star : P.Fg4);

    /// The left tile: mark as read when unread, pin / unpin when read.
    public string TileGlyph => Email.Unread ? "" : model.IsPinned(Email) ? "" : "";
    public Brush TileForeground => Palette.Brush(Email.Unread ? P.Clay : P.Slate);
    public Brush TileBackground => Palette.Brush(Palette.WithAlpha(Email.Unread ? P.Clay : P.Slate, P.IsDark ? 0.2 : 0.12));
    public string TileHelp => Email.Unread ? "Mark as read (U)" : model.IsPinned(Email) ? "Unpin (U)" : "Pin to the top of the inbox (U)";

    public Visibility AccountDotVisibility => ShowAccountDot ? Visibility.Visible : Visibility.Collapsed;
    public Brush AccountBrush => Palette.Brush(Palette.Hex(AccountColorHex));
    public Visibility AttachmentVisibility => Email.HasAttachment ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HoverVisibility => IsHovered ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RestVisibility => IsHovered ? Visibility.Collapsed : Visibility.Visible;
    public string ReadToggleGlyph => Email.Unread ? "" : "";
    public string ReadToggleHelp => Email.Unread ? "Mark as read" : "Mark as unread";

    public static string FormatTime(DateTimeOffset date)
    {
        var local = date.LocalDateTime;
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("t");
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        if ((today - local.Date).TotalDays < 7) return local.ToString("ddd");
        return local.Year == today.Year ? local.ToString("d MMM") : local.ToString("d MMM yyyy");
    }
}

public sealed class SidebarItem(string id, string title, string glyph) : Bindable
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public int Count { get; set; }
    public bool IsCurrent { get; set; }
    public string? ColorHex { get; set; }

    public string CountText => Count > 0 ? Count.ToString() : "";
    public Windows.UI.Text.FontWeight Weight => IsCurrent || Count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
    public Brush Background => Palette.Brush(IsCurrent ? Palette.Current.Current : Microsoft.UI.Colors.Transparent);
    public Brush Foreground => Palette.Brush(IsCurrent ? Palette.Current.Fg1 : Palette.Current.Fg2);
    public Brush IconForeground => ColorHex is { } hex ? Palette.Brush(Palette.Hex(hex)) : Foreground;
}

public sealed class AccountItem(MailAccountConfig config)
{
    public string Id => config.Id;
    public string Email => config.Email;
    public string Name => config.DisplayName;
    public string Label => string.IsNullOrWhiteSpace(config.DisplayName) ? config.Email : $"{config.DisplayName} <{config.Email}>";
}

public sealed class RowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? MailTemplate { get; set; }
    public DataTemplate? HeaderTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => item is SectionHeaderItem ? HeaderTemplate! : MailTemplate!;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
