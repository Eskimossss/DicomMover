using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace DicomMover.Controls;

public partial class StudyDateTimePicker : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate), typeof(DateTime?), typeof(StudyDateTimePicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, ValueChanged));
    public static readonly DependencyProperty SelectedTimeProperty = DependencyProperty.Register(
        nameof(SelectedTime), typeof(string), typeof(StudyDateTimePicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, ValueChanged));

    private bool _updating;
    public bool IsUpperBound { get; set; }
    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }
    public string? SelectedTime
    {
        get => (string?)GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    public StudyDateTimePicker()
    {
        InitializeComponent();
        HoursCombo.ItemsSource = Enumerable.Range(0, 24).Select(i => i.ToString("D2", CultureInfo.InvariantCulture)).ToArray();
        MinutesCombo.ItemsSource = Enumerable.Range(0, 60).Select(i => i.ToString("D2", CultureInfo.InvariantCulture)).ToArray();
        DataContextChanged += (_, _) => EditorPopup.IsOpen = false;
    }

    private static void ValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var picker = (StudyDateTimePicker)sender;
        if (picker.ValueText is null) return;
        picker.RefreshDisplay();
        if (picker.EditorPopup.IsOpen && !picker._updating) picker.SyncEditor();
    }

    private bool TryGetTime(out TimeSpan time) => TimeSpan.TryParseExact(SelectedTime?.Trim(),
        [@"hh\:mm", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);

    private void RefreshDisplay()
    {
        ValueText.Text = SelectedDate is { } date
            ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) +
              (TryGetTime(out var time) ? "  " + time.ToString(time.Seconds == 0 ? @"hh\:mm" : @"hh\:mm\:ss", CultureInfo.InvariantCulture) :
                  string.IsNullOrWhiteSpace(SelectedTime) ? string.Empty : "  — время?")
            : "Не ограничено";
        ValueText.Foreground = new SolidColorBrush(SelectedDate.HasValue ? Color.FromRgb(23, 32, 51) : Color.FromRgb(102, 112, 133));
        System.Windows.Automation.AutomationProperties.SetName(PickerButton,
            (IsUpperBound ? "До: " : "С: ") + ValueText.Text);
    }

    private void SyncEditor()
    {
        _updating = true;
        try
        {
            var hasTime = TryGetTime(out var time);
            DateCalendar.SelectedDate = SelectedDate?.Date;
            DateCalendar.DisplayDate = SelectedDate ?? DateTime.Today;
            UseTimeCheck.IsChecked = hasTime;
            HoursCombo.SelectedIndex = hasTime ? time.Hours : IsUpperBound ? 23 : 0;
            MinutesCombo.SelectedIndex = hasTime ? time.Minutes : IsUpperBound ? 59 : 0;
            TimePanel.IsEnabled = hasTime;
            NoTimeHint.Text = IsUpperBound ? "Без времени — до конца выбранного дня." : "Без времени — с начала выбранного дня.";
            PopupTitle.Text = IsUpperBound ? "До даты и времени включительно" : "С даты и времени";
        }
        finally { _updating = false; }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => EditorPopup.IsOpen = !EditorPopup.IsOpen;
    private void EditorPopup_Opened(object? sender, EventArgs e) => SyncEditor();

    private void DateCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        SetCurrentValue(SelectedDateProperty, DateCalendar.SelectedDate);
        // Calendar otherwise consumes the first click on a time selector after choosing a day.
        if (DateCalendar.IsMouseCaptureWithin) System.Windows.Input.Mouse.Capture(null);
    }

    private void Time_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating || TimePanel is null) return;
        _updating = true;
        try
        {
            var useTime = UseTimeCheck.IsChecked == true;
            TimePanel.IsEnabled = useTime;
            if (useTime && !SelectedDate.HasValue)
            {
                SetCurrentValue(SelectedDateProperty, DateCalendar.DisplayDate.Date);
                DateCalendar.SelectedDate = SelectedDate;
            }
            SetCurrentValue(SelectedTimeProperty, useTime
                ? $"{Math.Max(0, HoursCombo.SelectedIndex):D2}:{Math.Max(0, MinutesCombo.SelectedIndex):D2}" : null);
        }
        finally { _updating = false; }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SetCurrentValue(SelectedTimeProperty, null);
        SetCurrentValue(SelectedDateProperty, null);
        EditorPopup.IsOpen = false;
        PickerButton.Focus();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        EditorPopup.IsOpen = false;
        PickerButton.Focus();
    }

    private void Root_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && EditorPopup.IsOpen)
        {
            EditorPopup.IsOpen = false;
            PickerButton.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && !EditorPopup.IsOpen)
        {
            Clear_Click(sender, e);
            e.Handled = true;
        }
    }
}
