using System;
using System.Linq;
using Xamlio.Services;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Xamlio.Views
{
    public sealed partial class ShellPage : Page
    {
        private NavigationViewItem _lastContentItem;
        private string _pendingToastType;
        private string _pendingToastId;

        public ShellPage()
        {
            InitializeComponent();
            RequiresPointer =
                Windows.UI.Xaml.Controls.RequiresPointer.Never;
            App.SetPointerInputEnabled(false);
            NavigationCacheMode =
                NavigationCacheMode.Required;

            Loaded += OnLoaded;

            if (Window.Current?.CoreWindow != null)
                Window.Current.CoreWindow.KeyDown +=
                    CoreWindow_KeyDown;
        }

        protected override void OnNavigatedTo(
            NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            RequiresPointer =
                Windows.UI.Xaml.Controls.RequiresPointer.Never;
            App.SetPointerInputEnabled(false);
        }

        private void OnLoaded(
            object sender,
            RoutedEventArgs e)
        {
            ApplyDisplayMargins();
            App.SetPointerInputEnabled(false);

            if (Nav.SelectedItem == null &&
                Nav.MenuItems.Count > 0)
            {
                Nav.SelectedItem = Nav.MenuItems[0];
            }

            _lastContentItem = Nav.SelectedItem as NavigationViewItem;

            FocusContentAfterNavigation();

            if (!string.IsNullOrWhiteSpace(_pendingToastId))
            {
                var type = _pendingToastType;
                var id = _pendingToastId;
                _pendingToastType = null;
                _pendingToastId = null;
                Dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal,
                    () => NavigateToMetaNow(type, id));
            }
        }

        private void CoreWindow_KeyDown(
            CoreWindow sender,
            KeyEventArgs args)
        {
            if (HandleControllerKey(args.VirtualKey))
                args.Handled = true;
        }

        private bool HandleControllerKey(VirtualKey key)
        {
            // Menu/Start is handled exclusively by the Windows.Gaming.Input polling
            // in the active page. Prevent NavigationView from interpreting it
            // as its own menu/overflow command and opening a blank popup.
            if (key == VirtualKey.GamepadMenu)
                return true;

            if (key == VirtualKey.GamepadLeftShoulder)
            {
                SelectAdjacentTab(-1);
                return true;
            }

            if (key == VirtualKey.GamepadRightShoulder)
            {
                SelectAdjacentTab(1);
                return true;
            }

            return false;
        }

        public void SetEpisodeActionsVisible(bool visible)
        {
            var visibility = visible
                ? Visibility.Visible
                : Visibility.Collapsed;
            MarkWatchedAction.Visibility = visibility;
            MarkBelowAction.Visibility = visibility;
            BackgroundAction.Visibility = visibility;
            PositionEpisodeActions();
        }

        private void Nav_SizeChanged(
            object sender,
            SizeChangedEventArgs e)
        {
            PositionEpisodeActions();
        }

        private void PositionEpisodeActions()
        {
            // FooterMenuItems are laid out at the trailing edge in top mode,
            // avoiding the overflow flyout used by the main menu.
        }

        private void SelectAdjacentTab(int direction)
        {
            var items = Nav.MenuItems
                .OfType<NavigationViewItem>()
                .Where(item => item.Visibility == Visibility.Visible &&
                    item.IsEnabled)
                .ToList();

            if (items.Count == 0)
                return;

            var currentIndex = items.FindIndex(
                item => ReferenceEquals(item, Nav.SelectedItem));

            if (currentIndex < 0)
                currentIndex = 0;

            var nextIndex = currentIndex + direction;

            if (nextIndex < 0)
                nextIndex = items.Count - 1;
            else if (nextIndex >= items.Count)
                nextIndex = 0;

            Nav.SelectedItem = items[nextIndex];
        }

        public bool TryGoBack()
        {
            if (!ContentFrame.CanGoBack)
                return false;

            ContentFrame.GoBack();
            return true;
        }

        public void NavigateToMeta(string type, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            if (!IsLoaded)
            {
                _pendingToastType = type;
                _pendingToastId = id;
                AppLog.WriteLine("[Toast] Deferring title navigation until shell loaded.");
                return;
            }

            NavigateToMetaNow(type, id);
        }

        private void NavigateToMetaNow(string type, string id)
        {
            AppLog.WriteLine(
                "[Toast] Navigating to title: type=" + type + ", id=" + id + ".");

            ContentFrame.Navigate(typeof(DetailPage), new MetaArgs
            {
                Type = string.IsNullOrWhiteSpace(type) ? "series" : type,
                Id = id
            });
            ContentFrame.BackStack.Clear();
        }

        public void RestorePlaybackPath(PlayerArgs args)
        {
            var libraryItem = Nav.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(item =>
                    string.Equals(item.Tag as string, "library",
                        StringComparison.OrdinalIgnoreCase));

            if (libraryItem != null)
                Nav.SelectedItem = libraryItem;

            ContentFrame.Navigate(typeof(LibraryPage));
            ContentFrame.BackStack.Clear();

            if (args == null || string.IsNullOrWhiteSpace(args.MetaId))
                return;

            ContentFrame.Navigate(typeof(DetailPage), new MetaArgs
            {
                Type = args.Type,
                Id = args.MetaId
            });

            ContentFrame.Navigate(typeof(StreamsPage), new StreamsArgs
            {
                Type = args.Type,
                VideoId = args.VideoId,
                MetaId = args.MetaId,
                Title = args.Title,
                Background = args.Background
            });
        }

        public void NavigateFromPlayerToStreams(StreamsArgs args)
        {
            if (args == null)
                return;

            ContentFrame.Navigate(typeof(StreamsPage), args);
        }

        private void Nav_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            var tag =
                (args.SelectedItem as NavigationViewItem)?.Tag as string;

            if (tag == "action-mark-watched" ||
                tag == "action-mark-below" ||
                tag == "action-background")
            {
                var detail = ContentFrame.Content as DetailPage;
                if (detail != null)
                {
                    if (tag == "action-mark-watched")
                        detail.MarkFocusedEpisodeWatched();
                    else if (tag == "action-mark-below")
                        detail.MarkFocusedEpisodesBelow();
                    else
                        detail.CycleBackground();
                }

                RestoreContentSelection();
                return;
            }

            _lastContentItem = args.SelectedItem as NavigationViewItem;

            Type page;

            switch (tag)
            {
                case "search":
                    page = typeof(SearchPage);
                    break;
                case "library":
                    page = typeof(LibraryPage);
                    break;
                case "calendar":
                    page = typeof(CalendarPage);
                    break;
                case "settings":
                    page = typeof(SettingsPage);
                    break;
                default:
                    page = typeof(BoardPage);
                    break;
            }

            if (ContentFrame.CurrentSourcePageType != page)
            {
                ContentFrame.Navigate(page);
                ContentFrame.BackStack.Clear();
            }

            FocusContentAfterNavigation();
        }

        private void RestoreContentSelection()
        {
            var item = _lastContentItem ?? Nav.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(candidate =>
                    candidate.Tag as string == "library");
            if (item != null)
                Nav.SelectedItem = item;
        }

        private async void FocusContentAfterNavigation()
        {
            await Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal,
                () =>
                {
                    var focusable =
                        FindFirstFocusable(ContentFrame);

                    focusable?.Focus(
                        FocusState.Programmatic);
                });
        }

        private static Control FindFirstFocusable(
            DependencyObject root)
        {
            if (root == null)
                return null;

            if (root is Control control &&
                control.IsEnabled &&
                control.IsTabStop &&
                control.Visibility == Visibility.Visible)
            {
                return control;
            }

            for (var i = 0;
                 i < VisualTreeHelper.GetChildrenCount(root);
                 i++)
            {
                var result = FindFirstFocusable(
                    VisualTreeHelper.GetChild(root, i));

                if (result != null)
                    return result;
            }

            return null;
        }

        public static void ShowPlayer(PlayerArgs args)
        {
            App.RootFrame.Navigate(
                typeof(PlayerPage),
                args);
        }

        public void ApplyDisplayMargins()
        {
            Nav.Margin = AppServices.Settings.UseTvSafeMargin
                ? new Thickness(48, 27, 48, 27)
                : new Thickness(0);
        }
    }
}
