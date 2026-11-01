using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Stremio.Core.Addons;
using Xamlio.Services;
using Windows.Gaming.Input;
using Windows.Storage;
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
    public class CatalogRow : INotifyPropertyChanged
    {
        private bool _isEnabled = true;

        public int Index { get; set; }
        public string Key { get; set; }
        public string Title { get; set; }

        public ObservableCollection<MetaPreview> Items { get; } =
            new ObservableCollection<MetaPreview>();


        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                    return;

                _isEnabled = value;

                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed partial class BoardPage : Page
    {
        private const string DisabledCatalogsKey =
            "boardDisabledCatalogs";

        private readonly ObservableCollection<CatalogRow> _catalogs =
            new ObservableCollection<CatalogRow>();

        private readonly Dictionary<int, int> _titleIndexes =
            new Dictionary<int, int>();

        private readonly DispatcherTimer _gamepadTimer;

        private MenuFlyout _categoryMenu;
        private MenuFlyout _jumpMenu;

        private HashSet<string> _disabledCatalogKeys =
            new HashSet<string>();

        private bool _loaded;
        private bool _verticalInputHeld;
        private bool _horizontalInputHeld;
        private bool _menuInputHeld;
        private bool _backInputHeld;

        private int _topCatalogIndex;
        private int _activeVisibleRow;

        private static readonly SolidColorBrush SelectedPosterBrush =
            new SolidColorBrush(
                Color.FromArgb(255, 192, 32, 255));

        private static readonly SolidColorBrush TransparentBrush =
            new SolidColorBrush(Colors.Transparent);

        private bool IsMenuOpen
        {
            get
            {
                return (BoardSideMenu != null &&
                        BoardSideMenu.IsPaneOpen) ||
                       (_categoryMenu != null &&
                        _categoryMenu.IsOpen) ||
                       (_jumpMenu != null &&
                        _jumpMenu.IsOpen);
            }
        }

        public BoardPage()
        {
            InitializeComponent();
            RequiresPointer =
                Windows.UI.Xaml.Controls.RequiresPointer.Never;
            App.SetPointerInputEnabled(false);

            NavigationCacheMode =
                NavigationCacheMode.Enabled;

            _gamepadTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };

            _gamepadTimer.Tick += GamepadTimer_Tick;
        }

        protected override async void OnNavigatedTo(
            NavigationEventArgs e)
        {

            base.OnNavigatedTo(e);

            RequiresPointer =
                Windows.UI.Xaml.Controls.RequiresPointer.Never;

            // Board is controller-first. Explicitly disable pointer input
            // every time this page becomes active so the cursor cannot return
            // after navigating away from and back to the Board.
            App.SetPointerInputEnabled(false);

            _verticalInputHeld = false;
            _horizontalInputHeld = false;
            _menuInputHeld = false;
            _backInputHeld = false;

            _gamepadTimer.Start();
            if (_loaded)
            {
                RestoreCurrentSelection();
                return;
            }

            _loaded = true;
            await LoadAsync();
        }

        protected override void OnNavigatedFrom(
            NavigationEventArgs e)
        {
            _gamepadTimer.Stop();

            _categoryMenu?.Hide();
            _jumpMenu?.Hide();
            BoardSideMenu.IsPaneOpen = false;

            base.OnNavigatedFrom(e);
        }

        private void GamepadTimer_Tick(
            object sender,
            object e)
        {
            var gamepad =
                Gamepad.Gamepads.FirstOrDefault();

            if (gamepad == null)
                return;

            var reading =
                gamepad.GetCurrentReading();

            var menuPressed =
                (reading.Buttons &
                 GamepadButtons.Menu) != 0;

            var backPressed =
                (reading.Buttons &
                 GamepadButtons.B) != 0;

            if (menuPressed && !_menuInputHeld)
            {
                if (IsMenuOpen)
                    CloseCategoryPopup();
                else
                    OpenCategoryPopup();
            }

            if (backPressed && !_backInputHeld && IsMenuOpen)
                CloseCategoryPopup();

            _menuInputHeld = menuPressed;
            _backInputHeld = backPressed;

            if (IsMenuOpen)
                return;

            var leftPressed =
                (reading.Buttons &
                 GamepadButtons.DPadLeft) != 0 ||
                reading.LeftThumbstickX < -0.65;

            var rightPressed =
                (reading.Buttons &
                 GamepadButtons.DPadRight) != 0 ||
                reading.LeftThumbstickX > 0.65;

            var horizontalPressed =
                leftPressed || rightPressed;

            if (horizontalPressed &&
                !_horizontalInputHeld)
            {
                MovePosterFocus(
                    rightPressed ? 1 : -1);
            }

            _horizontalInputHeld = horizontalPressed;

            var upPressed =
                (reading.Buttons &
                 GamepadButtons.DPadUp) != 0 ||
                reading.LeftThumbstickY > 0.65;

            var downPressed =
                (reading.Buttons &
                 GamepadButtons.DPadDown) != 0 ||
                reading.LeftThumbstickY < -0.65;

            var verticalPressed =
                upPressed || downPressed;

            if (verticalPressed &&
                !_verticalInputHeld)
            {
                MoveCatalog(
                    upPressed ? -1 : 1);
            }

            _verticalInputHeld =
                verticalPressed;
        }

        private async Task LoadAsync()
        {
            try
            {
                LoadDisabledCatalogs();

                await AppServices.EnsureAddonsLoadedAsync();

                var sources = AppServices.Addons
                    .GetCatalogs()
                    .Where(c => !c.RequiresExtra)
                    .ToList();

                var tasks = sources.Select(async (source, index) =>
                {
                    var key =
                        source.Addon + "|" +
                        source.Catalog.Type + "|" +
                        source.Catalog.Id;

                    var catalog = new CatalogRow
                    {
                        Index = index,
                        Key = key,
                        Title = source.Title,
                        IsEnabled =
                            !_disabledCatalogKeys.Contains(key)
                    };

                    try
                    {
                        var metas =
                            await AppServices.Addons.Client.GetCatalogAsync(
                                source.Addon,
                                source.Catalog.Type,
                                source.Catalog.Id);

                        foreach (var meta in metas.Take(30))
                        {
                            meta.Type =
                                meta.Type ?? source.Catalog.Type;

                            catalog.Items.Add(meta);
                        }

                        if (catalog.Items.Count > 0)
                        {
                            await Dispatcher.RunAsync(
                                CoreDispatcherPriority.Normal,
                                () => AddCatalog(catalog));
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore broken addons.
                    }
                }).ToList();

                await Task.WhenAll(tasks);

                if (_catalogs.Count == 0)
                {
                    ShowMessage(
                        "No catalogs loaded. Check your connection, " +
                        "or log in under Settings to load your addons.");
                }
                else
                {
                    RebuildMenus();
                    ShowVisibleCatalogs();
                }
            }
            catch (Exception ex)
            {
                ShowMessage(
                    "Couldn't load addons: " + ex.Message);
            }
            finally
            {
                Loading.IsActive = false;
                Loading.Visibility = Visibility.Collapsed;
            }
        }

        private void AddCatalog(
            CatalogRow catalog)
        {
            if (catalog == null ||
                catalog.Items.Count == 0 ||
                _catalogs.Any(c =>
                    c.Index == catalog.Index))
            {
                return;
            }

            var insertIndex =
                _catalogs.Count(c =>
                    c.Index < catalog.Index);

            _catalogs.Insert(
                insertIndex,
                catalog);
        }

        private void RebuildMenus()
        {
            _categoryMenu =
                new MenuFlyout();

            _jumpMenu =
                new MenuFlyout();

            foreach (var catalog in _catalogs)
            {
                var toggle =
                    new ToggleMenuFlyoutItem
                    {
                        Text = catalog.Title,
                        IsChecked = catalog.IsEnabled,
                        Tag = catalog
                    };

                toggle.Click +=
                    CategoryToggle_Click;

                _categoryMenu.Items.Add(toggle);

                var jump =
                    new MenuFlyoutItem
                    {
                        Text = catalog.Title,
                        Tag = catalog
                    };

                jump.Click +=
                    JumpCategory_Click;

                _jumpMenu.Items.Add(jump);
            }
        }

        private void OpenCategoryPopup()
        {
            if (_categoryMenu == null ||
                _categoryMenu.Items.Count == 0)
            {
                RebuildMenus();
            }

            BoardSideMenu.IsPaneOpen = true;
            CategoryButton.IsTabStop = true;
            JumpButton.IsTabStop = true;
            _verticalInputHeld = false;
            _horizontalInputHeld = false;

            CategoryButton.Focus(
                FocusState.Programmatic);
        }

        private void CloseCategoryPopup()
        {
            _categoryMenu?.Hide();
            _jumpMenu?.Hide();
            BoardSideMenu.IsPaneOpen = false;
            CategoryButton.IsTabStop = false;
            JumpButton.IsTabStop = false;
            _verticalInputHeld = false;
            _horizontalInputHeld = false;

            Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                RestoreCurrentSelection);
        }

        private void CategoryButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_categoryMenu == null)
                RebuildMenus();

            _categoryMenu.ShowAt(
                CategoryButton);

            FocusFirstMenuItem(_categoryMenu);
        }

        private void JumpButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_jumpMenu == null)
                RebuildMenus();

            _jumpMenu.ShowAt(
                JumpButton);

            FocusFirstMenuItem(_jumpMenu);
        }

        private void CategoryToggle_Click(
            object sender,
            RoutedEventArgs e)
        {
            var item =
                sender as ToggleMenuFlyoutItem;

            var catalog =
                item?.Tag as CatalogRow;

            if (catalog == null)
                return;

            catalog.IsEnabled =
                item.IsChecked;

            SaveDisabledCatalogs();
            ShowVisibleCatalogs(false);

            Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                () =>
                {
                    RebuildMenus();
                    _categoryMenu.ShowAt(CategoryButton);
                    FocusFirstMenuItem(_categoryMenu);
                });
        }

        private static void FocusFirstMenuItem(
            MenuFlyout menu)
        {
            var first =
                menu?.Items.FirstOrDefault() as Control;

            first?.Focus(FocusState.Programmatic);
        }

        private void JumpCategory_Click(
            object sender,
            RoutedEventArgs e)
        {
            var item =
                sender as MenuFlyoutItem;

            var catalog =
                item?.Tag as CatalogRow;

            if (catalog == null)
                return;

            var enabled =
                EnabledCatalogs();

            var index =
                enabled.IndexOf(catalog);

            if (index < 0)
                return;

            _topCatalogIndex = index;
            _activeVisibleRow = 0;

            _jumpMenu?.Hide();
            BoardSideMenu.IsPaneOpen = false;
            ShowVisibleCatalogs();
        }

        private List<CatalogRow> EnabledCatalogs()
        {
            return _catalogs
                .Where(c => c.IsEnabled)
                .ToList();
        }

        private void ShowVisibleCatalogs(
            bool restoreFocus = true)
        {
            var enabled =
                EnabledCatalogs();

            ClearAllPosterHighlights();

            if (enabled.Count == 0)
            {
                TopCatalogTitle.Text =
                    "No categories enabled";

                BottomCatalogTitle.Text =
                    "Use Categories to enable one";

                TopCatalogList.ItemsSource = null;
                BottomCatalogList.ItemsSource = null;
                CatalogPosition.Text = "0 / 0";
                return;
            }

            if (_topCatalogIndex >= enabled.Count)
                _topCatalogIndex = 0;

            var topCatalog =
                enabled[_topCatalogIndex];

            TopCatalogTitle.Text =
                topCatalog.Title ?? "Catalog";

            TopCatalogList.ItemsSource =
                topCatalog.Items;

            var bottomIndex =
                _topCatalogIndex + 1;

            if (bottomIndex < enabled.Count)
            {
                var bottomCatalog =
                    enabled[bottomIndex];

                BottomCatalogTitle.Text =
                    bottomCatalog.Title ?? "Catalog";

                BottomCatalogList.ItemsSource =
                    bottomCatalog.Items;

                BottomCatalogList.Visibility =
                    Visibility.Visible;
            }
            else
            {
                BottomCatalogTitle.Text =
                    "No more catalogs";

                BottomCatalogList.ItemsSource =
                    null;

                BottomCatalogList.Visibility =
                    Visibility.Collapsed;
            }

            CatalogPosition.Text =
                (_topCatalogIndex + 1) +
                " / " +
                enabled.Count;

            if (restoreFocus)
            {
                Dispatcher.RunAsync(
                    CoreDispatcherPriority.Low,
                    RestoreCurrentSelection);
            }
        }

        private void MoveCatalog(
            int amount)
        {
            var enabled =
                EnabledCatalogs();

            if (enabled.Count == 0)
                return;

            UpdateFocusedTitleIndex();

            var current =
                _topCatalogIndex +
                _activeVisibleRow;

            if (current >= enabled.Count)
                current =
                    enabled.Count - 1;

            var target =
                current + amount;

            if (target < 0)
                target =
                    enabled.Count - 1;

            if (target >= enabled.Count)
                target = 0;

            if (amount > 0)
            {
                if (_activeVisibleRow == 0 &&
                    target == _topCatalogIndex + 1 &&
                    target < enabled.Count)
                {
                    _activeVisibleRow = 1;
                }
                else if (target == 0)
                {
                    _topCatalogIndex = 0;
                    _activeVisibleRow = 0;
                }
                else
                {
                    _topCatalogIndex = target - 1;
                    _activeVisibleRow = 1;
                }
            }
            else
            {
                if (_activeVisibleRow == 1 &&
                    target == _topCatalogIndex)
                {
                    _activeVisibleRow = 0;
                }
                else
                {
                    _topCatalogIndex =
                        Math.Max(0, target);

                    _activeVisibleRow = 0;
                }
            }

            ShowVisibleCatalogs();
        }

        private void RestoreCurrentSelection()
        {
            if (IsMenuOpen)
                return;

            var enabled =
                EnabledCatalogs();

            if (enabled.Count == 0)
                return;

            var index =
                _topCatalogIndex +
                _activeVisibleRow;

            if (index >= enabled.Count)
                index =
                    enabled.Count - 1;

            var catalog =
                enabled[index];

            var list =
                _activeVisibleRow == 0
                    ? TopCatalogList
                    : BottomCatalogList;

            if (catalog.Items.Count == 0)
                return;

            var titleIndex =
                _titleIndexes.TryGetValue(
                    catalog.Index,
                    out var savedIndex)
                    ? savedIndex
                    : 0;

            titleIndex = Math.Max(
                0,
                Math.Min(
                    titleIndex,
                    catalog.Items.Count - 1));

            list.UpdateLayout();

            var item =
                list.ContainerFromIndex(
                    titleIndex) as Control;

            if (item == null)
            {
                list.ScrollIntoView(
                    catalog.Items[titleIndex]);

                list.UpdateLayout();

                item =
                    list.ContainerFromIndex(
                        titleIndex) as Control;
            }

            item?.Focus(
                FocusState.Keyboard);
        }

        private void CatalogList_ContainerContentChanging(
            ListViewBase sender,
            ContainerContentChangingEventArgs args)
        {
            var item =
                args.ItemContainer as ListViewItem;

            if (item == null)
                return;

            item.XYFocusUp = item;
            item.XYFocusDown = item;

            item.GotFocus -= PosterItem_GotFocus;
            item.LostFocus -= PosterItem_LostFocus;

            item.GotFocus += PosterItem_GotFocus;
            item.LostFocus += PosterItem_LostFocus;

            if (args.InRecycleQueue)
                ClearPosterHighlight(item);
        }

        private void PosterItem_GotFocus(
            object sender,
            RoutedEventArgs e)
        {
            var item =
                sender as ListViewItem;

            var list =
                FindParent<ListView>(item);

            if (item == null ||
                list == null)
            {
                return;
            }

            var enabled =
                EnabledCatalogs();

            var row =
                list == TopCatalogList ? 0 : 1;

            var index =
                _topCatalogIndex + row;

            if (index >= enabled.Count)
                return;

            var catalog =
                enabled[index];

            var titleIndex =
                list.IndexFromContainer(item);

            if (titleIndex >= 0)
                _titleIndexes[catalog.Index] =
                    titleIndex;

            ClearAllPosterHighlights();
            SetPosterHighlight(item, true);
        }

        private void PosterItem_LostFocus(
            object sender,
            RoutedEventArgs e)
        {
            var item =
                sender as ListViewItem;

            if (item != null)
                ClearPosterHighlight(item);
        }

        private void SetPosterHighlight(
            ListViewItem item,
            bool selected)
        {
            var ring =
                FindDescendants<Border>(item)
                    .FirstOrDefault(x =>
                        x.Name == "PosterFocusRing");

            if (ring == null)
                return;

            ring.BorderBrush =
                selected
                    ? SelectedPosterBrush
                    : TransparentBrush;

            ring.BorderThickness =
                selected
                    ? new Thickness(3)
                    : new Thickness(0);
        }

        private void ClearPosterHighlight(
            ListViewItem item)
        {
            SetPosterHighlight(item, false);
        }

        private void ClearAllPosterHighlights()
        {
            foreach (var item in
                FindDescendants<ListViewItem>(this))
            {
                ClearPosterHighlight(item);
            }
        }

        private void UpdateFocusedTitleIndex()
        {
            var focused =
                FocusManager.GetFocusedElement()
                as DependencyObject;

            var item =
                FindParent<ListViewItem>(focused);

            var list =
                FindParent<ListView>(focused);

            if (item == null ||
                list == null)
            {
                return;
            }

            var enabled =
                EnabledCatalogs();

            var row =
                list == TopCatalogList ? 0 : 1;

            var index =
                _topCatalogIndex + row;

            if (index >= enabled.Count)
                return;

            var catalog =
                enabled[index];

            var titleIndex =
                list.IndexFromContainer(item);

            if (titleIndex >= 0)
                _titleIndexes[catalog.Index] =
                    titleIndex;
        }

        private void BoardPage_KeyDown(
            object sender,
            KeyRoutedEventArgs e)
        {
            if (IsMenuOpen)
                return;

            if (Gamepad.Gamepads.Count > 0 &&
                (e.Key == VirtualKey.Left ||
                 e.Key == VirtualKey.Right ||
                 e.Key == VirtualKey.Up ||
                 e.Key == VirtualKey.Down ||
                 e.Key == VirtualKey.GamepadDPadLeft ||
                 e.Key == VirtualKey.GamepadDPadRight ||
                 e.Key == VirtualKey.GamepadDPadUp ||
                 e.Key == VirtualKey.GamepadDPadDown))
            {
                e.Handled = true;
            }
        }

        private void CatalogList_KeyDown(
            object sender,
            KeyRoutedEventArgs e)
        {
            if (IsMenuOpen)
                return;

            if (Gamepad.Gamepads.Count > 0 &&
                (e.Key == VirtualKey.Left ||
                 e.Key == VirtualKey.Right ||
                 e.Key == VirtualKey.Up ||
                 e.Key == VirtualKey.Down ||
                 e.Key == VirtualKey.GamepadDPadLeft ||
                 e.Key == VirtualKey.GamepadDPadRight ||
                 e.Key == VirtualKey.GamepadDPadUp ||
                 e.Key == VirtualKey.GamepadDPadDown))
            {
                e.Handled = true;
            }
        }

        private void BoardPage_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                () => RestoreCurrentSelection());
        }

        private void MovePosterFocus(int amount)
        {
            var focused =
                FocusManager.GetFocusedElement()
                as DependencyObject;

            var item =
                FindParent<ListViewItem>(focused);

            var list =
                FindParent<ListView>(focused);

            if (item == null || list == null)
                return;

            var currentIndex =
                list.IndexFromContainer(item);

            if (currentIndex < 0)
                return;

            var nextIndex =
                Math.Max(
                    0,
                    Math.Min(
                        list.Items.Count - 1,
                        currentIndex + amount));

            if (nextIndex == currentIndex)
                return;

            UpdateFocusedTitleIndex();

            var nextItem =
                list.ContainerFromIndex(nextIndex)
                as Control;

            if (nextItem == null)
            {
                list.ScrollIntoView(
                    list.Items[nextIndex]);

                list.UpdateLayout();

                nextItem =
                    list.ContainerFromIndex(nextIndex)
                    as Control;
            }

            nextItem?.Focus(FocusState.Keyboard);
        }

        private void Poster_ItemClick(
            object sender,
            ItemClickEventArgs e)
        {
            if (IsMenuOpen)
                return;

            var meta =
                e.ClickedItem as MetaPreview;

            if (meta == null)
                return;

            UpdateFocusedTitleIndex();

            Frame.Navigate(
                typeof(DetailPage),
                new MetaArgs
                {
                    Type = meta.Type,
                    Id = meta.Id
                });
        }

        private void LoadDisabledCatalogs()
        {
            var value =
                ApplicationData.Current.LocalSettings.Values[
                    DisabledCatalogsKey] as string;

            if (!string.IsNullOrWhiteSpace(value))
            {
                _disabledCatalogKeys =
                    new HashSet<string>(
                        value.Split(
                            new[] { '\n' },
                            StringSplitOptions.RemoveEmptyEntries));
            }
        }

        private void SaveDisabledCatalogs()
        {
            ApplicationData.Current.LocalSettings.Values[
                DisabledCatalogsKey] =
                string.Join(
                    "\n",
                    _catalogs
                        .Where(c => !c.IsEnabled)
                        .Select(c => c.Key));
        }

        private void ShowMessage(
            string text)
        {
            Message.Text = text;
            Message.Visibility =
                Visibility.Visible;
        }

        private static T FindParent<T>(
            DependencyObject child)
            where T : DependencyObject
        {
            var parent =
                VisualTreeHelper.GetParent(child);

            while (parent != null)
            {
                if (parent is T match)
                    return match;

                parent =
                    VisualTreeHelper.GetParent(parent);
            }

            return null;
        }

        private static IEnumerable<T>
            FindDescendants<T>(
                DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
                yield break;

            for (var i = 0;
                 i < VisualTreeHelper.GetChildrenCount(root);
                 i++)
            {
                var child =
                    VisualTreeHelper.GetChild(root, i);

                if (child is T match)
                    yield return match;

                foreach (var descendant in
                    FindDescendants<T>(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}

