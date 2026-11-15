using System;
using System.Linq;
using Xamlio.Services;
using Xamlio.Views;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Core;
using Windows.UI.Notifications;
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
            var settings = AppServices.Settings;
            if (!settings.LoggingEnabled)
                settings.ShowMemoryUsage = false;
            AppLog.SetEnabled(settings.LoggingEnabled);
            AppLog.Initialize();
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
            var rootFrame = EnsureRootFrame();
            AppLog.WriteLine(
                "[Toast] Launch arguments: " + (e.Arguments ?? "<null>") + ".");
            ReleaseReminderService.RefreshScheduledNotifications();

            if (!e.PrelaunchActivated)
            {
                if (rootFrame.Content == null)
                    rootFrame.Navigate(typeof(ShellPage), e.Arguments);

                Window.Current.Activate();

                // Some Xbox builds deliver toast activation through the
                // launch callback instead of OnActivated. Handle both paths.
                HandleToastActivation(e.Arguments);
            }
        }

        protected override void OnActivated(IActivatedEventArgs e)
        {
            base.OnActivated(e);

            AppLog.WriteLine("[Toast] Activation received: " + e.Kind + ".");

            if (e.Kind != ActivationKind.ToastNotification)
                return;

            var toastArgs = e as ToastNotificationActivatedEventArgs;
            HandleToastActivation(toastArgs?.Argument);
        }

        public static void HandleToastActivation(string argument)
        {
            var app = Current as App;
            if (app == null)
                return;

            AppLog.WriteLine(
                "[Toast] Argument: " + (argument ?? "<null>") + ".");
            var navigation = ParseToastNavigation(argument);
            if (navigation == null)
            {
                AppLog.WriteLine("[Toast] No valid title navigation was found.");
                return;
            }

            var rootFrame = app.EnsureRootFrame();
            app.NavigateFromToast(navigation, rootFrame);
        }

        private void NavigateFromToast(
            ToastNavigation navigation,
            Frame rootFrame)
        {
            if (rootFrame.Content == null)
                rootFrame.Navigate(typeof(ShellPage));

            Window.Current.Activate();

            var shell = rootFrame.Content as ShellPage;
            if (shell == null)
            {
                AppLog.WriteLine("[Toast] Shell was not created.");
                return;
            }

            shell.NavigateToMeta(navigation.Type, navigation.Id);
        }

        private Frame EnsureRootFrame()
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
            return rootFrame;
        }

        private static ToastNavigation ParseToastNavigation(string argument)
        {
            if (string.IsNullOrWhiteSpace(argument))
                return null;

            var values = argument.Split('&')
                .Select(part => part.Split(new[] { '=' }, 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(
                    parts => parts[0],
                    parts => Uri.UnescapeDataString(parts[1]),
                    StringComparer.OrdinalIgnoreCase);
            string type;
            string id;
            if (!values.TryGetValue("type", out type) ||
                !values.TryGetValue("id", out id) ||
                string.IsNullOrWhiteSpace(id))
                return null;

            AppLog.WriteLine(
                "[Toast] Parsed title navigation: type=" + type +
                ", id=" + id + ".");

            return new ToastNavigation
            {
                Type = type,
                Id = id
            };
        }

        private sealed class ToastNavigation
        {
            public string Type { get; set; }
            public string Id { get; set; }
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
