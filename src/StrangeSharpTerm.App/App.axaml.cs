using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StrangeSharpTerm.App.Terminal;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Terminal;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // A palette before anything is built, so no colour is ever looked up
        // and missing. The remembered one replaces it below; the XAML previewer,
        // which never gets that far, keeps this one.
        ThemeTokens.Apply(this, AppPalette.StrangeTermDark);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The theme comes back before any window is built, so nothing is
            // ever painted in one theme and then repainted in another.
            var theme = AppTheme.Load();
            theme.PaintInto(this);

            // --connect opens one host and nothing else, which is how a terminal
            // is checked against a server without the inventory in the way. With
            // no argument the app opens its own window.
            var arguments = desktop.Args ?? [];
            var request = TerminalLaunchRequest.Parse(arguments);
            desktop.MainWindow =
                Editor(arguments) ?? (request is null ? Shell(theme, arguments) : TerminalWindow(request, theme));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Window Shell(AppTheme theme, string[] arguments)
    {
        // The dialog service needs the window it will be modal to, and the window
        // needs the view model that asks for dialogs, so the reference is late.
        ShellWindow? window = null;
        var model = new ShellViewModel(
            InventoryLoader.Load(), dialogs: new DialogService(() => window), theme: theme);
        window = new ShellWindow(model);

        // --demo-palette [--demo-query x], as the Swift app had them: the palette
        // is opened with ⌘K and by nothing else, and a shortcut is the one thing
        // the DevTools MCP cannot send. Posted rather than called, because the
        // window builds its command list as it opens.
        if (arguments.Contains("--demo-palette"))
            Dispatcher.UIThread.Post(
                () =>
                {
                    model.OpenPaletteCommand.Execute(null);
                    var index = Array.IndexOf(arguments, "--demo-query");
                    if (index >= 0 && index + 1 < arguments.Length)
                        model.Palette.Query = arguments[index + 1];
                },
                DispatcherPriority.Background);

        return window;
    }

    /// <summary>
    /// <c>--demo-editor host|folder|credential|credentials</c>: a dialog on its
    /// own, over a fixture.
    ///
    /// The editors are reached through a flyout, and a flyout cannot be opened by
    /// the synthetic input the DevTools MCP sends — which would leave the two
    /// dialogs in this app as the only windows nobody can look at. This is the
    /// same idea as the Swift app's <c>--demo-*</c> fixtures, which the migration
    /// plan keeps for exactly this reason, and it is what the screenshot checks in
    /// M4–M5 will drive.
    /// </summary>
    private static Window? Editor(string[] arguments)
    {
        var index = Array.IndexOf(arguments, "--demo-editor");
        if (index < 0 || index + 1 >= arguments.Length)
            return null;

        var folder = new Model.Folder
        {
            Name = "Production",
            Settings = new Model.ConnectionSettings { Username = "ops", Port = 2222, ForwardAgent = true },
        };
        var host = new Model.Connection
        {
            ParentId = folder.Id,
            Name = "db-primary",
            Hostname = "db-01.prod.example.com",
            Tags = ["postgres", "primary"],
            Settings = new Model.ConnectionSettings
            {
                HostKeyPolicy = Model.HostKeyPolicy.Strict,
                IdentityFiles = ["~/.ssh/id_ed25519"],
            },
        };
        var key = new Model.Credential
        {
            Name = "Ops key",
            Username = "ops",
            Method = Model.CredentialMethod.IdentityFile,
            IdentityFile = "~/.ssh/id_ed25519",
        };
        var password = new Model.Credential
        {
            Name = "Legacy password",
            Username = "admin",
            Method = Model.CredentialMethod.Password,
            SortIndex = 1,
        };
        var tree = new Model.InventoryTree(
            [folder with { Settings = folder.Settings with { CredentialId = key.Id } }],
            [host],
            credentials: [key, password]);

        // In memory, and never the real keychain: a fixture must not write to
        // the login keychain of whoever is looking at it.
        var secrets = new Security.InMemorySecretStore();
        secrets.SetSecret(password.SecretAccount, "not a real password");

        return arguments[index + 1] switch
        {
            "folder" => new FolderEditor(FolderDraft.For(tree, tree.Folders.Values.First())),
            "credential" => new CredentialEditor(CredentialDraft.For(password, secrets)),
            "credentials" => Library(tree, secrets),
            _ => new HostEditor(HostDraft.For(tree, host)),
        };
    }

    /// <summary>
    /// The credential library over a fixture, with its own dialogs live, so Add,
    /// Edit and Delete can be walked through without touching the real keychain.
    /// The reference is late for the same reason it is in <see cref="Shell"/>:
    /// the dialogs need the window they will be modal to.
    /// </summary>
    private static Window Library(Model.InventoryTree tree, Security.ISecretStore secrets)
    {
        CredentialsWindow? window = null;
        var model = new CredentialsViewModel(
            new InventoryViewModel(null, tree), secrets, new DialogService(() => window));
        return window = new CredentialsWindow(model);
    }

    private static Window TerminalWindow(TerminalLaunchRequest request, AppTheme theme)
    {
        var window = new Window { Title = $"StrangeSharpTerm — {request.Target}", Width = 900, Height = 560 };
        try
        {
            var session = TerminalLauncher.Connect(request);
            // --theme names one outright; without it the window uses whichever
            // theme the app is in, as a pane in the shell would.
            var palette = request.Theme is null
                ? theme.Palette.Terminal
                : TerminalPalette.ByName(request.Theme);
            var pane = new TerminalPaneView(session, palette, new TerminalRegistry());
            pane.TitleChanged += (_, title) => window.Title = title.Length > 0 ? title : window.Title;
            pane.Ended += (_, _) => window.Title = $"{window.Title} — disconnected";
            window.Content = pane;
        }
        catch (Exception e)
        {
            // A connection that fails has to say why in the window: there is no
            // other surface yet to say it on.
            window.Content = new TextBlock
            {
                Margin = new Thickness(24),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = SshFailure.Classify(e).Summary,
            };
        }
        return window;
    }
}