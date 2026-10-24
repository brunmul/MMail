using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MMail.App.ViewModels;
using MMail.Core.Mail;
using MMail.Core.Models;
using MMail.Core.State;
using MMail.Core.Storage;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MMail.App;

public sealed partial class MainWindow : Window
{
    private readonly MailController _model;
    private readonly MailStore _store;
    private readonly MailListViewModel _list;
    private readonly DispatcherQueueTimer _poll;
    private readonly DispatcherQueueTimer _toastTimer;
    private ToastMessage? _toast;
    private bool _refreshQueued, _closing, _showImagesOnce;
    private Email? _shown;

    public MainWindow(MailController model, MailStore store)
    {
        InitializeComponent();
        _model = model;
        _store = store;
        _list = new MailListViewModel(model);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "MMail.ico"));
        AppWindow.Resize(new SizeInt32(1380, 880));
        AppIcon.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "MMail.png")));

        ApplyTheme(store.GetPref<string>("theme") ?? "system");
        Palette.Current = Palette.For(Root.ActualTheme);
        Root.ActualThemeChanged += (_, _) => { Palette.Current = Palette.For(Root.ActualTheme); QueueRefresh(); };

        MailList.ItemsSource = _list.Rows;
        model.Changed += QueueRefresh;
        model.Toast += ShowToast;
        model.ComposeChanged += OnComposeChanged;
        model.OpenMessageChanged += OnOpenMessageChanged;
        model.BodyLoadFailed += id => { if (_shown?.Id == id) ReaderLoading.IsActive = false; };

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Tick += (_, _) => HideToast();
        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(60);
        _poll.Tick += (_, _) => _ = model.RefreshAsync();

        Root.Loaded += OnLoaded;
        AppWindow.Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupLog.Write("window loaded");
        Refresh();
        await InitReader();
        _poll.Start();
        if (_model.Accounts.Count == 0) await ShowAccountDialog(firstRun: true);
        else await _model.RefreshAsync();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing) return;
        // Archive/delete wait for their Undo window: run them before quitting.
        args.Cancel = true;
        _closing = true;
        _poll.Stop();
        await _model.FlushPendingAsync();
        Close();
    }

    public void Minimize() => (AppWindow.Presenter as OverlappedPresenter)?.Minimize();

    private void ApplyTheme(string theme) =>
        Root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };

    // MARK: Refresh

    /// Many model changes arrive together; redraw once per dispatcher turn.
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        DispatcherQueue.TryEnqueue(() => { _refreshQueued = false; Refresh(); });
    }

    private void Refresh()
    {
        _list.Sync();
        RefreshSidebar();
        RefreshHeader();
    }

    private static readonly Dictionary<string, string> FolderGlyphs = new()
    {
        ["inbox"] = "", ["starred"] = "", ["snoozed"] = "", ["archive"] = "",
        ["sent"] = "", ["drafts"] = "", ["spam"] = "", ["trash"] = "",
    };

    private void RefreshSidebar()
    {
        var inFolder = _model.LabelFilter is null && !_model.SearchActive;
        FolderList.ItemsSource = Folders.Standard.Select(f => new SidebarItem($"folder:{f}", Folders.Title(f), FolderGlyphs[f])
        {
            IsCurrent = inFolder && _model.Folder == f,
            Count = f is "inbox" or "spam" or "snoozed" ? _model.UnreadCount(f) : 0,
        }).ToList();

        LabelsTitle.Visibility = _model.Labels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LabelList.ItemsSource = _model.Labels.OrderBy(l => l.Name).Select(l => new SidebarItem($"label:{l.Id}", l.Name, "")
        {
            IsCurrent = _model.LabelFilter == l.Id, ColorHex = l.ColorHex,
        }).ToList();

        var unread = _model.UnreadByAccount();
        var accounts = new List<SidebarItem>();
        if (_model.Accounts.Count > 1)
            accounts.Add(new SidebarItem("account:all", "All inboxes", "") { IsCurrent = _model.CurrentAccount == "all" });
        accounts.AddRange(_model.Accounts.Select(a => new SidebarItem($"account:{a.Id}", a.Email, "")
        {
            IsCurrent = _model.CurrentAccount == a.Id && _model.Accounts.Count > 1,
            Count = unread.GetValueOrDefault(a.Id), ColorHex = Sender.StableColorHex(a.Id),
        }));
        AccountsTitle.Visibility = accounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountList.ItemsSource = accounts;
        SyncRing.IsActive = _model.LoadingAccounts.Count > 0;
    }

    private void RefreshHeader()
    {
        var count = _list.MessageRows.Count();
        FolderTitle.Text = _model.SearchActive ? $"Search: {_model.SearchQuery}"
            : _model.LabelFilter is { } label ? _model.Labels.FirstOrDefault(l => l.Id == label)?.Name ?? label
            : Folders.Title(_model.Folder);
        FolderCount.Text = count == 0 ? "" : count == 1 ? "1 message" : $"{count} messages";
        ErrorText.Text = _model.Error ?? "";
        ClearSearchButton.Visibility = _model.SearchActive ? Visibility.Visible : Visibility.Collapsed;
        OlderButton.Visibility = !_model.SearchActive && _model.LabelFilter is null && Folders.IsServerFolder(_model.Folder) && count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _model.Accounts.Count == 0 ? "Add an account to get started"
            : _model.LoadingAccounts.Count > 0 ? "Loading…"
            : _model.SearchActive ? "No results" : "Nothing here";
        RefreshTabs();
    }

    private void RefreshTabs()
    {
        var show = _model.Folder == "inbox" && _model.LabelFilter is null && !_model.SearchActive;
        Tabs.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        Tabs.Children.Clear();
        var p = Palette.Current;
        foreach (var filter in Enum.GetValues<InboxFilter>())
        {
            var active = _model.Filter == filter;
            var button = new Button
            {
                Style = (Style)Root.Resources["TabButton"],
                Content = new TextBlock
                {
                    Text = filter.ToString(), FontSize = 13.5,
                    FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = Palette.Brush(active ? p.ClayText : p.Fg2),
                },
                BorderBrush = Palette.Brush(active ? p.Clay : Colors.Transparent),
                BorderThickness = new Thickness(0, 0, 0, 3),
                MinWidth = 120,
            };
            button.Click += (_, _) => _model.SetFilter(filter);
            Tabs.Children.Add(button);
        }
    }

    // MARK: Sidebar

    private void Sidebar_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SidebarItem item) return;
        var (kind, id) = (item.Id[..item.Id.IndexOf(':')], item.Id[(item.Id.IndexOf(':') + 1)..]);
        SearchBox.Text = "";
        switch (kind)
        {
            case "folder": _model.SetFolder(id); break;
            case "label": _model.SelectLabel(id); break;
            case "account": _model.SetAccount(id); break;
        }
    }

    private void Compose_Click(object sender, RoutedEventArgs e) => StartCompose();

    private void StartCompose()
    {
        if (_model.Accounts.Count == 0) { _ = ShowAccountDialog(firstRun: false); return; }
        if (_model.Compose is null) _model.StartCompose();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _model.LoadCurrentAsync(force: true);
    private async void Older_Click(object sender, RoutedEventArgs e) => await _model.LoadOlderAsync();
    private async void AddAccount_Click(object sender, RoutedEventArgs e) => await ShowAccountDialog(firstRun: false);
    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowSettings();

    // MARK: Search

    private async void Search_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        await _model.SearchAsync(args.QueryText);

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && sender.Text.Length == 0) _model.ClearSearch();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _model.ClearSearch();
    }

    // MARK: The list

    private void MailList_PointerEntered(object sender, PointerRoutedEventArgs e) => _list.Frozen = true;
    private void MailList_PointerExited(object sender, PointerRoutedEventArgs e) => _list.Frozen = false;

    private async void MailList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MessageItem item) return;
        if (item.Email.Folder != "drafts") { _model.Open(item.Email); return; }
        try
        {
            if (!item.Email.BodyLoaded) await _model.LoadFullAsync(item.Email);
            _model.EditDraft(item.Email);
        }
        catch (Exception ex) { ShowToast(new ToastMessage($"Could not open the draft: {ex.Message}")); }
    }

    private static MessageItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as MessageItem;

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e) { if (ItemOf(sender) is { } i) i.IsHovered = true; }
    private void Row_PointerExited(object sender, PointerRoutedEventArgs e) { if (ItemOf(sender) is { } i) i.IsHovered = false; }

    private void Tile_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) _model.PrimaryAction(i.Email); }
    private void Star_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) _model.ToggleStar(i.Email); }
    private void RowArchive_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) _model.Archive(i.Email); }
    private void RowDelete_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) _model.Delete(i.Email); }
    private void RowRead_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) _model.ToggleRead(i.Email); }
    private void RowSnooze_Click(object sender, RoutedEventArgs e) { if (ItemOf(sender) is { } i) SnoozeMenu(i.Email).ShowAt((FrameworkElement)sender); }

    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        MessageMenu(item.Email, includeTriage: true).ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        e.Handled = true;
    }

    /// The row a key acts on: the hovered one, else the keyboard's current one.
    private Email? KeyTarget() =>
        _model.OpenMessage ?? _list.MessageRows.FirstOrDefault(r => r.IsHovered)?.Email ?? (MailList.SelectedItem as MessageItem)?.Email;

    // MARK: Menus

    private MenuFlyout SnoozeMenu(Email email)
    {
        var menu = new MenuFlyout();
        foreach (var item in SnoozeItems(email)) menu.Items.Add(item);
        return menu;
    }

    private List<MenuFlyoutItem> SnoozeItems(Email email) =>
        MailController.SnoozePresets(DateTimeOffset.Now).Select(preset =>
        {
            var item = new MenuFlyoutItem { Text = $"{preset.Label} · {preset.When.LocalDateTime:ddd HH:mm}" };
            item.Click += (_, _) => _model.Snooze(email, preset.When, preset.Label.ToLowerInvariant());
            return item;
        }).ToList();

    private MenuFlyout MessageMenu(Email email, bool includeTriage)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, IList<MenuFlyoutItemBase>? into = null)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            (into ?? menu.Items).Add(item);
        }
        if (includeTriage)
        {
            Add("Archive", "", () => _model.Archive(email));
            Add("Delete", "", () => _model.Delete(email));
            Add(email.Unread ? "Mark as read" : "Mark as unread", email.Unread ? "" : "", () => _model.ToggleRead(email));
        }
        Add(email.Starred ? "Remove star" : "Star", "", () => _model.ToggleStar(email));
        Add(_model.IsPinned(email) ? "Unpin" : "Pin to top", "", () => _model.TogglePin(email));

        var snooze = new MenuFlyoutSubItem { Text = "Snooze", Icon = new FontIcon { Glyph = "" } };
        foreach (var item in SnoozeItems(email)) snooze.Items.Add(item);
        menu.Items.Add(snooze);

        var labels = new MenuFlyoutSubItem { Text = "Label", Icon = new FontIcon { Glyph = "" } };
        foreach (var label in _model.Labels.OrderBy(l => l.Name))
        {
            var toggle = new ToggleMenuFlyoutItem { Text = label.Name, IsChecked = email.Labels.Contains(label.Id) };
            toggle.Click += (_, _) => _model.ApplyLabel(email, label.Id, toggle.IsChecked);
            labels.Items.Add(toggle);
        }
        if (labels.Items.Count > 0) labels.Items.Add(new MenuFlyoutSeparator());
        Add("New label…", "", async () =>
        {
            if (await Prompt("New label", "Label name") is { Length: > 0 } name) _model.ApplyLabel(email, _model.AddLabel(name), true);
        }, labels.Items);
        menu.Items.Add(labels);

        var move = new MenuFlyoutSubItem { Text = "Move to", Icon = new FontIcon { Glyph = "" } };
        foreach (var box in _model.MailboxNames(email.Account))
        {
            var item = new MenuFlyoutItem { Text = box };
            item.Click += (_, _) => Guard(() => _model.MoveToMailbox(email, box));
            move.Items.Add(item);
        }
        if (move.Items.Count > 0) menu.Items.Add(move);

        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Report spam", "", () => _model.MarkSpam(email));
        if (email.Unsubscribe is { } unsubscribe && UnsubscribeLink(unsubscribe) is { } link)
            Add("Unsubscribe", "", () => _ = Launcher.LaunchUriAsync(link));
        return menu;
    }

    /// The first link of a List-Unsubscribe header (https preferred, else mailto).
    private static Uri? UnsubscribeLink(string header)
    {
        var links = header.Split(',').Select(s => s.Trim().Trim('<', '>')).ToList();
        var pick = links.FirstOrDefault(l => l.StartsWith("https:")) ?? links.FirstOrDefault(l => l.StartsWith("mailto:"));
        return pick is not null && Uri.TryCreate(pick, UriKind.Absolute, out var uri) ? uri : null;
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (InvalidOperationException ex) { ShowToast(new ToastMessage(ex.Message)); }
    }

    // MARK: Reader

    private async Task InitReader()
    {
        ReaderWeb.DefaultBackgroundColor = Colors.White;
        await ReaderWeb.EnsureCoreWebView2Async();
        var core = ReaderWeb.CoreWebView2;
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Links open in the browser, never inside the reader.
        core.NavigationStarting += (s, a) =>
        {
            if (a.Uri.StartsWith("data:") || a.Uri.StartsWith("about:")) return;
            a.Cancel = true;
            if (Uri.TryCreate(a.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                _ = Launcher.LaunchUriAsync(uri);
        };
        core.NewWindowRequested += (s, a) =>
        {
            a.Handled = true;
            if (Uri.TryCreate(a.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                _ = Launcher.LaunchUriAsync(uri);
        };
    }

    private void OnOpenMessageChanged()
    {
        if (_shown is not null) _shown.PropertyChanged -= OnShownChanged;
        _shown = _model.OpenMessage;
        _showImagesOnce = false;
        if (_shown is null)
        {
            ReaderPane.Visibility = Visibility.Collapsed;
            ListPane.Visibility = Visibility.Visible;
            MailList.Focus(FocusState.Programmatic);
            return;
        }
        _shown.PropertyChanged += OnShownChanged;
        ListPane.Visibility = Visibility.Collapsed;
        ReaderPane.Visibility = Visibility.Visible;
        RenderReader();
    }

    private void OnShownChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(RenderReader);

    private void RenderReader()
    {
        if (_shown is not { } e) return;
        var p = Palette.Current;
        ReaderSubject.Text = e.Subject;
        ReaderFrom.Text = e.SenderName;
        ReaderFromEmail.Text = e.FromEmail.Length > 0 ? $"  <{e.FromEmail}>" : "";
        ReaderTo.Text = e.To.Count > 0 ? $"to {string.Join(", ", e.To)}" + (e.Cc.Count > 0 ? $", cc {string.Join(", ", e.Cc)}" : "") : "";
        ReaderDate.Text = e.Date.LocalDateTime.ToString("f");
        ReaderAvatar.Fill = Palette.Brush(Palette.Hex(Sender.StableColorHex(e.FromEmail.Length > 0 ? e.FromEmail : e.SenderName)));
        ReaderInitials.Text = e.SenderInitials;
        ReaderStarIcon.Glyph = e.Starred ? "" : "";
        ReaderStarIcon.Foreground = Palette.Brush(e.Starred ? p.Star : p.Fg2);

        ReaderLabels.ItemsSource = e.Labels.Select(id =>
        {
            var label = _model.Labels.FirstOrDefault(l => l.Id == id);
            var color = Palette.Hex(label?.ColorHex ?? "7D7C76");
            return new Border
            {
                Background = Palette.Brush(Palette.WithAlpha(color, 0.15)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock { Text = label?.Name ?? id, FontSize = 12, Foreground = Palette.Brush(color) },
            };
        }).ToList();

        ReaderLoading.IsActive = !e.BodyLoaded;
        string html;
        if (e.BodyHtml is { } bodyHtml)
        {
            var remote = MimeTools.CountRemoteImages(bodyHtml);
            var allow = _showImagesOnce || _model.ImagesTrusted(e);
            ImagesBar.Visibility = remote > 0 && !allow ? Visibility.Visible : Visibility.Collapsed;
            ImagesText.Text = $"Remote images blocked ({remote}) to protect your privacy.";
            html = MimeTools.SafeHtml(bodyHtml, allow);
        }
        else
        {
            ImagesBar.Visibility = Visibility.Collapsed;
            html = MimeTools.TextAsHtml(e.BodyLoaded ? e.Body : e.Preview);
        }
        if (ReaderWeb.CoreWebView2 is not null) ReaderWeb.NavigateToString(html);

        if (e.CalendarEvent is { } ev)
        {
            InviteBar.Visibility = Visibility.Visible;
            InviteTitle.Text = $"Invitation: {ev.Summary}";
            InviteWhen.Text = string.Join(" · ", new[] { ev.Start?.LocalDateTime.ToString("f"), ev.Location }.Where(s => !string.IsNullOrEmpty(s)));
        }
        else InviteBar.Visibility = Visibility.Collapsed;

        ReaderAttachments.ItemsSource = e.Attachments.Select(a =>
        {
            var button = new Button
            {
                CornerRadius = new CornerRadius(8),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        new FontIcon { Glyph = "", FontSize = 13 },
                        new TextBlock { Text = a.FileName },
                        new TextBlock { Text = FormatSize(a.Size), Foreground = Palette.Brush(p.Fg3), FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            button.Click += (_, _) => AttachmentMenu(e, a).ShowAt(button);
            return button;
        }).ToList();
    }

    private MenuFlyout AttachmentMenu(Email email, AttachmentMeta meta)
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open", Icon = new FontIcon { Glyph = "" } };
        open.Click += async (_, _) =>
        {
            try
            {
                var (_, path) = await _model.AttachmentFileAsync(email, meta.FileName, null);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { ShowToast(new ToastMessage($"Could not open it: {ex.Message}")); }
        };
        var save = new MenuFlyoutItem { Text = "Save to Downloads", Icon = new FontIcon { Glyph = "" } };
        save.Click += async (_, _) =>
        {
            try
            {
                var (_, path) = await _model.AttachmentFileAsync(email, meta.FileName, null);
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                var dest = Path.Combine(downloads, meta.FileName);
                for (var n = 2; File.Exists(dest); n++)
                    dest = Path.Combine(downloads, $"{Path.GetFileNameWithoutExtension(meta.FileName)} ({n}){Path.GetExtension(meta.FileName)}");
                File.Copy(path, dest);
                ShowToast(new ToastMessage($"Saved to Downloads · {Path.GetFileName(dest)}", "Show", () =>
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dest}\""))));
            }
            catch (Exception ex) { ShowToast(new ToastMessage($"Could not save it: {ex.Message}")); }
        };
        menu.Items.Add(open);
        menu.Items.Add(save);
        return menu;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB" : bytes >= 1024 ? $"{bytes / 1024} KB" : $"{bytes} B";

    private void Back_Click(object sender, RoutedEventArgs e) => _model.CloseReader();
    private void ReaderArchive_Click(object sender, RoutedEventArgs e) { if (_shown is { } m) _model.Archive(m); }
    private void ReaderDelete_Click(object sender, RoutedEventArgs e) { if (_shown is { } m) _model.Delete(m); }
    private void ReaderSpam_Click(object sender, RoutedEventArgs e) { if (_shown is { } m) _model.MarkSpam(m); }
    private void ReaderStar_Click(object sender, RoutedEventArgs e) { if (_shown is { } m) _model.ToggleStar(m); }

    private void ReaderUnread_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is not { } m) return;
        _model.CloseReader();
        _model.SetRead(m, false);
    }

    private void ReaderSnooze_Click(object sender, RoutedEventArgs e) { if (_shown is { } m) SnoozeMenu(m).ShowAt(ReaderSnoozeButton); }
    private void ReaderMore_Click(object sender, RoutedEventArgs e) { if (_shown is { } m) MessageMenu(m, includeTriage: false).ShowAt(ReaderMoreButton); }
    private void Reply_Click(object sender, RoutedEventArgs e) { if (_shown is { } m) _model.Reply(m, all: false); }
    private void ReplyAll_Click(object sender, RoutedEventArgs e) { if (_shown is { } m) _model.Reply(m, all: true); }
    private async void Forward_Click(object sender, RoutedEventArgs e) { if (_shown is { } m) await _model.ForwardAsync(m); }

    private void ShowImages_Click(object sender, RoutedEventArgs e)
    {
        _showImagesOnce = true;
        RenderReader();
    }

    private void TrustImages_Click(object sender, RoutedEventArgs e) { if (_shown is { } m) _model.TrustImages(m); }

    // MARK: Composer

    private void OnComposeChanged()
    {
        var draft = _model.Compose;
        ComposePane.DataContext = draft;
        ComposePane.Visibility = draft is null ? Visibility.Collapsed : Visibility.Visible;
        if (draft is null) return;
        FromBox.ItemsSource = _model.Accounts.Select(a => new AccountItem(a)).ToList();
        FromBox.SelectedValue = draft.FromId;
        var cc = draft.Cc.Length > 0 || draft.Bcc.Length > 0;
        CcBox.Visibility = BccBox.Visibility = cc ? Visibility.Visible : Visibility.Collapsed;
        ComposeBody.Focus(FocusState.Programmatic);
        ComposeBody.SelectionStart = 0;
    }

    private void CcToggle_Click(object sender, RoutedEventArgs e) =>
        CcBox.Visibility = BccBox.Visibility = CcBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void Send_Click(object sender, RoutedEventArgs e) => SendCompose();

    private void SendCompose()
    {
        if (_model.Compose is { } draft) Guard(() => _model.SendDraft(draft));
    }

    private void ComposeClose_Click(object sender, RoutedEventArgs e) { if (_model.Compose is { } d) _model.SaveDraftAndClose(d); }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Compose is not { } draft) return;
        _model.CloseCompose();
        ShowToast(new ToastMessage("Draft discarded", "Undo", () => _model.SetCompose(draft)));
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (_model.Compose is not { } draft) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
        {
            var data = await File.ReadAllBytesAsync(file.Path);
            draft.Attachments.Add(new ComposeAttachment(file.Name, MimeTools.GuessMime(file.Name), data));
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ComposeAttachment a) _model.Compose?.Attachments.Remove(a);
    }

    // MARK: Toast

    private void ShowToast(ToastMessage toast)
    {
        _toast = toast;
        ToastText.Text = toast.Text;
        ToastAction.Content = toast.ActionLabel;
        ToastAction.Visibility = toast.ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Interval = toast.Duration ?? TimeSpan.FromSeconds(4);
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
        _toast = null;
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toast?.Action;
        HideToast();
        action?.Invoke();
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();

    // MARK: Keyboard (Gmail keys)

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.Enter && _model.Compose is not null) { SendCompose(); e.Handled = true; return; }

        var focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        var typing = focused is TextBox or PasswordBox or AutoSuggestBox or ComboBox or RichEditBox;
        if (e.Key == VirtualKey.Escape)
        {
            if (_model.OpenMessage is not null) _model.CloseReader();
            else if (_model.SearchActive) { SearchBox.Text = ""; _model.ClearSearch(); }
            e.Handled = true;
            return;
        }
        if (typing || ctrl) return;

        var target = KeyTarget();
        switch (e.Key)
        {
            case VirtualKey.C: StartCompose(); break;
            case (VirtualKey)191: SearchBox.Focus(FocusState.Keyboard); break;   // '/'
            case VirtualKey.Number3 when shift: if (target is not null) _model.Delete(target); break;   // '#'
            case VirtualKey.Delete: if (target is not null) _model.Delete(target); break;
            case VirtualKey.E: if (target is not null) _model.Archive(target); break;
            case VirtualKey.U: if (target is not null) _model.PrimaryAction(target); break;
            case VirtualKey.S: if (target is not null) _model.ToggleStar(target); break;
            case VirtualKey.Z:
                if (target is not null) _model.Snooze(target, MailController.SnoozePresets(DateTimeOffset.Now).First(p => p.Label == "Tomorrow").When, "until tomorrow");
                break;
            case VirtualKey.R: if (target is not null) _model.Reply(target, all: false); break;
            case VirtualKey.A: if (target is not null) _model.Reply(target, all: true); break;
            case VirtualKey.F: if (target is not null) _ = _model.ForwardAsync(target); break;
            case VirtualKey.J or VirtualKey.Down: MoveSelection(+1); break;
            case VirtualKey.K or VirtualKey.Up: MoveSelection(-1); break;
            case VirtualKey.Enter: if ((MailList.SelectedItem as MessageItem)?.Email is { } m) _model.Open(m); break;
            default: return;
        }
        e.Handled = true;
    }

    private void MoveSelection(int delta)
    {
        if (_model.OpenMessage is not null) return;
        var rows = _list.Rows;
        var i = MailList.SelectedIndex;
        do { i += delta; } while (i >= 0 && i < rows.Count && rows[i] is not MessageItem);
        if (i < 0 || i >= rows.Count) return;
        MailList.SelectedIndex = i;
        MailList.ScrollIntoView(rows[i]);
    }

    // MARK: Dialogs

    private async Task<string?> Prompt(string title, string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder };
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = title, Content = box,
            PrimaryButtonText = "OK", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    private Task ShowAccountDialog(bool firstRun) => AccountDialog.ShowAsync(Root.XamlRoot, _model, firstRun, () => Activate());

    private Task ShowSettings() => SettingsDialog.ShowAsync(Root.XamlRoot, _model, _store, theme =>
    {
        _store.SetPref("theme", theme);
        ApplyTheme(theme);
    });
}
