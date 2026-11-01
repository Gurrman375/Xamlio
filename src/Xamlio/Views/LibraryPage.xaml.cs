using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Stremio.Core.Api;
using Xamlio.Services;
using Windows.Gaming.Input;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Xamlio.Views
{
    public sealed class LibraryRow
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public ObservableCollection<LibraryCard> Items { get; } =
            new ObservableCollection<LibraryCard>();

        public Visibility RowVisibility =>
            Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public sealed class LibraryCard
    {
        public LibraryItem Item { get; }
        public SavedPlaybackState Playback { get; }

        public string Id => Item.Id;
        public string Type => Item.Type;
        public string Poster => Item.Poster;
        public string Name => Playback?.Title ?? Item.Name;
        public bool IsContinueWatching => Playback != null;
        public string PlaybackVideoId => Playback?.VideoId ?? Item.Id;
        public string PlaybackUrl => Playback?.Url;
        public Visibility ProgressVisibility =>
            Playback == null ? Visibility.Collapsed : Visibility.Visible;
        public double ProgressWidth =>
            Playback == null || Playback.Duration <= 0
                ? 0
                : 300.0 * Math.Min(1.0, (double)Playback.TimeMs / Playback.Duration);

        public LibraryCard(LibraryItem item) { Item = item; }

        public LibraryCard(LibraryItem item, SavedPlaybackState playback)
        {
            Item = item;
            Playback = playback;
        }
    }

    public sealed class SavedPlaybackState
    {
        public string VideoId { get; set; }
        public string Title { get; set; }
        public long TimeMs { get; set; }
        public long Duration { get; set; }
        public DateTime SavedUtc { get; set; }
        public string Url { get; set; }
    }

    public sealed partial class LibraryPage : Page
    {
        private readonly ObservableCollection<LibraryRow> _rows =
            new ObservableCollection<LibraryRow>();
        private readonly Dictionary<string, int> _selectedIndexes =
            new Dictionary<string, int>();
        private readonly DispatcherTimer _gamepadTimer;

        private bool _loaded;
        private bool _horizontalHeld;
        private bool _verticalHeld;

        private static readonly SolidColorBrush SelectedPosterBrush =
            new SolidColorBrush(Color.FromArgb(255, 192, 32, 255));
        private static readonly SolidColorBrush TransparentBrush =
            new SolidColorBrush(Colors.Transparent);

        public LibraryPage()
        {
            InitializeComponent();
            RequiresPointer = Windows.UI.Xaml.Controls.RequiresPointer.Never;
            App.SetPointerInputEnabled(false);
            Rows.ItemsSource = _rows;

            _gamepadTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _gamepadTimer.Tick += GamepadTimer_Tick;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            RequiresPointer = Windows.UI.Xaml.Controls.RequiresPointer.Never;
            App.SetPointerInputEnabled(false);
            _horizontalHeld = false;
            _verticalHeld = false;
            if (_loaded)
            {
                RestoreCurrentSelection();
                return;
            }

            _loaded = true;
            await LoadAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _gamepadTimer.Stop();
            base.OnNavigatedFrom(e);
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            if (!AppServices.Settings.IsLoggedIn)
            {
                ShowMessage("Log in under Settings to see your Stremio library.");
                Loading.IsActive = false;
                Loading.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var items = await AppServices.Api.GetLibraryAsync(
                    AppServices.Settings.AuthKey);
                var visible = items
                    .Where(i => !i.Removed && !i.Temp)
                    .OrderByDescending(i => i.ModifiedTime ?? DateTime.MinValue)
                    .ToList();

                _rows.Clear();
                var continueCards = BuildContinueWatchingCards(visible);
                if (continueCards.Count > 0)
                    AddRow("continue", "Continue Watching", continueCards);

                AddRow("movies", "Movies", visible.Where(IsMovie).Select(i => new LibraryCard(i)));
                AddRow("series", "Series", visible.Where(IsSeries).Select(i => new LibraryCard(i)));
                AddRow("other", "Other", visible.Where(i => !IsMovie(i) && !IsSeries(i)).Select(i => new LibraryCard(i)));

                if (_rows.Count == 0)
                    ShowMessage("Your library is empty.");
            }
            catch (Exception ex)
            {
                ShowMessage("Couldn't load your library: " + ex.Message);
            }
            finally
            {
                Loading.IsActive = false;
                Loading.Visibility = Visibility.Collapsed;
                RestoreCurrentSelection();
            }
        }

        private void AddRow(string key, string title, IEnumerable<LibraryCard> items)
        {
            var row = new LibraryRow { Key = key, Title = title };
            foreach (var item in items)
                row.Items.Add(item);
            if (row.Items.Count > 0)
                _rows.Add(row);
        }

        private static bool IsMovie(LibraryItem item) =>
            string.Equals(item.Type, "movie", StringComparison.OrdinalIgnoreCase);

        private static bool IsSeries(LibraryItem item) =>
            string.Equals(item.Type, "series", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Type, "tv", StringComparison.OrdinalIgnoreCase);

        private static List<LibraryCard> BuildContinueWatchingCards(
            List<LibraryItem> libraryItems)
        {
            var saved = new List<SavedPlaybackState>();

            foreach (var pair in ApplicationData.Current.LocalSettings.Values)
            {
                if (!pair.Key.StartsWith("playbackState:", StringComparison.Ordinal) ||
                    !(pair.Value is string raw))
                    continue;

                var parts = raw.Split('|');
                if (parts.Length < 5 ||
                    !long.TryParse(parts[2], out var timeMs) ||
                    !long.TryParse(parts[3], out var duration) ||
                    !DateTime.TryParse(parts[4], CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var savedUtc))
                    continue;

                if (timeMs <= 0 || duration <= 0 || timeMs >= duration * 0.95)
                    continue;

                saved.Add(new SavedPlaybackState
                {
                    VideoId = parts[0],
                    Title = parts[1],
                    TimeMs = timeMs,
                    Duration = duration,
                    SavedUtc = savedUtc,
                    Url = parts.Length >= 6
                        ? Uri.UnescapeDataString(parts[5])
                        : null
                });
            }

            var cards = new List<LibraryCard>();
            foreach (var item in libraryItems.Where(i => IsMovie(i) || IsSeries(i)))
            {
                var localState = saved
                    .Where(s => string.Equals(s.VideoId, item.Id,
                        StringComparison.OrdinalIgnoreCase) ||
                        (IsSeries(item) && s.VideoId.StartsWith(item.Id + ":",
                            StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(s => s.SavedUtc)
                    .FirstOrDefault();

                var remoteState = item.State != null &&
                    !string.IsNullOrWhiteSpace(item.State.VideoId) &&
                    item.State.TimeOffset > 0 &&
                    item.State.Duration > 0 &&
                    item.State.TimeOffset < item.State.Duration * 0.95
                    ? new SavedPlaybackState
                    {
                        VideoId = item.State.VideoId,
                        Title = item.Name,
                        TimeMs = item.State.TimeOffset,
                        Duration = item.State.Duration,
                        SavedUtc = item.ModifiedTime ?? DateTime.MinValue
                    }
                    : null;

                var state = localState ?? remoteState;
                if (state != null)
                    cards.Add(new LibraryCard(item, state));
            }

            return cards
                .OrderByDescending(c => c.Playback.SavedUtc)
                .ToList();
        }

        private void GamepadTimer_Tick(object sender, object e)
        {
            var gamepad = Gamepad.Gamepads.FirstOrDefault();
            if (gamepad == null)
                return;

            var reading = gamepad.GetCurrentReading();
            var left = (reading.Buttons & GamepadButtons.DPadLeft) != 0 ||
                       reading.LeftThumbstickX < -0.65;
            var right = (reading.Buttons & GamepadButtons.DPadRight) != 0 ||
                        reading.LeftThumbstickX > 0.65;
            var up = (reading.Buttons & GamepadButtons.DPadUp) != 0 ||
                     reading.LeftThumbstickY > 0.65;
            var down = (reading.Buttons & GamepadButtons.DPadDown) != 0 ||
                       reading.LeftThumbstickY < -0.65;

            if ((left || right) && !_horizontalHeld)
                MovePosterFocus(right ? 1 : -1);
            if ((up || down) && !_verticalHeld)
                MoveRow(down ? 1 : -1);

            _horizontalHeld = left || right;
            _verticalHeld = up || down;
        }

        private void LibraryPage_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.RunAsync(CoreDispatcherPriority.Low, RestoreCurrentSelection);
        }

        private void LibraryList_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                RestoreCurrentSelection);
        }

        private void LibraryPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Let XAML XY focus navigation handle controller movement.
        }

        private void LibraryList_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Let XAML XY focus navigation handle controller movement.
        }

        private void MovePosterFocus(int amount)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            var item = FindParent<ListViewItem>(focused);
            var list = FindParent<ListView>(focused);
            if (item == null || list == null)
                return;

            var current = list.IndexFromContainer(item);
            var next = Math.Max(0, Math.Min(list.Items.Count - 1, current + amount));
            if (next == current)
                return;

            SaveSelection(list, next);
            FocusListItem(list, next);
        }

        private void MoveRow(int amount)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            var currentList = FindParent<ListView>(focused);
            if (currentList == null)
                return;

            var lists = FindDescendants<ListView>(this).ToList();
            var currentRow = lists.IndexOf(currentList);
            var nextRow = Math.Max(0, Math.Min(lists.Count - 1, currentRow + amount));
            if (nextRow == currentRow)
                return;

            var target = lists[nextRow];
            var currentItem = FindParent<ListViewItem>(focused);
            var sourceIndex = currentItem == null ? 0 : currentList.IndexFromContainer(currentItem);
            var targetIndex = Math.Max(0, Math.Min(target.Items.Count - 1, sourceIndex));
            FocusListItem(target, targetIndex);
        }

        private void RestoreCurrentSelection()
        {
            foreach (var list in FindDescendants<ListView>(this))
            {
                if (list.Items.Count == 0)
                    continue;

                var row = FindParent<StackPanel>(list)?.DataContext as LibraryRow;
                var index = row != null && _selectedIndexes.TryGetValue(row.Key, out var saved)
                    ? saved
                    : 0;
                FocusListItem(list, Math.Max(0, Math.Min(list.Items.Count - 1, index)));
                return;
            }
        }

        private void FocusListItem(ListView list, int index)
        {
            list.UpdateLayout();
            var item = list.ContainerFromIndex(index) as Control;
            if (item == null)
            {
                list.ScrollIntoView(list.Items[index]);
                list.UpdateLayout();
                item = list.ContainerFromIndex(index) as Control;
            }
            item?.Focus(FocusState.Programmatic);
        }

        private void SaveSelection(ListView list, int index)
        {
            var row = FindParent<StackPanel>(list)?.DataContext as LibraryRow;
            if (row != null)
                _selectedIndexes[row.Key] = index;
        }

        private void LibraryList_ContainerContentChanging(
            ListViewBase sender,
            ContainerContentChangingEventArgs args)
        {
            var item = args.ItemContainer as ListViewItem;
            if (item == null)
                return;

            item.GotFocus -= PosterItem_GotFocus;
            item.LostFocus -= PosterItem_LostFocus;
            item.GotFocus += PosterItem_GotFocus;
            item.LostFocus += PosterItem_LostFocus;

            if (args.InRecycleQueue)
                SetPosterHighlight(item, false);
        }

        private void PosterItem_GotFocus(object sender, RoutedEventArgs e)
        {
            var item = sender as ListViewItem;
            var list = FindParent<ListView>(item);
            if (item == null || list == null)
                return;

            SaveSelection(list, list.IndexFromContainer(item));
            foreach (var other in FindDescendants<ListViewItem>(this))
                SetPosterHighlight(other, ReferenceEquals(other, item));
        }

        private void PosterItem_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is ListViewItem item)
                SetPosterHighlight(item, false);
        }

        private static void SetPosterHighlight(ListViewItem item, bool selected)
        {
            var ring = FindDescendants<Border>(item)
                .FirstOrDefault(x => x.Name == "PosterFocusRing");
            if (ring == null)
                return;

            ring.BorderBrush = selected ? SelectedPosterBrush : TransparentBrush;
            ring.BorderThickness = selected ? new Thickness(3) : new Thickness(0);
        }

        private void ShowMessage(string text)
        {
            Loading.IsActive = false;
            Message.Text = text;
            Message.Visibility = Visibility.Visible;
        }

        private void Items_ItemClick(object sender, ItemClickEventArgs e)
        {
            var card = e.ClickedItem as LibraryCard;
            if (card == null)
                return;

            if (card.IsContinueWatching)
            {
                if (!string.IsNullOrWhiteSpace(card.PlaybackUrl))
                {
                    ShellPage.ShowPlayer(new PlayerArgs
                    {
                        Url = card.PlaybackUrl,
                        VideoId = card.PlaybackVideoId,
                        Type = card.Type,
                        MetaId = card.Id,
                        Title = card.Name
                    });
                    return;
                }

                Frame.Navigate(typeof(StreamsPage), new StreamsArgs
                {
                    Type = card.Type,
                    VideoId = card.PlaybackVideoId,
                    MetaId = card.Id,
                    Title = card.Name
                });
                return;
            }

            Frame.Navigate(typeof(DetailPage), new MetaArgs
            {
                Type = card.Type,
                Id = card.Id
            });
        }

        private static T FindParent<T>(DependencyObject child)
            where T : DependencyObject
        {
            var current = child;
            while (current != null)
            {
                if (current is T match)
                    return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
                yield break;

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    yield return match;

                foreach (var descendant in FindDescendants<T>(child))
                    yield return descendant;
            }
        }
    }
}
