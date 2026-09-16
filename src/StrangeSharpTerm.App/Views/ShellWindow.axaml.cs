using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StrangeSharpTerm.App.ViewModels;

namespace StrangeSharpTerm.App.Views;

public partial class ShellWindow : Window
{
    /// <summary>
    /// How far the sidebar's header keeps clear of the traffic lights.
    ///
    /// macOS only: there the title bar is extended into and the window controls
    /// float over our own chrome, as the Swift app has it. Windows keeps its
    /// system title bar, so nothing is in the way. See docs/adr/0005.
    /// </summary>
    public static Thickness HeaderMargin { get; } =
        OperatingSystem.IsMacOS() ? new Thickness(14, 34, 10, 8) : new Thickness(14, 12, 10, 8);

    /// <summary>
    /// The same clearance for the dock's own header.
    ///
    /// It is against the right edge, where macOS puts nothing, so only the
    /// height of the extended title bar applies — but it does apply, or the
    /// first row of the dock sits level with the traffic lights on the far side
    /// and reads as part of the window chrome.
    /// </summary>
    public static Thickness DockHeaderMargin { get; } =
        OperatingSystem.IsMacOS() ? new Thickness(10, 34, 10, 8) : new Thickness(10, 12, 10, 8);

    /// <summary>
    /// How wide the dock was when it was last open, so closing and reopening it
    /// does not forget a width someone dragged.
    /// </summary>
    private double _dockWidth = 380;

    public ShellWindow() : this(new ShellViewModel(new InventoryViewModel()))
    {
    }

    public ShellWindow(ShellViewModel model)
    {
        InitializeComponent();
        DataContext = model;

        if (OperatingSystem.IsMacOS())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = -1;
        }
    }

    private ShellViewModel? Model => DataContext as ShellViewModel;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Model is not { } model)
            return;

        // The command modifier is the platform's, and only a window can be asked
        // what the platform is, which is why this is not in the view model.
        model.Describe(AppMenu.CommandModifier(this));
        AppMenu.Bind(this, model.Commands);

        var menu = AppMenu.Build(model.Commands);
        NativeMenu.SetMenu(this, menu);
        if (Application.Current is { } application)
            NativeMenu.SetMenu(application, menu);

        // A hidden control collapses an Auto column on its own; a pixel column
        // has to be told, or a closed dock leaves 380 points of background
        // beside the sessions. It is pixels rather than Auto because only a
        // sized column can be dragged.
        ShowDock(model.IsDockOpen);
        model.PropertyChanged += (_, changed) =>
        {
            if (changed.PropertyName == nameof(ShellViewModel.IsDockOpen))
                ShowDock(model.IsDockOpen);
        };

        // The field takes the keyboard each time the palette opens.
        //
        // Not when it is attached, which is what this did first: the palette is
        // hidden rather than absent, so attaching happens once, while it is not
        // on screen, and never again. Typing then went wherever the focus
        // already was — and a palette you have to click into is not one.
        model.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ViewModels.PaletteViewModel.IsOpen) || !model.Palette.IsOpen)
                return;
            Dispatcher.UIThread.Post(
                () => this.FindControl<TextBox>("PaletteQuery")?.Focus(),
                DispatcherPriority.Input);
        };
    }

    /// <summary>Gives the dock its column back, or takes it away.</summary>
    private void ShowDock(bool open)
    {
        if (this.FindControl<Grid>("Columns")?.ColumnDefinitions is not { Count: 4 } columns)
            return;

        var dock = columns[3];
        if (!open)
        {
            // Remembered before it goes, so a width someone dragged survives
            // closing the dock and opening it again.
            if (dock.Width.IsAbsolute && dock.Width.Value > 0)
                _dockWidth = dock.Width.Value;
            dock.MinWidth = 0;
            dock.Width = new GridLength(0);
            return;
        }

        dock.MinWidth = 300;
        dock.Width = new GridLength(_dockWidth);
    }

    /// <summary>
    /// Escape closes the palette, and the arrows and Enter drive it.
    ///
    /// Handled here rather than on the list, because the text box has the
    /// keyboard while the palette is open and would otherwise swallow them.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Model?.Palette is not { IsOpen: true } palette)
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                palette.Close();
                e.Handled = true;
                break;
            case Key.Down:
                palette.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                palette.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                palette.RunSelected();
                e.Handled = true;
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }
}
