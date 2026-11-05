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

            FocusContentAfterNavigation();
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

        private void SelectAdjacentTab(int direction)
        {
            var items = Nav.MenuItems
                .OfType<NavigationViewItem>()
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

        public void NavigateFromPlayerToEpisodeList(PlayerArgs args)
        {
            if (args == null || string.IsNullOrWhiteSpace(args.MetaId))
                return;

            ContentFrame.Navigate(
                typeof(DetailPage),
                new MetaArgs
                {
                    Type = args.Type,
                    Id = args.MetaId
                });
        }

        private void Nav_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            var tag =
                (args.SelectedItem as NavigationViewItem)?.Tag as string;

            Type page;

            switch (tag)
            {
                case "search":
                    page = typeof(SearchPage);
                    break;
                case "library":
                    page = typeof(LibraryPage);
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
