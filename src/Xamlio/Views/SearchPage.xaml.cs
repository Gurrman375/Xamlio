using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Stremio.Core.Addons;
using Xamlio.Services;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Xamlio.Views
{
    public sealed class SearchRow
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public ObservableCollection<MetaPreview> Items { get; } =
            new ObservableCollection<MetaPreview>();

        public Visibility RowVisibility =>
            Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public sealed partial class SearchPage : Page
    {
        private readonly ObservableCollection<SearchRow> _rows =
            new ObservableCollection<SearchRow>();
        private readonly Dictionary<string, int> _selectedIndexes =
            new Dictionary<string, int>();
        private readonly DispatcherTimer _gamepadTimer;

        private int _searchId;
        private bool _loaded;
        private bool _horizontalHeld;
        private bool _verticalHeld;
        private bool _aHeld;
        private DateTime _lastHorizontalMove = DateTime.MinValue;
        private DateTime _lastVerticalMove = DateTime.MinValue;

        private static readonly SolidColorBrush SelectedPosterBrush =
            new SolidColorBrush(Color.FromArgb(255, 192, 32, 255));
        private static readonly SolidColorBrush TransparentBrush =
            new SolidColorBrush(Colors.Transparent);

        public SearchPage()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;
            RequiresPointer = Windows.UI.Xaml.Controls.RequiresPointer.Never;
            App.SetPointerInputEnabled(false);
            Rows.ItemsSource = _rows;

            _gamepadTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _gamepadTimer.Tick += GamepadTimer_Tick;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            RequiresPointer = Windows.UI.Xaml.Controls.RequiresPointer.Never;
            App.SetPointerInputEnabled(false);
            _horizontalHeld = false;
            _verticalHeld = false;
            _aHeld = false;
            _lastHorizontalMove = DateTime.MinValue;
            _lastVerticalMove = DateTime.MinValue;

            if (_loaded)
                RestoreCurrentSelection();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
        }

        private void SearchPage_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                RestoreCurrentSelection);
        }

        private void SearchList_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                RestoreCurrentSelection);
        }

        private void SearchPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Let XAML XY focus navigation handle controller movement once.
        }

        private void SearchList_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Let XAML XY focus navigation handle controller movement once.
        }

        private async void Query_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter &&
                e.Key != VirtualKey.GamepadMenu)
            {
                return;
            }

            e.Handled = true;
            await SearchAsync(Query.Text.Trim());
        }

        private async Task SearchAsync(string text)
        {
            if (text.Length == 0)
                return;

            var id = ++_searchId;
            _loaded = true;
            _rows.Clear();
            Message.Visibility = Visibility.Collapsed;
            Loading.IsActive = true;

            try
            {
                await AppServices.EnsureAddonsLoadedAsync();
                var extra = new Dictionary<string, string>
                {
                    ["search"] = text
                };

                var sources = AppServices.Addons.GetCatalogs()
                    .Where(c => c.SupportsSearch)
                    .ToList();

                var batches = await Task.WhenAll(sources.Select(async source =>
                {
                    try
                    {
                        var metas = await AppServices.Addons.Client.GetCatalogAsync(
                            source.Addon,
                            source.Catalog.Type,
                            source.Catalog.Id,
                            extra);

                        return metas.Select(meta =>
                        {
                            meta.Type = meta.Type ?? source.Catalog.Type;
                            return meta;
                        }).ToList();
                    }
                    catch (Exception)
                    {
                        return new List<MetaPreview>();
                    }
                }).ToList());

                if (id != _searchId)
                    return;

                var seen = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                var results = batches
                    .SelectMany(batch => batch)
                    .Where(meta => seen.Add((meta.Type ?? "") + "/" + meta.Id))
                    .ToList();

                AddRow(
                    "movies",
                    "Movies",
                    results.Where(IsMovie));
                AddRow(
                    "series",
                    "Series",
                    results.Where(IsSeries));
                AddRow(
                    "other",
                    "Other",
                    results.Where(meta => !IsMovie(meta) && !IsSeries(meta)));

                if (_rows.Count == 0)
                {
                    Message.Text = "No results for \"" + text + "\".";
                    Message.Visibility = Visibility.Visible;
                }
                else
                {
                    RestoreCurrentSelection();
                }
            }
            catch (Exception ex)
            {
                Message.Text = "Couldn't search: " + ex.Message;
                Message.Visibility = Visibility.Visible;
            }
            finally
            {
                if (id == _searchId)
                {
                    Loading.IsActive = false;
                    Loading.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void AddRow(
            string key,
            string title,
            IEnumerable<MetaPreview> items)
        {
            var row = new SearchRow
            {
                Key = key,
                Title = title
            };

            foreach (var item in items)
                row.Items.Add(item);

            if (row.Items.Count > 0)
                _rows.Add(row);
        }

        private static bool IsMovie(MetaPreview meta) =>
            string.Equals(
                meta.Type,
                "movie",
                StringComparison.OrdinalIgnoreCase);

        private static bool IsSeries(MetaPreview meta) =>
            string.Equals(
                meta.Type,
                "series",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                meta.Type,
                "tv",
                StringComparison.OrdinalIgnoreCase);

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
            var aPressed = (reading.Buttons & GamepadButtons.A) != 0;

            var now = DateTime.UtcNow;

            if ((left || right) &&
                !_horizontalHeld &&
                now - _lastHorizontalMove >= TimeSpan.FromMilliseconds(250))
            {
                MovePosterFocus(right ? 1 : -1);
                _lastHorizontalMove = now;
            }

            if ((up || down) &&
                !_verticalHeld &&
                now - _lastVerticalMove >= TimeSpan.FromMilliseconds(250))
            {
                MoveRow(down ? 1 : -1);
                _lastVerticalMove = now;
            }

            if (aPressed && !_aHeld)
                OpenFocusedResult();

            _horizontalHeld = left || right;
            _verticalHeld = up || down;
            _aHeld = aPressed;
        }

        private void MovePosterFocus(int amount)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            var item = FindParent<ListViewItem>(focused);
            var list = FindParent<ListView>(focused);
            if (item == null || list == null)
                return;

            var current = list.IndexFromContainer(item);
            var next = Math.Max(
                0,
                Math.Min(list.Items.Count - 1, current + amount));
            if (next == current)
                return;

            SaveSelection(list, next);
            FocusListItem(list, next);
        }

        private void MoveRow(int amount)
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            var currentList = FindParent<ListView>(focused);
            var lists = FindDescendants<ListView>(this).ToList();

            if (currentList == null)
            {
                if (amount > 0 && lists.Count > 0)
                    FocusListItem(lists[0], 0);
                return;
            }

            var currentRow = lists.IndexOf(currentList);
            var nextRow = Math.Max(
                0,
                Math.Min(lists.Count - 1, currentRow + amount));
            if (nextRow == currentRow)
                return;

            var target = lists[nextRow];
            var currentItem = FindParent<ListViewItem>(focused);
            var sourceIndex = currentItem == null
                ? 0
                : currentList.IndexFromContainer(currentItem);
            var targetIndex = Math.Max(
                0,
                Math.Min(target.Items.Count - 1, sourceIndex));
            FocusListItem(target, targetIndex);
        }

        private void RestoreCurrentSelection()
        {
            foreach (var list in FindDescendants<ListView>(this))
            {
                if (list.Items.Count == 0)
                    continue;

                var row = FindParent<StackPanel>(list)?.DataContext as SearchRow;
                var index = row != null &&
                            _selectedIndexes.TryGetValue(row.Key, out var saved)
                    ? saved
                    : 0;

                FocusListItem(
                    list,
                    Math.Max(0, Math.Min(list.Items.Count - 1, index)));
                return;
            }

            if (_rows.Count == 0)
                Query.Focus(FocusState.Programmatic);
        }

        private void FocusListItem(ListView list, int index)
        {
            if (list == null || list.Items.Count == 0)
                return;

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
            var row = FindParent<StackPanel>(list)?.DataContext as SearchRow;
            if (row != null)
                _selectedIndexes[row.Key] = index;
        }

        private void SearchList_ContainerContentChanging(
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

        private static void SetPosterHighlight(
            ListViewItem item,
            bool selected)
        {
            var ring = FindDescendants<Border>(item)
                .FirstOrDefault(border => border.Name == "PosterFocusRing");
            if (ring == null)
                return;

            ring.BorderBrush = selected
                ? SelectedPosterBrush
                : TransparentBrush;
            ring.BorderThickness = selected
                ? new Thickness(3)
                : new Thickness(0);
        }

        private void Results_ItemClick(object sender, ItemClickEventArgs e)
        {
            var meta = e.ClickedItem as MetaPreview;
            OpenResult(meta);
        }

        private void OpenFocusedResult()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            var item = FindParent<ListViewItem>(focused);
            if (item?.Content is MetaPreview meta)
                OpenResult(meta);
        }

        private void OpenResult(MetaPreview meta)
        {
            if (meta == null)
                return;

            Frame.Navigate(
                typeof(DetailPage),
                new MetaArgs
                {
                    Type = meta.Type,
                    Id = meta.Id
                });
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
    }
}
