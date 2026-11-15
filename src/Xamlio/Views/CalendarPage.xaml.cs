using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Xamlio.Services;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Xamlio.Views
{
    public sealed partial class CalendarPage : Page
    {
        private enum CalendarMode
        {
            Month,
            Week,
            Day
        }

        private CalendarMode _mode = CalendarMode.Month;
        private DateTime _currentDate = DateTime.Today;
        private List<ReleaseCalendarEntry> _entries =
            new List<ReleaseCalendarEntry>();

        public CalendarPage()
        {
            InitializeComponent();
            RequiresPointer = Windows.UI.Xaml.Controls.RequiresPointer.Never;
            App.SetPointerInputEnabled(false);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            RequiresPointer = Windows.UI.Xaml.Controls.RequiresPointer.Never;
            App.SetPointerInputEnabled(false);
            _entries = ReleaseReminderService.GetCalendarEntries();
            Render();
            MonthButton.Focus(FocusState.Programmatic);
            await LoadMissingPostersAsync();
            Render();
        }

        private async Task LoadMissingPostersAsync()
        {
            var missing = _entries
                .Where(entry => string.IsNullOrWhiteSpace(entry.Poster) &&
                    !string.IsNullOrWhiteSpace(entry.MediaId))
                .GroupBy(entry => entry.MediaId,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missing.Count == 0)
                return;

            try
            {
                await AppServices.EnsureAddonsLoadedAsync();
                foreach (var group in missing)
                {
                    var first = group.First();
                    var meta = await AppServices.Addons.GetMetaAsync(
                        first.Type ?? "series", first.MediaId);
                    var poster = meta?.Poster;
                    if (string.IsNullOrWhiteSpace(poster))
                        continue;

                    ReleaseReminderService.UpdateReminderPoster(
                        first.MediaId, poster);
                    foreach (var entry in group)
                        entry.Poster = poster;
                }
            }
            catch (Exception ex)
            {
                AppLog.WriteLine(
                    "[Calendar] Poster lookup failed: " + ex.Message);
            }
        }

        private void Month_Click(object sender, RoutedEventArgs e)
        {
            _mode = CalendarMode.Month;
            Render();
        }

        private void Week_Click(object sender, RoutedEventArgs e)
        {
            _mode = CalendarMode.Week;
            Render();
        }

        private void Day_Click(object sender, RoutedEventArgs e)
        {
            _mode = CalendarMode.Day;
            Render();
        }

        private void Previous_Click(object sender, RoutedEventArgs e)
        {
            _currentDate = _mode == CalendarMode.Month
                ? _currentDate.AddMonths(-1)
                : _mode == CalendarMode.Week
                    ? _currentDate.AddDays(-7)
                    : _currentDate.AddDays(-1);
            Render();
        }

        private void Today_Click(object sender, RoutedEventArgs e)
        {
            _currentDate = DateTime.Today;
            Render();
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            _currentDate = _mode == CalendarMode.Month
                ? _currentDate.AddMonths(1)
                : _mode == CalendarMode.Week
                    ? _currentDate.AddDays(7)
                    : _currentDate.AddDays(1);
            Render();
        }

        private void Render()
        {
            WeekdayHeader.Visibility = _mode == CalendarMode.Day
                ? Visibility.Collapsed
                : Visibility.Visible;
            CalendarGrid.Visibility = _mode == CalendarMode.Day
                ? Visibility.Collapsed
                : Visibility.Visible;
            DayScroll.Visibility = _mode == CalendarMode.Day
                ? Visibility.Visible
                : Visibility.Collapsed;

            RangeTitle.Text = GetRangeTitle();
            SetModeButtonState();

            if (_mode == CalendarMode.Day)
                RenderDay();
            else if (_mode == CalendarMode.Week)
                RenderWeek();
            else
                RenderMonth();
        }

        private void SetModeButtonState()
        {
            MonthButton.IsEnabled = _mode != CalendarMode.Month;
            WeekButton.IsEnabled = _mode != CalendarMode.Week;
            DayButton.IsEnabled = _mode != CalendarMode.Day;
        }

        private string GetRangeTitle()
        {
            if (_mode == CalendarMode.Month)
                return _currentDate.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

            if (_mode == CalendarMode.Day)
            {
                return _currentDate.ToString(
                    "dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
            }

            var start = StartOfWeek(_currentDate);
            var end = start.AddDays(6);
            return start.Month == end.Month
                ? start.ToString("MMM d", CultureInfo.CurrentCulture) +
                  " – " + end.ToString("d, yyyy", CultureInfo.CurrentCulture)
                : start.ToString("MMM d", CultureInfo.CurrentCulture) +
                  " – " + end.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        }

        private void RenderMonth()
        {
            CalendarGrid.Children.Clear();
            CalendarGrid.RowDefinitions.Clear();
            CalendarGrid.ColumnDefinitions.Clear();
            AddColumns(CalendarGrid, 7);
            for (var row = 0; row < 6; row++)
                CalendarGrid.RowDefinitions.Add(new RowDefinition());

            var first = new DateTime(_currentDate.Year, _currentDate.Month, 1);
            var start = first.AddDays(-(int)first.DayOfWeek);
            var hasEntries = _entries.Any(entry =>
                entry.ReleaseTime.ToLocalTime().Date >= start.Date &&
                entry.ReleaseTime.ToLocalTime().Date < start.Date.AddDays(42));
            EmptyMessage.Visibility = hasEntries
                ? Visibility.Collapsed
                : Visibility.Visible;
            for (var index = 0; index < 42; index++)
            {
                var date = start.AddDays(index);
                var cell = CreateDayCell(
                    date,
                    date.Month == _currentDate.Month,
                    4);
                Grid.SetColumn(cell, index % 7);
                Grid.SetRow(cell, index / 7);
                CalendarGrid.Children.Add(cell);
            }

            SetWeekdayHeader();
        }

        private void RenderWeek()
        {
            CalendarGrid.Children.Clear();
            CalendarGrid.RowDefinitions.Clear();
            CalendarGrid.ColumnDefinitions.Clear();
            AddColumns(CalendarGrid, 7);
            CalendarGrid.RowDefinitions.Add(new RowDefinition());

            var start = StartOfWeek(_currentDate);
            var hasEntries = _entries.Any(entry =>
                entry.ReleaseTime.ToLocalTime().Date >= start.Date &&
                entry.ReleaseTime.ToLocalTime().Date < start.Date.AddDays(7));
            EmptyMessage.Visibility = hasEntries
                ? Visibility.Collapsed
                : Visibility.Visible;
            for (var index = 0; index < 7; index++)
            {
                var cell = CreateDayCell(start.AddDays(index), true, 12);
                Grid.SetColumn(cell, index);
                CalendarGrid.Children.Add(cell);
            }

            SetWeekdayHeader();
        }

        private void RenderDay()
        {
            DayList.Children.Clear();
            var entries = EntriesForDate(_currentDate).ToList();
            EmptyMessage.Visibility = entries.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            foreach (var entry in entries)
            {
                var button = CreateEntryButton(entry, true);
                DayList.Children.Add(button);
            }
        }

        private Border CreateDayCell(
            DateTime date,
            bool currentMonth,
            int maxEntries)
        {
            var entries = EntriesForDate(date).ToList();
            var panel = new StackPanel { Spacing = 2 };
            var heading = new TextBlock
            {
                Text = date.Day.ToString(CultureInfo.InvariantCulture),
                FontSize = 20,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = currentMonth
                    ? new SolidColorBrush(Colors.White)
                    : new SolidColorBrush(Color.FromArgb(255, 120, 117, 140))
            };
            panel.Children.Add(heading);

            foreach (var entry in entries.Take(maxEntries))
                panel.Children.Add(CreateEntryButton(entry, false));

            if (entries.Count > maxEntries)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "+" + (entries.Count - maxEntries) + " more",
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 169, 167, 191)),
                    Margin = new Thickness(6, 2, 0, 0)
                });
            }

            var border = new Border
            {
                Margin = new Thickness(2),
                Padding = new Thickness(8),
                Background = new SolidColorBrush(currentMonth
                    ? Color.FromArgb(255, 36, 33, 58)
                    : Color.FromArgb(255, 27, 25, 43)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 62, 58, 88)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = panel
            };
            return border;
        }

        private Button CreateEntryButton(
            ReleaseCalendarEntry entry,
            bool large)
        {
            var time = entry.ReleaseTime.ToLocalTime().ToString(
                large ? "h:mm tt" : "h:mm", CultureInfo.CurrentCulture);
            var posterWidth = large ? 110 : 54;
            var posterHeight = large ? 154 : 76;
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(posterWidth)
            });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            content.Children.Add(CreatePosterVisual(
                entry,
                posterWidth,
                posterHeight,
                large));

            var details = new StackPanel
            {
                Margin = new Thickness(large ? 14 : 7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            details.Children.Add(new TextBlock
            {
                Text = time,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 215, 194, 255)),
                FontSize = large ? 20 : 12
            });
            details.Children.Add(new TextBlock
            {
                Text = entry.DisplayTitle,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = large ? TextWrapping.Wrap : TextWrapping.NoWrap,
                FontSize = large ? 24 : 13
            });
            Grid.SetColumn(details, 1);
            content.Children.Add(details);

            return new Button
            {
                Tag = entry,
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(6, large ? 12 : 4, 6, large ? 12 : 4),
                Background = new SolidColorBrush(Color.FromArgb(255, 71, 48, 105))
            }.WithClick(Reminder_Click);
        }

        private static FrameworkElement CreatePosterVisual(
            ReleaseCalendarEntry entry,
            double width,
            double height,
            bool large)
        {
            var poster = new Image
            {
                Width = width,
                Height = height,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (Uri.TryCreate(entry.Poster, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp ||
                 uri.Scheme == Uri.UriSchemeHttps))
            {
                poster.Source = new BitmapImage(uri);
                return poster;
            }

            var title = entry.ShowTitle ?? "?";
            var initials = new string(title
                .Split(new[] { ' ', '-', ':' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Take(2)
                .Select(word => word[0])
                .ToArray())
                .ToUpperInvariant();
            return new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromArgb(255, 48, 45, 76)),
                Child = new TextBlock
                {
                    Text = initials,
                    FontSize = large ? 34 : 16,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 192, 160, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
        }

        private void Reminder_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var entry = button?.Tag as ReleaseCalendarEntry;
            if (entry == null || string.IsNullOrWhiteSpace(entry.MediaId))
                return;

            Frame.Navigate(typeof(DetailPage), new MetaArgs
            {
                Type = string.IsNullOrWhiteSpace(entry.Type)
                    ? "series"
                    : entry.Type,
                Id = entry.MediaId
            });
        }

        private IEnumerable<ReleaseCalendarEntry> EntriesForDate(DateTime date)
        {
            return _entries
                .Where(entry => entry.ReleaseTime.ToLocalTime().Date == date.Date)
                .OrderBy(entry => entry.ReleaseTime)
                .ThenBy(entry => entry.ShowTitle);
        }

        private void SetWeekdayHeader()
        {
            WeekdayHeader.Children.Clear();
            WeekdayHeader.ColumnDefinitions.Clear();
            AddColumns(WeekdayHeader, 7);
            var start = StartOfWeek(DateTime.Today);
            for (var index = 0; index < 7; index++)
            {
                var text = new TextBlock
                {
                    Text = start.AddDays(index).ToString(
                        "ddd", CultureInfo.CurrentCulture),
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 169, 167, 191)),
                    Margin = new Thickness(8, 0, 0, 0)
                };
                Grid.SetColumn(text, index);
                WeekdayHeader.Children.Add(text);
            }
        }

        private static DateTime StartOfWeek(DateTime date)
        {
            return date.Date.AddDays(-(int)date.DayOfWeek);
        }

        private static void AddColumns(Grid grid, int count)
        {
            for (var index = 0; index < count; index++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            }
        }
    }

    internal static class CalendarButtonExtensions
    {
        public static Button WithClick(
            this Button button,
            RoutedEventHandler handler)
        {
            button.Click += handler;
            return button;
        }
    }
}
