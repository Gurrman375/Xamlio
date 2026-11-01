using Stremio.Core.Addons;
using Stremio.Core.Api;
using Xamlio.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Gaming.Input;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Xamlio.Views
{
    public sealed class EpisodeRow
    {
        public Video Video { get; }
        public string DisplayTitle => Video.DisplayTitle;
        public string Released => Video.Released;
        public string StatusText { get; set; }
        public string MarkWatchedText =>
            IsWatched ? "Mark as unwatched" : "Mark as watched";
        public bool IsWatched { get; set; }
        public bool CanMarkWatched => true;
        public Visibility StatusVisibility =>
            string.IsNullOrWhiteSpace(StatusText)
                ? Visibility.Collapsed
                : Visibility.Visible;
        public Brush StatusBrush =>
            IsWatched
                ? new SolidColorBrush(Color.FromArgb(255, 192, 32, 255))
                : new SolidColorBrush(Colors.White);

        public EpisodeRow(Video video)
        {
            Video = video;
        }
    }

    public sealed partial class DetailPage : Page
    {
        private MetaArgs _args;
        private MetaDetail _meta;
        private Dictionary<int, List<Video>> _seasons = new Dictionary<int, List<Video>>();
        private LibraryItem _libraryItem;
        private readonly HashSet<string> _watchedVideoIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _gamepadTimer;
        private bool _xButtonHeld;

        public DetailPage()
        {
            InitializeComponent();
            _gamepadTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _gamepadTimer.Tick += GamepadTimer_Tick;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            _xButtonHeld = false;
            _gamepadTimer.Start();
            _args = (MetaArgs)e.Parameter;
            try
            {
                await AppServices.EnsureAddonsLoadedAsync();
                _meta = await AppServices.Addons.GetMetaAsync(_args.Type, _args.Id);
            }
            catch (Exception)
            {
                _meta = null;
            }

            Loading.IsActive = false;
            Loading.Visibility = Visibility.Collapsed;

            if (_meta == null)
            {
                Title.Text = "Couldn't load details";
                return;
            }

            Title.Text = _meta.Name ?? "";
            Info.Text = string.Join("   ·   ", new[] { _meta.ReleaseInfo, _meta.Runtime,
                _meta.ImdbRating != null ? "IMDb " + _meta.ImdbRating : null, string.Join(", ", _meta.Genres ?? new List<string>()) }
                .Where(s => !string.IsNullOrEmpty(s)));
            Description.Text = _meta.Description ?? "";
            SetImage(Poster, _meta.Poster);
            SetImage(Backdrop, _meta.Background);

            await LoadWatchStateAsync();

            var videos = _meta.Videos ?? new List<Video>();
            if (_args.Type == "movie" || videos.Count == 0)
            {
                PlayButton.Visibility = Visibility.Visible;
                PlayButton.Focus(FocusState.Programmatic);
                return;
            }

            // Season 0 is specials; list it last.
            _seasons = videos.GroupBy(v => v.Season ?? 0).ToDictionary(g => g.Key, g => g.OrderBy(v => v.Episode ?? 0).ToList());
            SeasonPicker.ItemsSource = _seasons.Keys.OrderBy(s => s == 0 ? int.MaxValue : s)
                .Select(s => s == 0 ? "Specials" : "Season " + s).ToList();
            EpisodesPanel.Visibility = Visibility.Visible;
            SeasonPicker.SelectedIndex = 0;
            SeasonPicker.Focus(FocusState.Programmatic);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _gamepadTimer.Stop();
            base.OnNavigatedFrom(e);
        }

        private void SeasonPicker_DropDownClosed(object sender, object e)
        {
            FocusFirstEpisode();
        }

        private void SeasonPicker_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if ((e.Key == VirtualKey.GamepadB || e.Key == VirtualKey.Escape) &&
                SeasonPicker.IsDropDownOpen)
            {
                SeasonPicker.IsDropDownOpen = false;
                FocusFirstEpisode();
                e.Handled = true;
            }
        }

        private void FocusFirstEpisode()
        {
            if (Episodes.Items.Count == 0)
                return;

            Episodes.UpdateLayout();
            var item = Episodes.ContainerFromIndex(0) as Control;

            if (item == null)
            {
                Episodes.ScrollIntoView(Episodes.Items[0]);
                Episodes.UpdateLayout();
                item = Episodes.ContainerFromIndex(0) as Control;
            }

            item?.Focus(FocusState.Programmatic);
        }

        private static void SetImage(Image image, string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                image.Source = new BitmapImage(uri);
        }

        private void SeasonPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var label = SeasonPicker.SelectedItem as string;
            if (label == null)
                return;
            var season = label == "Specials" ? 0 : int.Parse(label.Substring("Season ".Length));
            Episodes.ItemsSource = _seasons[season]
                .Select(BuildEpisodeRow)
                .ToList();
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(
                typeof(StreamsPage),
                new StreamsArgs
                {
                    Type = _args.Type,
                    VideoId = _meta.Id,
                    MetaId = _meta.Id,
                    Title = _meta.Name,
                    Background = _meta.Background,
                    WatchStatus = ""
                });
        }

        private void Episodes_ItemClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as EpisodeRow;
            var video = row?.Video;
            if (video == null)
                return;

            var title =
                _meta.Name +
                " S" +
                (video.Season ?? 0).ToString("00") +
                "E" +
                (video.Episode ?? 0).ToString("00");

            Frame.Navigate(
                typeof(StreamsPage),
                new StreamsArgs
                {
                    Type = _args.Type,
                    VideoId = video.Id,
                    MetaId = _meta.Id,
                    Title = title,
                    Background = _meta.Background,
                    WatchStatus = row?.StatusText ?? ""
                });
        }

        private async void MarkWatched_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.Tag as EpisodeRow;
            await MarkEpisodeWatchedAsync(row);
        }

        private async Task MarkEpisodeWatchedAsync(EpisodeRow row)
        {
            if (row?.Video == null)
                return;

            if (_libraryItem == null)
                await LoadWatchStateAsync();

            if (_libraryItem == null)
                return;

            try
            {
                var shouldBeWatched = !row.IsWatched;
                var watched = WatchStateHelper.SetVideo(
                    _libraryItem.State?.Watched,
                    WatchStateHelper.OrderEpisodes(_meta.Videos),
                    row.Video,
                    shouldBeWatched);
                await AppServices.Api.UpdateLibraryWatchedAsync(
                    AppServices.Settings.AuthKey,
                    _libraryItem.Id,
                    watched,
                    shouldBeWatched);

                if (shouldBeWatched)
                    _watchedVideoIds.Add(row.Video.Id);
                else
                    _watchedVideoIds.Remove(row.Video.Id);
                if (_libraryItem.State == null)
                    _libraryItem.State = new LibraryItemState();
                _libraryItem.State.Watched = watched;
                RefreshCurrentSeason();
            }
            catch (Exception ex)
            {
                // Keep the episode unchanged when the server update fails.
                Debug.WriteLine("Mark watched failed: " + ex.Message);
            }
        }

        private void GamepadTimer_Tick(object sender, object e)
        {
            var gamepad = Gamepad.Gamepads.FirstOrDefault();
            if (gamepad == null)
                return;

            var buttons = gamepad.GetCurrentReading().Buttons;
            var xPressed = (buttons & GamepadButtons.X) != 0;

            if (xPressed && !_xButtonHeld)
            {
                var focused = FocusManager.GetFocusedElement() as DependencyObject;
                var item = FindParent<ListViewItem>(focused);
                var row = item?.DataContext as EpisodeRow ??
                          item?.Content as EpisodeRow;
                if (row != null)
                    _ = MarkEpisodeWatchedAsync(row);
            }

            _xButtonHeld = xPressed;
        }

        private async Task LoadWatchStateAsync()
        {
            _libraryItem = null;
            _watchedVideoIds.Clear();

            if (!AppServices.Settings.IsLoggedIn ||
                string.IsNullOrWhiteSpace(_args?.Id))
                return;

            try
            {
                var items = await AppServices.Api.GetLibraryAsync(
                    AppServices.Settings.AuthKey);
                _libraryItem = items.FirstOrDefault(item =>
                    string.Equals(item.Id, _args.Id,
                        StringComparison.OrdinalIgnoreCase));

                if (_libraryItem?.State != null)
                    DecodeWatchedValue(_libraryItem.State.Watched);
            }
            catch (Exception)
            {
                _libraryItem = null;
            }
        }

        private EpisodeRow BuildEpisodeRow(Video video)
        {
            var row = new EpisodeRow(video)
            {
                IsWatched = _watchedVideoIds.Contains(video.Id)
            };

            var state = _libraryItem?.State;
            if (!row.IsWatched &&
                state != null &&
                string.Equals(state.VideoId, video.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                state.TimeOffset > 0 &&
                state.Duration > 0 &&
                state.TimeOffset < state.Duration * 0.95)
            {
                row.StatusText = "In progress";
            }
            else if (row.IsWatched)
            {
                row.StatusText = "Watched";
            }

            return row;
        }

        private void RefreshCurrentSeason()
        {
            var label = SeasonPicker.SelectedItem as string;
            if (label == null)
                return;

            var season = label == "Specials"
                ? 0
                : int.Parse(label.Substring("Season ".Length));

            Episodes.ItemsSource = _seasons[season]
                .Select(BuildEpisodeRow)
                .ToList();
        }

        private List<Video> OrderedVideos()
        {
            return (_meta?.Videos ?? new List<Video>())
                .Where(video => video.Season.HasValue && video.Episode.HasValue)
                .OrderBy(video => video.Season.Value)
                .ThenBy(video => video.Episode.Value)
                .ToList();
        }

        private void DecodeWatchedValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            try
            {
                var parts = value.Split(new[] { ':' }, 5);
                if (parts.Length != 5 || !int.TryParse(parts[3], out var bitCount))
                    return;

                var compressed = Convert.FromBase64String(parts[4]);
                var deflateBytes = RemoveZlibWrapper(compressed);
                using (var input = new MemoryStream(deflateBytes))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    deflate.CopyTo(output);
                    var bytes = output.ToArray();
                    var videos = OrderedVideos();

                    for (var i = 0; i < bitCount && i < videos.Count; i++)
                    {
                        if (i / 8 < bytes.Length &&
                            (bytes[i / 8] & (1 << (i % 8))) != 0)
                            _watchedVideoIds.Add(videos[i].Id);
                    }
                }
            }
            catch (Exception)
            {
                // Invalid or legacy watched data is treated as unwatched.
            }
        }

        private string BuildWatchedValue(
            Video selectedVideo,
            bool shouldBeWatched)
        {
            var videos = OrderedVideos();
            if (!videos.Any())
                return selectedVideo.Id + ":1:" +
                    CompressBits(new byte[] { 1 });

            var watched = new HashSet<string>(
                _watchedVideoIds,
                StringComparer.OrdinalIgnoreCase);
            if (shouldBeWatched)
                watched.Add(selectedVideo.Id);
            else
                watched.Remove(selectedVideo.Id);

            var bitCount = videos.Count;
            var bytes = new byte[(bitCount + 7) / 8];
            for (var i = 0; i < videos.Count; i++)
            {
                if (watched.Contains(videos[i].Id))
                    bytes[i / 8] |= (byte)(1 << (i % 8));
            }

            return selectedVideo.Id + ":" + bitCount + ":" +
                CompressBits(bytes);
        }

        private static string CompressBits(byte[] bytes)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(
                    output,
                    CompressionMode.Compress,
                    true))
                {
                    deflate.Write(bytes, 0, bytes.Length);
                }

                var deflateBytes = output.ToArray();
                var zlib = new byte[deflateBytes.Length + 6];
                zlib[0] = 0x78;
                zlib[1] = 0x9C;
                Buffer.BlockCopy(deflateBytes, 0, zlib, 2,
                    deflateBytes.Length);

                var checksum = Adler32(bytes);
                zlib[zlib.Length - 4] = (byte)(checksum >> 24);
                zlib[zlib.Length - 3] = (byte)(checksum >> 16);
                zlib[zlib.Length - 2] = (byte)(checksum >> 8);
                zlib[zlib.Length - 1] = (byte)checksum;

                return Convert.ToBase64String(zlib);
            }
        }

        private static byte[] RemoveZlibWrapper(byte[] bytes)
        {
            if (bytes.Length >= 6 && bytes[0] == 0x78)
            {
                var raw = new byte[bytes.Length - 6];
                Buffer.BlockCopy(bytes, 2, raw, 0, raw.Length);
                return raw;
            }

            return bytes;
        }

        private static uint Adler32(byte[] bytes)
        {
            const uint mod = 65521;
            uint a = 1;
            uint b = 0;

            foreach (var value in bytes)
            {
                a = (a + value) % mod;
                b = (b + a) % mod;
            }

            return (b << 16) | a;
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
