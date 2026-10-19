using Microsoft.UI.Xaml.Controls;

namespace MiniInk;

// Picks what the mouse and touch do on every open note; the choice is remembered.
public sealed partial class InputToolsPanel : UserControl
{
    private bool _showing;

    public InputToolsPanel()
    {
        InitializeComponent();
        // Reloaded each time its flyout opens, so a change made in another window shows here.
        Loaded += (_, _) =>
        {
            _showing = true;
            MouseChoices.SelectedIndex = (int)App.Current.MouseTool;
            TouchChoices.SelectedIndex = (int)App.Current.TouchTool;
            _showing = false;
        };
    }

    private void Choices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showing || sender is not RadioButtons { SelectedIndex: >= 0 } choices)
            return;
        if (choices == MouseChoices)
            App.Current.MouseTool = (InputTool)choices.SelectedIndex;
        else
            App.Current.TouchTool = (InputTool)choices.SelectedIndex;
    }
}
