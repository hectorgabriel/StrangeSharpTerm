using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace StrangeSharpTerm.App.Views;

/// <summary>The settings both editors share. See SettingsEditor.axaml.</summary>
public partial class SettingsEditor : UserControl
{
    public SettingsEditor()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
