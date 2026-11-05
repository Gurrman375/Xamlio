using Stremio.Core.Addons;
using Stremio.Core.Api;
using Stremio.Core.Streaming;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Xamlio.Services;

namespace Xamlio.Views
{
    public sealed partial class StreamsPage : Page
    {
        private StreamsArgs _args;

        private readonly List<Stream> _allStreams =
            new List<Stream>();

        private readonly ObservableCollection<StreamListItem> _visibleStreams =
            new ObservableCollection<StreamListItem>();

        private readonly ObservableCollection<string> _sources =
            new ObservableCollection<string>();

        private CancellationTokenSource _loadCts;
        private bool _streamsReceived;
        private readonly DispatcherTimer _gamepadTimer;
        private bool _xButtonHeld;
        private LibraryItem _libraryItem;
        private List<Video> _episodeVideos = new List<Video>();
        private HashSet<string> _watchedVideoIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public StreamsPage()
        {
            InitializeComponent();

            StreamList.ItemsSource = _visibleStreams;
            SourceFilter.ItemsSource = _sources;

            _sources.Add("All");
            SourceFilter.SelectedIndex = 0;
            SourceFilter.IsTabStop = false;
            StreamList.IsTabStop = false;

            _gamepadTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _gamepadTimer.Tick += GamepadTimer_Tick;

            UpdateStreamWarningVisibility();
        }

        protected override async void OnNavigatedTo(
            NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _xButtonHeld = false;
            _gamepadTimer.Start();

            _args = (StreamsArgs)e.Parameter;
            Header.Text = _args.Title ?? "";

            if (!string.IsNullOrWhiteSpace(_args.WatchStatus))
            {
                EpisodeWatchStatus.Text = _args.WatchStatus;
                EpisodeWatchStatus.Visibility = Visibility.Visible;
            }
            else
            {
                EpisodeWatchStatus.Text = "Not watched";
                EpisodeWatchStatus.Visibility = Visibility.Visible;
            }

            SetImage(Backdrop, _args.Background);

            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();

            _allStreams.Clear();
            _visibleStreams.Clear();

            _sources.Clear();
            _sources.Add("All");
            SourceFilter.SelectedIndex = 0;
            SourceFilter.IsTabStop = false;
            StreamList.IsTabStop = false;
            _streamsReceived = false;

            Loading.IsActive = true;
            Loading.Visibility = Visibility.Visible;

            try
            {
                await AppServices.EnsureAddonsLoadedAsync();

                await LoadEpisodeWatchStateAsync();

                IProgress<List<Stream>> progress =
                    new Progress<List<Stream>>(batch =>
                    {
                        foreach (var stream in batch)
                        {
                            _allStreams.Add(stream);

                            var source =
                                string.IsNullOrWhiteSpace(stream.AddonName)
                                    ? "Unknown"
                                    : stream.AddonName;

                            if (!_sources.Contains(source))
                                _sources.Add(source);
                        }

                        ApplyStreamFilter();
                        UpdateStreamStatus();
                        UpdateStreamWarningVisibility();

                        if (!_streamsReceived && _visibleStreams.Count > 0)
                        {
                            _streamsReceived = true;
                            SourceFilter.IsTabStop = true;
                            StreamList.IsTabStop = true;

                            _ = Dispatcher.RunAsync(
                                CoreDispatcherPriority.Normal,
                                FocusFirstStream);
                        }
                    });

                await AppServices.Addons.GetStreamsIncrementalAsync(
                    _args.Type,
                    _args.VideoId,
                    progress,
                    _loadCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Status.Text =
                    "Couldn't load streams: " + ex.Message;
            }
            finally
            {
                Loading.IsActive = false;
                Loading.Visibility = Visibility.Collapsed;
            }

            ApplyStreamFilter();
            UpdateStreamStatus();
            UpdateStreamWarningVisibility();

            if (_visibleStreams.Count > 0)
            {
                _streamsReceived = true;
                SourceFilter.IsTabStop = true;
                StreamList.IsTabStop = true;
                FocusFirstStream();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _gamepadTimer.Stop();
            _loadCts?.Cancel();
            base.OnNavigatedFrom(e);
        }

        private async void StreamList_ItemClick(
            object sender,
            ItemClickEventArgs e)
        {
            var item = e.ClickedItem as StreamListItem;

            if (item == null)
                return;

            if (item.RequiresServer)
            {
                await FlashServerWarningAsync();
                return;
            }

            var stream = item.Stream;

            var server =
                AppServices.Settings.StreamingServerUrl;

            var resolved =
                StreamResolver.Resolve(stream, server);

            if (resolved == null)
            {
                if (Uri.TryCreate(
                    stream.ExternalUrl,
                    UriKind.Absolute,
                    out var externalUri))
                {
                    await Launcher.LaunchUriAsync(externalUri);
                }

                return;
            }

            // Addon subtitles are loaded from the player only when requested.
            // This avoids unnecessary subtitle requests and prevents LibVLC from
            // attaching a large number of subtitle tracks at startup.
            var subtitles =
                new List<Subtitle>(resolved.Subtitles ?? new List<Subtitle>());

            var text =
                (stream.Name ?? "") + " " +
                (stream.Title ?? "") + " " +
                (stream.Description ?? "");

            text = text.ToUpperInvariant();

            ShellPage.ShowPlayer(new PlayerArgs
            {
                Url = resolved.Url,
                VideoId = _args.VideoId,
                Type = _args.Type,
                MetaId = _args.MetaId,
                Headers = resolved.Headers,
                Title = _args.Title,
                Background = _args.Background,
                AddonName = stream.AddonName,
                AddonUrl = stream.AddonUrl,
                StreamTitle = stream.Label,
                Subtitles = subtitles,
                LooksHdr =
                    text.Contains("HDR") ||
                    text.Contains("DOLBY VISION") ||
                    text.Contains(" DV")
            });
        }

        private async Task FlashServerWarningAsync()
        {
            if (!string.IsNullOrWhiteSpace(
                AppServices.Settings.StreamingServerUrl))
            {
                return;
            }

            for (int i = 0; i < 3; i++)
            {
                StreamWarning.Visibility =
                    Visibility.Collapsed;

                await Task.Delay(120);

                StreamWarning.Visibility =
                    Visibility.Visible;

                await Task.Delay(180);
            }

            StreamWarning.Visibility =
                Visibility.Visible;
        }

        private async void JustWatchButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            var title = _args?.Title;
            if (string.IsNullOrWhiteSpace(title))
                return;

            var url =
                "https://www.justwatch.com/us/search?q=" +
                Uri.EscapeDataString(title);

            await Launcher.LaunchUriAsync(
                new Uri(url, UriKind.Absolute));
        }

        private async void MarkWatchedButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await ToggleCurrentWatchStateAsync();
        }

        private void GamepadTimer_Tick(object sender, object e)
        {
            var gamepad = Gamepad.Gamepads.FirstOrDefault();
            if (gamepad == null)
                return;

            var buttons = gamepad.GetCurrentReading().Buttons;
            var xPressed = (buttons & GamepadButtons.X) != 0;

            if (xPressed && !_xButtonHeld)
                _ = ToggleCurrentWatchStateAsync();

            _xButtonHeld = xPressed;
        }

        private async Task LoadEpisodeWatchStateAsync()
        {
            MarkWatchedButton.Visibility = Visibility.Collapsed;
            EpisodeWatchStatus.Visibility = Visibility.Collapsed;
            _libraryItem = null;
            _episodeVideos = new List<Video>();
            _watchedVideoIds.Clear();

            if (!string.Equals(
                    _args?.Type,
                    "series",
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(_args?.MetaId) ||
                !AppServices.Settings.IsLoggedIn)
            {
                return;
            }

            try
            {
                var items = await AppServices.Api.GetLibraryAsync(
                    AppServices.Settings.AuthKey);
                _libraryItem = items.FirstOrDefault(item =>
                    string.Equals(
                        item.Id,
                        _args.MetaId,
                        StringComparison.OrdinalIgnoreCase));

                var meta = await AppServices.Addons.GetMetaAsync(
                    _args.Type,
                    _args.MetaId);
                _episodeVideos = WatchStateHelper.OrderEpisodes(
                    meta?.Videos);

                _watchedVideoIds = WatchStateHelper.Decode(
                    _libraryItem?.State?.Watched,
                    _episodeVideos);

                MarkWatchedButton.Visibility = Visibility.Visible;
                UpdateEpisodeWatchStatus();
            }
            catch (Exception)
            {
                // Stream loading can continue even if watch state cannot be loaded.
            }
        }

        private async Task ToggleCurrentWatchStateAsync()
        {
            if (_libraryItem == null ||
                _episodeVideos.Count == 0 ||
                string.IsNullOrWhiteSpace(_args?.VideoId))
            {
                return;
            }

            var video = _episodeVideos.FirstOrDefault(item =>
                string.Equals(
                    item.Id,
                    _args.VideoId,
                    StringComparison.OrdinalIgnoreCase));
            if (video == null)
                return;

            var isWatched = _watchedVideoIds.Contains(video.Id);
            var watched = WatchStateHelper.SetVideo(
                _libraryItem.State?.Watched,
                _episodeVideos,
                video,
                !isWatched);

            try
            {
                await AppServices.Api.UpdateLibraryWatchedAsync(
                    AppServices.Settings.AuthKey,
                    _libraryItem.Id,
                    watched,
                    !isWatched);

                if (isWatched)
                    _watchedVideoIds.Remove(video.Id);
                else
                    _watchedVideoIds.Add(video.Id);

                if (_libraryItem.State == null)
                    _libraryItem.State = new LibraryItemState();
                _libraryItem.State.Watched = watched;
                UpdateEpisodeWatchStatus();
            }
            catch (Exception ex)
            {
                // Keep the current state if the server update fails.
                AppLog.WriteLine("Stream watch toggle failed: " + ex.Message);
            }
        }

        private void UpdateEpisodeWatchStatus()
        {
            var isWatched = _watchedVideoIds.Contains(_args.VideoId);
            var state = _libraryItem?.State;
            var inProgress = !isWatched &&
                string.Equals(
                    state?.VideoId,
                    _args.VideoId,
                    StringComparison.OrdinalIgnoreCase) &&
                state.TimeOffset > 0 &&
                state.Duration > 0 &&
                state.TimeOffset < state.Duration * 0.95;

            EpisodeWatchStatus.Text = isWatched
                ? "Watched"
                : inProgress
                    ? "In progress"
                    : "Not watched";
            EpisodeWatchStatus.Visibility = Visibility.Visible;
            MarkWatchedButtonText.Text = isWatched
                ? "Mark as unwatched"
                : "Mark as watched";
        }

        private void UpdateStreamWarningVisibility()
        {
            StreamWarning.Visibility =
                string.IsNullOrWhiteSpace(
                    AppServices.Settings.StreamingServerUrl)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private static bool RequiresServer(
            Stream stream,
            string server)
        {
            return string.IsNullOrWhiteSpace(server) &&
                   !StreamResolver.IsPlayable(stream, server) &&
                   string.IsNullOrWhiteSpace(
                       stream.ExternalUrl);
        }

        private static int GetQualityRank(Stream stream)
        {
            var text = string.Join(
                " ",
                stream.Name ?? "",
                stream.Title ?? "",
                stream.Description ?? "",
                stream.BehaviorHints?.Filename ?? "")
                .ToUpperInvariant();

            if (text.Contains("2160") ||
                text.Contains("4K") ||
                text.Contains("UHD"))
            {
                return 4;
            }

            if (text.Contains("1080"))
                return 3;

            if (text.Contains("720"))
                return 2;

            if (text.Contains("480"))
                return 1;

            return 0;
        }

        private void SourceFilter_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            ApplyStreamFilter();
        }

        private void SourceFilter_DropDownClosed(object sender, object e)
        {
            FocusFirstStream();
        }

        private void SourceFilter_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if ((e.Key == VirtualKey.GamepadB || e.Key == VirtualKey.Escape) &&
                SourceFilter.IsDropDownOpen)
            {
                SourceFilter.IsDropDownOpen = false;
                FocusFirstStream();
                e.Handled = true;
            }
        }

        private void FocusFirstStream()
        {
            if (_visibleStreams.Count == 0)
                return;

            StreamList.UpdateLayout();
            var item = StreamList.ContainerFromIndex(0) as Control;

            if (item == null)
            {
                StreamList.ScrollIntoView(_visibleStreams[0]);
                StreamList.UpdateLayout();
                item = StreamList.ContainerFromIndex(0) as Control;
            }

            item?.Focus(FocusState.Programmatic);
        }

        private void ApplyStreamFilter()
        {
            var selectedSource =
                SourceFilter.SelectedItem as string;

            var server =
                AppServices.Settings.StreamingServerUrl;

            IEnumerable<Stream> streams =
                _allStreams.AsEnumerable();

            if (!string.IsNullOrEmpty(selectedSource) &&
                selectedSource != "All")
            {
                streams = streams.Where(stream =>
                    string.Equals(
                        stream.AddonName,
                        selectedSource,
                        StringComparison.OrdinalIgnoreCase));
            }

            var sorted = streams
                .OrderByDescending(stream =>
                    StreamResolver.IsPlayable(
                        stream,
                        server))
                .ThenBy(
                    stream => stream.AddonName ?? "Unknown",
                    StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(GetQualityRank)
                .ThenBy(
                    stream => stream.Label ?? "",
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            _visibleStreams.Clear();

            foreach (var stream in sorted)
            {
                _visibleStreams.Add(
                    new StreamListItem(
                        stream,
                        RequiresServer(stream, server)));
            }
        }

        private void UpdateStreamStatus()
        {
            var server =
                AppServices.Settings.StreamingServerUrl;

            var playable = _allStreams.Count(stream =>
                StreamResolver.IsPlayable(stream, server));

            var requiresServer = _allStreams.Count(stream =>
                RequiresServer(stream, server));

            var informational =
                _allStreams.Count -
                playable -
                requiresServer;

            if (_allStreams.Count == 0)
            {
                Status.Text = "No streams found.";
                return;
            }

            Status.Text =
                playable + " playable stream(s)";

            if (requiresServer > 0)
            {
                Status.Text +=
                    " • " +
                    requiresServer +
                    " server-required stream(s)";
            }

            if (informational > 0)
            {
                Status.Text +=
                    " • " +
                    informational +
                    " addon information/link item(s)";
            }
        }

        private static void SetImage(
            Image image,
            string url)
        {
            image.Source = null;

            if (Uri.TryCreate(
                url,
                UriKind.Absolute,
                out var uri))
            {
                image.Source = new BitmapImage(uri);
            }
        }
    }

    public sealed class StreamListItem
    {
        public Stream Stream { get; }

        public bool RequiresServer { get; }

        public string Name =>
            Stream.Name ?? "";

        public string Label =>
            Stream.Label ?? "";

        public string AddonName =>
            Stream.AddonName ?? "Unknown";

        public string LockIcon =>
            RequiresServer ? "🔒" : "";

        public Visibility LockVisibility =>
            RequiresServer
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Brush NameBrush =>
            RequiresServer
                ? new SolidColorBrush(Colors.Red)
                : GetBrush("AccentPurpleBrush");

        public Brush TextBrush =>
            RequiresServer
                ? new SolidColorBrush(Colors.Red)
                : GetBrush("ApplicationForegroundThemeBrush");

        public Brush MutedBrush =>
            RequiresServer
                ? new SolidColorBrush(Colors.Red)
                : GetBrush("MutedTextBrush");

        public StreamListItem(
            Stream stream,
            bool requiresServer)
        {
            Stream = stream;
            RequiresServer = requiresServer;
        }

        private static Brush GetBrush(
            string resourceName)
        {
            var resources =
                Application.Current.Resources;

            if (resources.ContainsKey(resourceName) &&
                resources[resourceName] is Brush brush)
            {
                return brush;
            }

            return new SolidColorBrush(Colors.White);
        }
    }
}
