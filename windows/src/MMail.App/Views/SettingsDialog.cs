using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MMail.Core.Agent;
using MMail.Core.State;
using MMail.Core.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace MMail.App;

/// Appearance, accounts and signatures, and connecting Claude.
public static class SettingsDialog
{
    public static async Task ShowAsync(XamlRoot root, MailController model, MailStore store, Action<string> setTheme)
    {
        var p = Palette.Current;
        var panel = new StackPanel { Spacing = 12, Width = 520 };
        TextBlock Title(string text) => new() { Text = text, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
        TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Palette.Brush(p.Fg3) };

        // Appearance
        panel.Children.Add(Title("Appearance"));
        var themes = new[] { ("system", "Same as Windows"), ("light", "Light"), ("dark", "Dark") };
        var current = store.GetPref<string>("theme") ?? "system";
        var theme = new RadioButtons { MaxColumns = 3 };
        foreach (var (_, label) in themes) theme.Items.Add(label);
        theme.SelectedIndex = Array.FindIndex(themes, t => t.Item1 == current);
        theme.SelectionChanged += (_, _) => { if (theme.SelectedIndex >= 0) setTheme(themes[theme.SelectedIndex].Item1); };
        panel.Children.Add(theme);

        // Accounts and signatures
        panel.Children.Add(Title("Accounts"));
        var signatures = new Dictionary<string, TextBox>();
        if (model.Accounts.Count == 0) panel.Children.Add(Note("No account yet."));
        foreach (var account in model.Accounts.ToList())
        {
            var header = new Grid();
            header.Children.Add(new TextBlock { Text = $"{account.DisplayName} <{account.Email}>", VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = "Remove", HorizontalAlignment = HorizontalAlignment.Right };
            header.Children.Add(remove);
            var signature = new TextBox
            {
                Header = "Signature", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64,
                Text = model.Signature(account.Id),
            };
            signatures[account.Id] = signature;
            var block = new StackPanel { Spacing = 6, Children = { header, signature } };
            remove.Click += (_, _) =>
            {
                model.RemoveAccount(account.Id);
                signatures.Remove(account.Id);
                panel.Children.Remove(block);
            };
            panel.Children.Add(block);
        }

        // Claude
        panel.Children.Add(Title("Claude"));
        var bridge = Path.Combine(AppContext.BaseDirectory, "mmail-mcp.exe");
        panel.Children.Add(Note("Claude drives MMail through a local MCP server: it reads, sorts and drafts mail in this window, " +
                                "where you see every action. Nothing goes through the internet besides your mail provider."));
        var status = Note("");
        var connect = new Button { Content = "Connect Claude Desktop", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        connect.Click += (_, _) =>
        {
            var paths = ClaudeSetup.DesktopConfigPaths();
            var existing = paths.Where(File.Exists).ToList();
            List<string> targets = existing.Count > 0 ? existing : [paths[0]];
            var done = targets.Where(t => ClaudeSetup.AddToConfig(t, bridge)).ToList();
            status.Text = done.Count > 0
                ? "Done. Restart Claude Desktop, then ask Claude about your mail."
                : $"Could not update {targets[0]} (it is not valid JSON). Add the snippet below by hand.";
        };
        var copyCode = new Button { Content = "Copy the Claude Code command" };
        copyCode.Click += (_, _) => Copy(ClaudeSetup.ClaudeCodeCommand(bridge), status, "Copied. Paste it in a terminal.");
        var copyJson = new Button { Content = "Copy the JSON" };
        copyJson.Click += (_, _) => Copy(ClaudeSetup.ConfigSnippet(bridge), status, "Copied.");
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { connect, copyCode, copyJson } });
        panel.Children.Add(status);
        panel.Children.Add(Note($"Server: {bridge}"));
        panel.Children.Add(Note($"Data: {AppPaths.Home}"));

        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = "Settings", CloseButtonText = "Done", DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer { Content = panel, MaxHeight = 620 },
        };
        await dialog.ShowAsync();
        foreach (var (id, box) in signatures) model.SetSignature(id, box.Text.TrimEnd());
    }

    private static void Copy(string text, TextBlock status, string message)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        status.Text = message;
    }
}
