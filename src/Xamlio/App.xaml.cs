using Xamlio.Services;
using Xamlio.Views;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Xamlio
{
    sealed partial class App : Application
    {
        private static bool _pointerHandlersConfigured;
        private static bool _pointerInputEnabled = false;

        public App()
        {
            InitializeComponent();
            RequiresPointerMode =
                ApplicationRequiresPointerMode.WhenRequested;
            ApplicationViewScaling.TrySetDisableLayoutScaling(true);
            Suspending += OnSuspending;
        }

        public static Frame RootFrame
        {
            get { return Window.Current.Content as Frame; }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            ApplicationView.GetForCurrentView().SetDesiredBoundsMode(
                ApplicationViewBoundsMode.UseCoreWindow);

            var rootFrame = RootFrame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
                SystemNavigationManager.GetForCurrentView().BackRequested +=
                    OnBackRequested;
            }

            ConfigurePointerHandlers();

            if (!e.PrelaunchActivated)
            {
                if (rootFrame.Content == null)
                    rootFrame.Navigate(typeof(ShellPage), e.Arguments);

                Window.Current.Activate();
            }
        }

        public static void SetPointerInputEnabled(bool enabled)
        {
            _pointerInputEnabled = enabled;

            var coreWindow = Window.Current?.CoreWindow;
            if (coreWindow == null)
                return;

            coreWindow.PointerCursor = enabled
                ? new CoreCursor(CoreCursorType.Arrow, 0)
                : null;
        }

        private static void ConfigurePointerHandlers()
        {
            if (_pointerHandlersConfigured)
                return;

            var coreWindow = Window.Current?.CoreWindow;
            if (coreWindow == null)
                return;

            _pointerHandlersConfigured = true;

            coreWindow.PointerPressed += BlockPointer;
            coreWindow.PointerReleased += BlockPointer;
            coreWindow.PointerMoved += BlockPointer;
            coreWindow.PointerWheelChanged += BlockPointer;
            coreWindow.PointerCursor = null;
        }

        private static void BlockPointer(
            CoreWindow sender,
            PointerEventArgs e)
        {
            if (!_pointerInputEnabled)
                e.Handled = true;
        }

        private void OnBackRequested(
            object sender,
            BackRequestedEventArgs e)
        {
            var root = RootFrame;
            if (root == null)
                return;

            if (root.Content is PlayerPage player)
            {
                var playerArgs = player.CurrentArgs;
                player.HandleBackNavigation();

                if (root.Content is ShellPage shell)
                    shell.RestorePlaybackPath(playerArgs);

                e.Handled = true;
            }
            else if (root.Content is ShellPage shell && shell.TryGoBack())
            {
                e.Handled = true;
            }
            else if (root.CanGoBack)
            {
                root.GoBack();
                e.Handled = true;
            }
        }

        private void OnSuspending(
            object sender,
            SuspendingEventArgs e)
        {
            var deferral =
                e.SuspendingOperation.GetDeferral();

            DisplayModeService.RestoreDefault();
            deferral.Complete();
        }
    }
}
