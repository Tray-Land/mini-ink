using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace MiniInk;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        NewNoteButton.Flyout = TemplateMenu.Create();
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
            return;
        }
        await DrawMissingThumbnailsAsync();
    }

    // Notes saved before thumbnails were kept get theirs drawn from their ink, once each.
    private readonly HashSet<Guid> _drawn = [];
    private async Task DrawMissingThumbnailsAsync()
    {
        List<(Guid, string, string)> thumbnails = [];
        foreach (Note note in App.Current.Notes.All.Where(n => n.Thumbnail.Length == 0 && n.StrokeCount != 0 && n.InkBase64.Length > 0))
        {
            if (_drawn.Add(note.Id))
                thumbnails.Add((note.Id, note.InkBase64, await InkThumbnail.FromInkAsync(note.InkBase64)));
        }
        try
        {
            if (thumbnails.Count > 0)
                await App.Current.Notes.AddThumbnailsAsync(thumbnails);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Drawn again the next time the library opens.
            System.Diagnostics.Debug.WriteLine($"MiniInk: saving thumbnails failed: {ex}");
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
        LibraryItem[] matches = App.Current.Notes.All.Where(n => n.Matches(Search.Text ?? ""))
            .Select(n => new LibraryItem(n, App.Current.IsOnDesktop(n.Id))).ToArray();
        NotesList.ItemsSource = matches;
        Empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = string.IsNullOrWhiteSpace(Search.Text) ? "No notes yet. Capture your first idea." : "No matching notes.";
    }
    public void ShowError(string message)
    {
        Error.Message = message;
        Error.IsOpen = true;
    }
    private async void NewNote_Click(SplitButton sender, SplitButtonClickEventArgs e) => await App.Current.CreateNoteAsync();
    private async void NewNote_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        await App.Current.CreateNoteAsync();
    }
    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();
    private async void NotesList_ItemClick(object sender, ItemClickEventArgs e) => await App.Current.OpenNoteAsync(((LibraryItem)e.ClickedItem).Note);
    private async void Delete_Click(object sender, RoutedEventArgs e) => await DeleteAsync((LibraryItem)((FrameworkElement)sender).DataContext);
    private async void NotesList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete || e.OriginalSource is not ListViewItem { Content: LibraryItem item })
            return;
        e.Handled = true;
        await DeleteAsync(item);
    }

    private bool _deleting;
    // Deleting can't be undone, so it always asks first.
    private async Task DeleteAsync(LibraryItem item)
    {
        if (_deleting)
            return;
        _deleting = true;
        try
        {
            ContentDialog confirm = new()
            {
                XamlRoot = XamlRoot,
                Title = "Delete this note?",
                Content = item.OnDesktop
                    ? $"\"{item.Title}\" will be deleted for good, and its sticky note closed."
                    : $"\"{item.Title}\" will be deleted for good.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;
            await App.Current.DeleteNoteAsync(item.Note.Id);
            Search.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not delete the note: {ex.Message}");
        }
        finally
        {
            _deleting = false;
        }
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
}

public sealed record LibraryItem(Note Note, bool OnDesktop)
{
    public string Title => Note.Title;
    public string Preview => Note.Preview;
    public string DateLabel => OnDesktop ? $"{Note.DateLabel} - on your desktop" : Note.DateLabel;
    public Geometry? Sketch => InkThumbnail.ToGeometry(Note.Thumbnail);
    public override string ToString() => Note.Title;
}
