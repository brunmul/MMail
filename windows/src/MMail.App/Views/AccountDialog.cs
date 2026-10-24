using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MMail.Core.Mail;
using MMail.Core.Models;
using MMail.Core.State;

namespace MMail.App;

/// Add an account: "Sign in with Google" in the browser (the default), or, under
/// "Other options", a Gmail app password or any IMAP/SMTP server.
public static class AccountDialog
{
    public static async Task ShowAsync(XamlRoot root, MailController model, bool firstRun, Action? bringToFront = null)
    {
        var googleReady = GoogleClient.Default is not null;
        CancellationTokenSource? signIn = null;

        // MARK: Sign in with Google

        var google = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, Height = 46, CornerRadius = new CornerRadius(23),
            Background = Palette.Brush(Palette.Current.Panel), BorderBrush = Palette.Brush(Palette.Current.Border),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new Image
                    {
                        Width = 20, Height = 20,
                        Source = new SvgImageSource(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Google.svg"))),
                    },
                    new TextBlock { Text = "Se connecter avec Google", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        var cancelSignIn = new HyperlinkButton { Content = "Annuler", Padding = new Thickness(0) };
        var waiting = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new ProgressRing { IsActive = true, Width = 16, Height = 16 },
                new TextBlock { Text = "Termine la connexion dans ton navigateur…", VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Brush(Palette.Current.Fg2) },
                cancelSignIn,
            },
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Current.Danger) };

        // MARK: Other options (app password, IMAP/SMTP)

        var provider = new ComboBox { Header = "Messagerie", ItemsSource = new[] { "Gmail (mot de passe d'application)", "Autre (IMAP / SMTP)" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Header = "Ton nom", PlaceholderText = "Bruno Muller" };
        var email = new TextBox { Header = "Adresse e-mail", PlaceholderText = "toi@gmail.com" };
        var password = new PasswordBox { Header = "Mot de passe d'application" };
        var appPasswordLink = new HyperlinkButton
        {
            Content = "Créer un mot de passe d'application Gmail",
            NavigateUri = new Uri("https://myaccount.google.com/apppasswords"), Padding = new Thickness(0),
        };
        var imapHost = new TextBox { Header = "Serveur IMAP", PlaceholderText = "imap.example.com" };
        var imapPort = new NumberBox { Header = "Port", Value = 993, Minimum = 1, Maximum = 65535 };
        var imapSecurity = Security();
        var smtpHost = new TextBox { Header = "Serveur SMTP", PlaceholderText = "smtp.example.com" };
        var smtpPort = new NumberBox { Header = "Port", Value = 465, Minimum = 1, Maximum = 65535 };
        var smtpSecurity = Security();
        var username = new TextBox { Header = "Identifiant (si différent de l'adresse)" };
        var servers = new StackPanel
        {
            Spacing = 8, Visibility = Visibility.Collapsed,
            Children = { Row(imapHost, imapPort, imapSecurity), Row(smtpHost, smtpPort, smtpSecurity), username },
        };
        var other = new StackPanel
        {
            Spacing = 10, Visibility = googleReady ? Visibility.Collapsed : Visibility.Visible,
            Children = { provider, name, email, password, appPasswordLink, servers },
        };
        var otherToggle = new HyperlinkButton
        {
            Content = "Autre messagerie, ou mot de passe d'application", Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center, Visibility = googleReady ? Visibility.Visible : Visibility.Collapsed,
        };
        provider.SelectionChanged += (_, _) =>
        {
            var gmail = provider.SelectedIndex == 0;
            servers.Visibility = gmail ? Visibility.Collapsed : Visibility.Visible;
            appPasswordLink.Visibility = gmail ? Visibility.Visible : Visibility.Collapsed;
            password.Header = gmail ? "Mot de passe d'application" : "Mot de passe";
        };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = firstRun ? "Bienvenue dans MMail" : "Ajouter un compte",
            PrimaryButtonText = googleReady ? "" : "Connecter",
            CloseButtonText = firstRun ? "Plus tard" : "Annuler",
            DefaultButton = googleReady ? ContentDialogButton.None : ContentDialogButton.Primary,
            Content = new ScrollViewer
            {
                MaxHeight = 600,
                Content = new StackPanel
                {
                    Spacing = 14, Width = 420,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = googleReady
                                ? "Connecte ta boîte Gmail en un clic. Tes mails restent sur ce PC : MMail parle directement à Google, sans serveur intermédiaire."
                                : "Tes mails restent sur ce PC : MMail parle directement à ta messagerie, et les mots de passe vont dans le Gestionnaire d'identification de Windows.",
                            TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Current.Fg2),
                        },
                        googleReady ? (UIElement)google : new Border(),
                        waiting, error, otherToggle, other,
                    },
                },
            },
        };

        otherToggle.Click += (_, _) =>
        {
            var open = other.Visibility == Visibility.Collapsed;
            other.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            dialog.PrimaryButtonText = open ? "Connecter" : "";
            dialog.DefaultButton = open ? ContentDialogButton.Primary : ContentDialogButton.None;
        };

        google.Click += async (_, _) =>
        {
            error.Text = "";
            var cts = signIn = new CancellationTokenSource();
            google.IsEnabled = false;
            waiting.Visibility = Visibility.Visible;
            try
            {
                await model.AddGoogleAccountAsync(uri => _ = Windows.System.Launcher.LaunchUriAsync(uri), cts.Token);
                bringToFront?.Invoke();
                dialog.Hide();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Cancelled from MMail: nothing to say.
            }
            catch (Exception ex)
            {
                bringToFront?.Invoke();
                error.Text = ex switch
                {
                    OperationCanceledException => "Connexion annulée dans le navigateur.",
                    HttpRequestException => "Impossible de joindre Google. Vérifie ta connexion internet.",
                    MailKit.Security.AuthenticationException => "Google a refusé l'accès à la boîte mail. Réessaie, et coche bien l'accès à Gmail.",
                    _ => ex.Message,
                };
            }
            finally
            {
                signIn = null;
                cts.Dispose();
                google.IsEnabled = true;
                waiting.Visibility = Visibility.Collapsed;
            }
        };
        cancelSignIn.Click += (_, _) => signIn?.Cancel();
        dialog.Closing += (_, _) => signIn?.Cancel();

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            error.Text = "";
            try
            {
                var address = email.Text.Trim();
                if (!address.Contains('@')) throw new InvalidOperationException("Indique ton adresse e-mail.");
                if (password.Password.Length == 0) throw new InvalidOperationException("Indique le mot de passe.");
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
                    if (cfg.ImapHost.Length == 0 || cfg.SmtpHost.Length == 0) throw new InvalidOperationException("Indique les serveurs IMAP et SMTP.");
                }
                dialog.IsPrimaryButtonEnabled = false;
                dialog.PrimaryButtonText = "Connexion…";
                // Gmail app passwords are shown in groups of four; spaces are not part of it.
                var secret = provider.SelectedIndex == 0 ? password.Password.Replace(" ", "") : password.Password;
                await model.AddAccountAsync(cfg, secret);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message.Contains("AUTHENTICATIONFAILED", StringComparison.OrdinalIgnoreCase) || ex is MailKit.Security.AuthenticationException
                    ? provider.SelectedIndex == 0
                        ? "Gmail a refusé ce mot de passe. Il faut un mot de passe d'application (16 lettres), pas ton mot de passe habituel."
                        : "Le serveur a refusé l'identifiant ou le mot de passe."
                    : ex.Message;
                args.Cancel = true;
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = true;
                dialog.PrimaryButtonText = "Connecter";
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }

    private static ComboBox Security() => new()
    {
        Header = "Sécurité", ItemsSource = new[] { "SSL / TLS", "STARTTLS", "Aucune" }, SelectedIndex = 0, MinWidth = 120,
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
