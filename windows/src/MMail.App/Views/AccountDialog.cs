using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MMail.Core.Models;
using MMail.Core.State;

namespace MMail.App;

/// Add an account: Gmail with an app password (the default), or any IMAP/SMTP server.
public static class AccountDialog
{
    public static async Task ShowAsync(XamlRoot root, MailController model, bool firstRun)
    {
        var provider = new ComboBox { Header = "Provider", ItemsSource = new[] { "Gmail", "Other (IMAP / SMTP)" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "Your name", PlaceholderText = "Bruno Muller" };
        var email = new TextBox { Header = "Email address", PlaceholderText = "you@gmail.com" };
        var password = new PasswordBox { Header = "App password" };
        var appPasswordLink = new HyperlinkButton
        {
            Content = "Create a Gmail app password (two-step verification required)",
            NavigateUri = new Uri("https://myaccount.google.com/apppasswords"), Padding = new Thickness(0),
        };

        var imapHost = new TextBox { Header = "IMAP server", PlaceholderText = "imap.example.com" };
        var imapPort = new NumberBox { Header = "Port", Value = 993, Minimum = 1, Maximum = 65535 };
        var imapSecurity = Security();
        var smtpHost = new TextBox { Header = "SMTP server", PlaceholderText = "smtp.example.com" };
        var smtpPort = new NumberBox { Header = "Port", Value = 465, Minimum = 1, Maximum = 65535 };
        var smtpSecurity = Security();
        var username = new TextBox { Header = "User name (if not the address)" };
        var manual = new StackPanel
        {
            Spacing = 8, Visibility = Visibility.Collapsed,
            Children = { Row(imapHost, imapPort, imapSecurity), Row(smtpHost, smtpPort, smtpSecurity), username },
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Current.Danger) };

        provider.SelectionChanged += (_, _) =>
        {
            var gmail = provider.SelectedIndex == 0;
            manual.Visibility = gmail ? Visibility.Collapsed : Visibility.Visible;
            appPasswordLink.Visibility = gmail ? Visibility.Visible : Visibility.Collapsed;
            password.Header = gmail ? "App password" : "Password";
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = firstRun ? "Welcome to MMail" : "Add an account",
            PrimaryButtonText = "Connect", CloseButtonText = firstRun ? "Later" : "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer
            {
                MaxHeight = 560,
                Content = new StackPanel
                {
                    Spacing = 10, Width = 440,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Your mail stays on this PC: MMail talks to your provider directly, and passwords go to the Windows Credential Manager.",
                            TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Current.Fg3), FontSize = 12.5,
                        },
                        provider, name, email, password, appPasswordLink, manual, error,
                    },
                },
            },
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            error.Text = "";
            try
            {
                var address = email.Text.Trim();
                if (!address.Contains('@')) throw new InvalidOperationException("Enter your email address.");
                if (password.Password.Length == 0) throw new InvalidOperationException("Enter the password.");
                MailAccountConfig cfg;
                if (provider.SelectedIndex == 0)
                {
                    cfg = MailAccountConfig.Gmail(name.Text.Trim(), address);
                }
                else
                {
                    var user = username.Text.Trim().Length > 0 ? username.Text.Trim() : address;
                    cfg = new MailAccountConfig
                    {
                        DisplayName = name.Text.Trim(), Email = address,
                        ImapHost = imapHost.Text.Trim(), ImapPort = (int)imapPort.Value, ImapSecurity = (ConnectionSecurity)imapSecurity.SelectedIndex, ImapUsername = user,
                        SmtpHost = smtpHost.Text.Trim(), SmtpPort = (int)smtpPort.Value, SmtpSecurity = (ConnectionSecurity)smtpSecurity.SelectedIndex, SmtpUsername = user,
                    };
                    if (cfg.ImapHost.Length == 0 || cfg.SmtpHost.Length == 0) throw new InvalidOperationException("Enter the IMAP and SMTP servers.");
                }
                dialog.IsPrimaryButtonEnabled = false;
                dialog.PrimaryButtonText = "Connecting…";
                // Gmail app passwords are shown in groups of four; spaces are not part of it.
                var secret = provider.SelectedIndex == 0 ? password.Password.Replace(" ", "") : password.Password;
                await model.AddAccountAsync(cfg, secret);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message.Contains("AUTHENTICATIONFAILED", StringComparison.OrdinalIgnoreCase) || ex is MailKit.Security.AuthenticationException
                    ? "The server refused the password. For Gmail, use an app password, not your usual password."
                    : ex.Message;
                args.Cancel = true;
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = true;
                dialog.PrimaryButtonText = "Connect";
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }

    private static ComboBox Security() => new()
    {
        Header = "Security", ItemsSource = new[] { "SSL / TLS", "STARTTLS", "None" }, SelectedIndex = 0, MinWidth = 120,
    };

    private static Grid Row(FrameworkElement host, FrameworkElement port, FrameworkElement security)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(port, 1);
        Grid.SetColumn(security, 2);
        grid.Children.Add(host);
        grid.Children.Add(port);
        grid.Children.Add(security);
        return grid;
    }
}
