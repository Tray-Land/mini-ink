using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MiniInk;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
    }

    public async void OnShown()
    {
        _visible = true;
        App.Current.Notes.Changed -= Notes_Changed;
        App.Current.Notes.Changed += Notes_Changed;
        Refresh();
        try
        {
            await App.Current.Notes.LoadAsync();
            if (!_visible)
                return;
            Refresh();
            if (App.Current.Notes.LoadErrors.Count > 0)
                ShowError("Some notes could not be loaded. Original files were preserved.\n" +
                    string.Join("\n", App.Current.Notes.LoadErrors));
            Search.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MiniInk: loading library failed: {ex}");
            ShowError($"Could not load notes: {ex.Message}");
        }
    }
    private bool _visible;
    public void OnHidden()
    {
        _visible = false;
        App.Current.Notes.Changed -= Notes_Changed;
    }
    private void Notes_Changed(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        Note[] matches = App.Current.Notes.All.Where(n => n.Matches(Search.Text ?? "")).ToArray();
        NotesList.ItemsSource = matches;
        Empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = string.IsNullOrWhiteSpace(Search.Text) ? "No notes yet. Capture your first idea." : "No matching notes.";
    }
    public void ShowError(string message)
    {
        Error.Message = message;
        Error.IsOpen = true;
    }
    private async void NewNote_Click(object sender, RoutedEventArgs e) => await App.Current.CreateNoteAsync();
    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();
    private async void NotesList_ItemClick(object sender, ItemClickEventArgs e) => await App.Current.OpenNoteAsync((Note)e.ClickedItem);
}
