using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace MiniInk;

// Shown in the popup in place of the library or the note; every change applies at once.
public sealed partial class SettingsPage : Page
{
    private static readonly GraphOrigin[] Origins = Enum.GetValues<GraphOrigin>();
    private static readonly NoteTemplate[] Templates = Enum.GetValues<NoteTemplate>();
    private readonly Action _back;
    private bool _loading;

    public SettingsPage(Action back)
    {
        _back = back;
        InitializeComponent();
        CultureInfo culture = CultureInfo.CurrentCulture;
        DateTimeFormatInfo format = culture.DateTimeFormat;
        FreshAfter.ItemsSource = FreshPagePolicy.Choices.Select(FreshPagePolicy.Label).ToArray();
        DefaultTemplateChoice.ItemsSource = Templates.Select(template => TemplateMenu.Label(template)).ToArray();
        DefaultPaperChoice.ItemsSource = NoteColors.All.Select(NoteColors.Label).ToArray();
        // The day runs from a starting hour to an ending one, so 24 (midnight) can only end it.
        StartHourChoice.ItemsSource = Enumerable.Range(0, 24).Select(hour => NoteTemplates.HourLabel(hour, culture)).ToArray();
        EndHourChoice.ItemsSource = Enumerable.Range(1, 24).Select(hour => NoteTemplates.HourLabel(hour, culture)).ToArray();
        NextHoursChoice.ItemsSource = Enumerable.Range(1, 24).Select(hours => hours == 1 ? "1 hour" : $"{hours} hours").ToArray();
        FirstDayChoice.ItemsSource = Enum.GetValues<DayOfWeek>().Select(format.GetDayName)
            .Prepend($"Same as Windows ({format.GetDayName(format.FirstDayOfWeek)})").ToArray();
        OriginChoice.ItemsSource = Origins.Select(origin => origin switch
        {
            GraphOrigin.Center => "Center",
            GraphOrigin.BottomLeft => "Bottom left",
            GraphOrigin.MiddleLeft => "Middle left",
            _ => "No axes"
        }).ToArray();
        NumbersChoice.ItemsSource = TemplateOptions.GraphNumberChoices.Select(every => every switch
        {
            0 => "None",
            1 => "Every square",
            _ => $"Every {every} squares"
        }).ToArray();
        PopupWidthBox.Minimum = PopupSize.MinWidth;
        PopupWidthBox.Maximum = PopupSize.MaxWidth;
        PopupHeightBox.Minimum = PopupSize.MinHeight;
        PopupHeightBox.Maximum = PopupSize.MaxHeight;
        DefaultSizeText.Text = $"{PopupSize.DefaultWidth} by {PopupSize.DefaultHeight}";
        VersionText.Text = $"MiniInk {Version}";
        // The popup can be resized by its edge while this page shows, so the boxes follow it.
        Loaded += (_, _) =>
        {
            App.Current.PopupSizeChanged -= PopupSize_Changed;
            App.Current.PopupSizeChanged += PopupSize_Changed;
            BackButton.Focus(FocusState.Programmatic);
        };
        Unloaded += (_, _) => App.Current.PopupSizeChanged -= PopupSize_Changed;
    }

    private static string Version
    {
        get
        {
            try
            {
                PackageVersion version = Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch (InvalidOperationException)
            {
                return "(unpackaged)";
            }
        }
    }

    // Read each time the page shows, since starting with Windows can also be changed in Windows Settings.
    public async void OnShown()
    {
        _loading = true;
        FreshAfter.SelectedIndex = FreshPagePolicy.Choices.ToList().IndexOf(App.Current.FreshAfterMinutes);
        DefaultTemplateChoice.SelectedIndex = Array.IndexOf(Templates, App.Current.DefaultTemplate);
        DefaultPaperChoice.SelectedIndex = Array.IndexOf(NoteColors.All, App.Current.DefaultPaper);
        ShowTemplateOptions(App.Current.TemplateOptions);
        _loading = false;
        ShowPopupSize();
        ShowStartupState(await StartupService.GetStateAsync());
    }

    private void ShowPopupSize()
    {
        _loading = true;
        PopupWidthBox.Value = App.Current.PopupWidth;
        PopupHeightBox.Value = App.Current.PopupHeight;
        _loading = false;
    }

    private void PopupSize_Changed(object? sender, EventArgs e) => ShowPopupSize();

    // An emptied box gives no number; the other box and the saved size stand until it has one again.
    private void PopupSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading)
            return;
        if (double.IsNaN(PopupWidthBox.Value) || double.IsNaN(PopupHeightBox.Value))
            return;
        App.Current.SetPopupSize((int)Math.Round(PopupWidthBox.Value), (int)Math.Round(PopupHeightBox.Value));
    }

    private void ResetPopupSize_Click(object sender, RoutedEventArgs e) =>
        App.Current.SetPopupSize(PopupSize.DefaultWidth, PopupSize.DefaultHeight);

    private void ShowTemplateOptions(TemplateOptions options)
    {
        HourRangeChoice.SelectedIndex = (int)options.HourRange;
        FixedHoursCard.Visibility = options.HourRange == HourRange.Fixed ? Visibility.Visible : Visibility.Collapsed;
        NextHoursCard.Visibility = options.HourRange == HourRange.FromNow ? Visibility.Visible : Visibility.Collapsed;
        StartHourChoice.SelectedIndex = options.StartHour;
        EndHourChoice.SelectedIndex = options.EndHour - 1;
        NextHoursChoice.SelectedIndex = options.NextHours - 1;
        FirstDayChoice.SelectedIndex = options.FirstDay is DayOfWeek day ? (int)day + 1 : 0;
        OriginChoice.SelectedIndex = Array.IndexOf(Origins, options.GraphOrigin);
        NumbersChoice.SelectedIndex = TemplateOptions.GraphNumberChoices.ToList().IndexOf(options.GraphNumbers);
        NumbersChoice.IsEnabled = options.GraphOrigin != GraphOrigin.None;
    }

    private void Template_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HourRangeChoice.SelectedIndex < 0 || StartHourChoice.SelectedIndex < 0 || EndHourChoice.SelectedIndex < 0 ||
            NextHoursChoice.SelectedIndex < 0 || FirstDayChoice.SelectedIndex < 0 || OriginChoice.SelectedIndex < 0 || NumbersChoice.SelectedIndex < 0)
            return;
        int start = StartHourChoice.SelectedIndex, end = EndHourChoice.SelectedIndex + 1;
        // A page always has at least one hour; moving either end past the other pushes it along.
        if (end <= start)
        {
            if (ReferenceEquals(sender, StartHourChoice))
                end = start + 1;
            else
                start = end - 1;
        }
        TemplateOptions options = new()
        {
            HourRange = (HourRange)HourRangeChoice.SelectedIndex,
            StartHour = start,
            EndHour = end,
            NextHours = NextHoursChoice.SelectedIndex + 1,
            FirstDay = FirstDayChoice.SelectedIndex > 0 ? (DayOfWeek)(FirstDayChoice.SelectedIndex - 1) : null,
            GraphOrigin = Origins[OriginChoice.SelectedIndex],
            GraphNumbers = TemplateOptions.GraphNumberChoices[NumbersChoice.SelectedIndex]
        };
        App.Current.TemplateOptions = options;
        _loading = true;
        ShowTemplateOptions(options);
        _loading = false;
    }

    private void DefaultTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && DefaultTemplateChoice.SelectedIndex >= 0)
            App.Current.DefaultTemplate = Templates[DefaultTemplateChoice.SelectedIndex];
    }

    private void DefaultPaper_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && DefaultPaperChoice.SelectedIndex >= 0)
            App.Current.DefaultPaper = NoteColors.All[DefaultPaperChoice.SelectedIndex];
    }

    private void FreshAfter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && FreshAfter.SelectedIndex >= 0)
            App.Current.FreshAfterMinutes = FreshPagePolicy.Choices[FreshAfter.SelectedIndex];
    }

    private void ShowStartupState(StartupTaskState? state)
    {
        _loading = true;
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        _loading = false;
        // Windows Settings and policy have the final say, so say where to change it instead of offering a toggle that can't.
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        StartupDescription.Text = state switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Settings > Apps > Startup. Turn it on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organization.",
            null => "Only available when MiniInk is installed.",
            _ => "Keep MiniInk in the tray after you sign in."
        };
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            ShowStartupState(await StartupService.SetEnabledAsync(StartupToggle.IsOn));
    }

    private void Back_Click(object sender, RoutedEventArgs e) => _back();
}
