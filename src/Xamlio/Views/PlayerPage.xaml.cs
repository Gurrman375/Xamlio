using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Xamlio.Services;
using Windows.System.Display;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using Windows.Gaming.Input;


namespace Xamlio.Views
{
    public sealed partial class PlayerPage : Page
    {
        private DispatcherTimer _gamepadTimer;
        private GamepadButtons _previousButtons;
        private double _previousLeftTrigger;
        private double _previousRightTrigger;
        private static readonly TimeSpan OverlayTimeout = TimeSpan.FromSeconds(4);
        private double _previousThumbstickX;
        private bool _suppressGamepadA;

        private readonly DispatcherTimer _overlayTimer = new DispatcherTimer { Interval = OverlayTimeout };
        private readonly DisplayRequest _displayRequest = new DisplayRequest();
        private PlayerArgs _args;
        private LibVLC _libVlc;
        private MediaPlayer _player;
        private bool _displayMatched;
        private long _lastSavedTime = -1;
        private long _lastServerSavedTime = -1;
        private bool _serverSaveInProgress;
        private bool _pendingServerSync;
        private long _pendingServerTime;
        private long _pendingServerDuration;
        private bool _pendingServerForce;
        private long _resumeTimeMs;
        private bool _resumeApplied;
        private bool _completionHandled;

        private const long PlaybackSaveIntervalMs = 5000;
        private const string PlaybackStatePrefix = "playbackState:";

        public PlayerArgs CurrentArgs => _args;

        private readonly DispatcherTimer _clockTimer =
        new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        public PlayerPage()
        {
            InitializeComponent();
            _overlayTimer.Tick += (s, e) => HideOverlay();
            _gamepadTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };

            _gamepadTimer.Tick += GamepadTimer_Tick;
            _clockTimer.Tick += (s, e) => UpdateClocks();
        }

        private static bool StickPressed(double current, double previous, double threshold = 0.5)
        {
            return Math.Abs(current) >= threshold &&
                   Math.Abs(previous) < threshold;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _args = (PlayerArgs)e.Parameter;
            _resumeTimeMs = LoadSavedPlaybackTime(_args?.VideoId);
            _resumeApplied = false;
            _completionHandled = false;
            TitleText.Text = _args.Title ?? "";
            _displayRequest.RequestActive();
            ShowOverlay();
            _gamepadTimer.Start();
            _clockTimer.Start();
            _suppressGamepadA = false;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            SavePlaybackPosition("navigated-from", true);
            _overlayTimer.Stop();
            try { _displayRequest.RequestRelease(); } catch (Exception) { }

            var player = _player;
            var libVlc = _libVlc;
            _player = null;
            _libVlc = null;
            if (player != null)
            {
                Video.MediaPlayer = null;
                player.Stop();
                player.Dispose();
            }
            libVlc?.Dispose();
            _gamepadTimer.Stop();
            _clockTimer.Stop();
            DisplayModeService.RestoreDefault();
        }

        public void HandleBackNavigation()
        {
            SavePlaybackPosition("back-requested", true);

            if (App.RootFrame != null && App.RootFrame.CanGoBack)
                App.RootFrame.GoBack();
        }

        // VideoView creates its swap chain first; LibVLC must be created with those options.
        private void Video_Initialized(object sender, InitializedEventArgs e)
        {
            var options = new List<string>(e.SwapChainOptions)
            {
                "--avcodec-hw=d3d11va",     // hardware decode on the console GPU (H.264/HEVC/VP9/AV1)
                "--network-caching=3000",
                "--sub-track-id=-1",
            };
            if (AppServices.Settings.AudioPassthrough)
                options.Add("--spdif"); // bitstream AC3/E-AC3/DTS to the TV or receiver

            _libVlc = new LibVLC(false, options.ToArray());
            _player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
            _player.Playing += (s, a) => OnUi(OnPlaying);
            _player.Buffering += (s, a) => OnUi(() => Buffering.IsActive = a.Cache < 100);
            _player.TimeChanged += (s, a) => OnUi(() => UpdatePosition(a.Time));
            _player.EncounteredError += (s, a) => OnUi(() => ShowError("Playback failed. The stream may be offline or unsupported."));
            _player.EndReached += (s, a) => OnUi(OnPlaybackEnded);
            _player.Vout += (s, a) => OnUi(MatchDisplay);
            Video.MediaPlayer = _player;

            var media = new Media(_libVlc, new Uri(_args.Url));
            if (_args.Headers != null && _args.Headers.TryGetValue("User-Agent", out var userAgent))
                media.AddOption(":http-user-agent=" + userAgent);
            _player.Play(media);
            media.Dispose();

            // Attach addon subtitles; the preferred language is selected, others stay in the menu.
            var preferred = AppServices.Settings.PreferredSubtitleLanguage;
            var subtitles = (_args.Subtitles ?? new List<Stremio.Core.Addons.Subtitle>())
                .Where(s => !string.IsNullOrEmpty(s.Url)).Take(20).ToList();
            var selected = subtitles.FirstOrDefault(s => s.Lang == preferred);
            foreach (var sub in subtitles)
                _player.AddSlave(MediaSlaveType.Subtitle, sub.Url, sub == selected);
            _player.Paused += (s, a) => OnUi(() =>
            {
                SavePlaybackPosition("paused", true);
                PlayPauseIcon.Symbol = Symbol.Play;
                Window.Current.CoreWindow.PointerCursor =
                    new CoreCursor(CoreCursorType.Arrow, 0);
                ShowOverlay();
            });
        }

        private async void OnPlaybackEnded()
        {
            if (_completionHandled)
                return;

            _completionHandled = true;
            SavePlaybackPosition("ended", true);
            await MarkCurrentVideoWatchedAsync();

            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private async Task MarkCurrentVideoWatchedAsync()
        {
            if (!AppServices.Settings.IsLoggedIn ||
                string.IsNullOrWhiteSpace(_args?.MetaId) ||
                string.IsNullOrWhiteSpace(_args?.VideoId))
            {
                return;
            }

            try
            {
                await AppServices.EnsureAddonsLoadedAsync();

                var libraryItems = await AppServices.Api.GetLibraryAsync(
                    AppServices.Settings.AuthKey);
                var libraryItem = libraryItems.FirstOrDefault(item =>
                    string.Equals(
                        item.Id,
                        _args.MetaId,
                        StringComparison.OrdinalIgnoreCase));

                if (libraryItem == null)
                    return;

                var meta = await AppServices.Addons.GetMetaAsync(
                    _args.Type,
                    _args.MetaId);
                var episodes = WatchStateHelper.OrderEpisodes(meta?.Videos);
                var video = episodes.FirstOrDefault(item =>
                    string.Equals(
                        item.Id,
                        _args.VideoId,
                        StringComparison.OrdinalIgnoreCase));

                // Movies do not have an episode bitfield. This completion path
                // currently applies to series episodes only.
                if (video == null)
                    return;

                var watched = WatchStateHelper.SetVideo(
                    libraryItem.State?.Watched,
                    episodes,
                    video,
                    true);

                await AppServices.Api.UpdateLibraryWatchedAsync(
                    AppServices.Settings.AuthKey,
                    libraryItem.Id,
                    watched,
                    true);

                Debug.WriteLine(
                    "Episode marked watched on completion: videoId=" +
                    _args.VideoId +
                    ", libraryId=" + libraryItem.Id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "Completion watch update failed: " + ex.Message);
            }
        }

        private void OnPlaying()
        {
            ApplySavedPlaybackPosition();
            Window.Current.CoreWindow.PointerCursor = null;

            Buffering.IsActive = false;
            ErrorText.Visibility = Visibility.Collapsed;
            PlayPauseIcon.Symbol = Symbol.Pause;
            DurationText.Text = Format(_player?.Length ?? 0);

            RefreshChapters();

            ShowOverlay();
        }

        private async void MatchDisplay()
        {
            if (_displayMatched || _player == null || !AppServices.Settings.MatchDisplayMode)
                return;
            uint width = 0, height = 0;
            if (!_player.Size(0, ref width, ref height) || width == 0)
                return;
            _displayMatched = true;
            await DisplayModeService.MatchAsync(width, height, _player.Fps, _args.LooksHdr);
        }

        private void UpdatePosition(long time)
        {
            var length = _player?.Length ?? 0;

            if (!_resumeApplied && length > 0)
                ApplySavedPlaybackPosition();

            PositionText.Text = Format(time);
            if (length > 0)
            {
                DurationText.Text = Format(length);
                Progress.Value = (double)time / length;
            }

            if (_resumeApplied &&
                (_lastSavedTime < 0 ||
                Math.Abs(time - _lastSavedTime) >= PlaybackSaveIntervalMs)
            )
            {
                SavePlaybackPosition("time-changed");
            }
        }

        private void ApplySavedPlaybackPosition()
        {
            if (_resumeApplied || _player == null || _player.Length <= 0)
                return;

            _resumeApplied = true;

            if (_resumeTimeMs <= 0 ||
                _resumeTimeMs >= _player.Length * 0.95)
            {
                return;
            }

            var target = Math.Max(
                0,
                Math.Min(_player.Length - 1000, _resumeTimeMs));

            _player.Time = target;
            UpdatePosition(target);

            Debug.WriteLine(
                "Playback resumed: videoId=" +
                (_args?.VideoId ?? "") +
                ", timeMs=" + target +
                ", durationMs=" + _player.Length);
        }

        private static long LoadSavedPlaybackTime(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
                return 0;

            var key = PlaybackStatePrefix + videoId;
            var value = ApplicationData.Current.LocalSettings.Values[key] as string;
            if (string.IsNullOrWhiteSpace(value))
                return 0;

            var parts = value.Split('|');
            return parts.Length > 2 && long.TryParse(parts[2], out var timeMs)
                ? timeMs
                : 0;
        }

        private void ShowError(string message)
        {
            Buffering.IsActive = false;
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
            ShowOverlay();
        }

        private void ShowOverlay(bool focusPlayPause = false)
        {
            var wasHidden = Overlay.Visibility != Visibility.Visible;
            Overlay.Visibility = Visibility.Visible;

            if (focusPlayPause ||
                wasHidden ||
                FocusManager.GetFocusedElement() == null)
            {
                PlayPauseButton.Focus(FocusState.Keyboard);
            }

            _overlayTimer.Stop();
            _overlayTimer.Start();
        }

        private void HideOverlay()
        {
            _overlayTimer.Stop();
            if (_player != null && _player.IsPlaying && !AudioMenu.IsOpen && !SubtitleMenu.IsOpen)
                Overlay.Visibility = Visibility.Collapsed;
        }

        private void RefreshChapters()
        {
            if (_player == null)
            {
                ChapterButton.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                var chapters = _player.ChapterDescription(_player.Title);
                ChapterButton.Visibility = chapters != null && chapters.Length > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                FillChapterMenu(chapters);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Chapter discovery failed: " + ex.Message);
                ChapterButton.Visibility = Visibility.Collapsed;
            }
        }

        private void FillChapterMenu(
            LibVLCSharp.Shared.Structures.TrackDescription[] chapters)
        {
            ChapterMenu.Items.Clear();

            if (chapters == null || chapters.Length == 0)
            {
                ChapterMenu.Items.Add(new MenuFlyoutItem
                {
                    Text = "No chapters",
                    IsEnabled = false
                });
                return;
            }

            for (var i = 0; i < chapters.Length; i++)
            {
                var chapter = chapters[i];
                var chapterNumber = i + 1;
                var chapterId = chapter.Id;
                var chapterName = string.IsNullOrWhiteSpace(chapter.Name)
                    ? "Chapter " + chapterNumber
                    : chapter.Name;

                var item = new MenuFlyoutItem
                {
                    Text = chapterNumber + ". " + chapterName,
                    Tag = chapterId
                };
                item.Click += (s, e) => SelectChapter(chapterId);
                ChapterMenu.Items.Add(item);
            }
        }

        private void SelectChapter(int chapterId)
        {
            if (_player == null)
                return;

            _player.Chapter = chapterId;
            ShowOverlay();
        }

        private void ChapterMenu_Opening(object sender, object e)
        {
            RefreshChapters();
        }

        private void GamepadTimer_Tick(object sender, object e)
        {
            if (Gamepad.Gamepads.Count == 0)
                return;

            var reading = Gamepad.Gamepads[0].GetCurrentReading();
            var buttons = reading.Buttons;

            var directionalInput =
                buttons.HasFlag(GamepadButtons.DPadLeft) ||
                buttons.HasFlag(GamepadButtons.DPadRight) ||
                buttons.HasFlag(GamepadButtons.DPadUp) ||
                buttons.HasFlag(GamepadButtons.DPadDown) ||
                Math.Abs(reading.LeftThumbstickX) >= 0.65 ||
                Math.Abs(reading.LeftThumbstickY) >= 0.65;

            var overlayWasVisible = Overlay.Visibility == Visibility.Visible;
            if (directionalInput)
            {
                if (!overlayWasVisible)
                    ShowOverlay(true);
                _suppressGamepadA = true;
            }
            else if (!buttons.HasFlag(GamepadButtons.A))
            {
                _suppressGamepadA = false;
            }

            // A = play/pause
            if (Pressed(buttons, GamepadButtons.A) && !_suppressGamepadA)
                if (!overlayWasVisible)
                {
                    TogglePause();
                }


            // Left stick or D-pad left/right = seek
            if (!overlayWasVisible)
            {
                if (Pressed(buttons, GamepadButtons.DPadLeft) ||
                    (StickPressed(reading.LeftThumbstickX, _previousThumbstickX) &&
                     reading.LeftThumbstickX < 0))
                {
                    Seek(-10000);
                }

                if (Pressed(buttons, GamepadButtons.DPadRight) ||
                    (StickPressed(reading.LeftThumbstickX, _previousThumbstickX) &&
                     reading.LeftThumbstickX > 0))
                {
                    Seek(30000);
                }
            }

            // Bumpers = seek
            if (Pressed(buttons, GamepadButtons.LeftShoulder))
                Seek(-10000);

            if (Pressed(buttons, GamepadButtons.RightShoulder))
                Seek(30000);

            // Triggers = large seek
            if (reading.LeftTrigger > 0.5 &&
                _previousLeftTrigger <= 0.5)
            {
                Seek(-300000);
            }

            if (reading.RightTrigger > 0.5 &&
                _previousRightTrigger <= 0.5)
            {
                Seek(300000);
            }

            _previousButtons = buttons;
            _previousLeftTrigger = reading.LeftTrigger;
            _previousRightTrigger = reading.RightTrigger;
            _previousThumbstickX = reading.LeftThumbstickX;
        }

        private bool Pressed(GamepadButtons current, GamepadButtons button)
        {
            return current.HasFlag(button) &&
                   !_previousButtons.HasFlag(button);
        }

        private void Seek(long deltaMs)
        {
            if (_player == null || !_player.IsSeekable)
                return;
            var target = Math.Max(0, Math.Min(_player.Length - 1000, _player.Time + deltaMs));
            _player.Time = target;
            UpdatePosition(target);
            SavePlaybackPosition("seek");
        }

        private void SavePlaybackPosition(
            string reason,
            bool forceServerSync = false)
        {
            if (_player == null ||
                _args == null ||
                string.IsNullOrWhiteSpace(_args.VideoId))
            {
                return;
            }

            var time = Math.Max(0, _player.Time);
            var duration = Math.Max(0, _player.Length);
            if (!_resumeApplied && time <= 0)
                return;

            if (duration <= 0)
                return;

            var key = PlaybackStatePrefix + _args.VideoId;
            var value = string.Join("|", new[]
            {
                _args.VideoId,
                _args.Title ?? "",
                time.ToString(),
                duration.ToString(),
                DateTime.UtcNow.ToString("O"),
                Uri.EscapeDataString(_args.Url ?? "")
            });

            ApplicationData.Current.LocalSettings.Values[key] = value;
            _lastSavedTime = time;

            Debug.WriteLine(
                "Playback saved: reason=" + reason +
                ", videoId=" + _args.VideoId +
                ", title=" + (_args.Title ?? "") +
                ", timeMs=" + time +
                ", durationMs=" + duration);

            _ = SyncPlaybackPositionAsync(time, duration, forceServerSync);
        }

        private async Task SyncPlaybackPositionAsync(
            long time,
            long duration,
            bool force)
        {
            if (string.IsNullOrWhiteSpace(_args?.MetaId) ||
                !AppServices.Settings.IsLoggedIn ||
                (!force &&
                 _lastServerSavedTime >= 0 &&
                 Math.Abs(time - _lastServerSavedTime) < PlaybackSaveIntervalMs))
            {
                return;
            }

            if (_serverSaveInProgress)
            {
                if (force)
                {
                    _pendingServerSync = true;
                    _pendingServerTime = time;
                    _pendingServerDuration = duration;
                    _pendingServerForce = true;
                }

                return;
            }

            _serverSaveInProgress = true;

            try
            {
                await AppServices.Api.UpdateLibraryPlaybackAsync(
                    AppServices.Settings.AuthKey,
                    _args.MetaId,
                    _args.VideoId,
                    time,
                    duration);

                _lastServerSavedTime = time;

                Debug.WriteLine(
                    "Playback synced: videoId=" +
                    _args.VideoId +
                    ", libraryId=" + _args.MetaId +
                    ", timeMs=" + time +
                    ", durationMs=" + duration);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "Playback sync failed: " + ex.Message);
            }
            finally
            {
                _serverSaveInProgress = false;

                if (_pendingServerSync)
                {
                    var pendingTime = _pendingServerTime;
                    var pendingDuration = _pendingServerDuration;
                    var pendingForce = _pendingServerForce;

                    _pendingServerSync = false;
                    _pendingServerForce = false;

                    _ = SyncPlaybackPositionAsync(
                        pendingTime,
                        pendingDuration,
                        pendingForce);
                }
            }
        }

        private void TogglePause()
        {
            if (_player == null)
                return;
            if (_player.IsPlaying)
                _player.SetPause(true);
            else
                _player.Play();
        }

        private void Rewind_Click(object sender, RoutedEventArgs e) { Seek(-10000); }
        private void Forward_Click(object sender, RoutedEventArgs e) { Seek(30000); }
        private void PlayPause_Click(object sender, RoutedEventArgs e) { TogglePause(); }

        private static void FillTrackMenu(MenuFlyout menu, LibVLCSharp.Shared.Structures.TrackDescription[] tracks, int current, Action<int> select)
        {
            menu.Items.Clear();
            if (tracks == null || tracks.Length == 0)
            {
                menu.Items.Add(new MenuFlyoutItem { Text = "No tracks", IsEnabled = false });
                return;
            }
            foreach (var track in tracks)
            {
                var id = track.Id;
                var item = new ToggleMenuFlyoutItem
                {
                    Text = track.Name ?? ("Track " + id),
                    IsChecked = id == current,
                    Tag = id
                };
                item.Click += (s, a) => select(id);
                menu.Items.Add(item);
            }

        }

        private void OnUi(Action action)
        {
            var _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_player != null)
                    action();
            });
        }

        private static string Format(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private void AudioMenu_Opening(object sender, object e)
        {
            FillTrackMenu(
                AudioMenu,
                _player?.AudioTrackDescription,
                _player?.AudioTrack ?? -1,
                id => _player?.SetAudioTrack(id));
        }

        private void SubtitleMenu_Opening(object sender, object e)
        {
            FillTrackMenu(
                SubtitleMenu,
                _player?.SpuDescription,
                _player?.Spu ?? -1,
                id => _player?.SetSpu(id));
        }

        private void UpdateClocks()
        {
            CurrentClockText.Text =
                DateTime.Now.ToString("h:mm tt");

            var remaining = Math.Max(
                0,
                (_player?.Length ?? 0) -
                (_player?.Time ?? 0));

            var finishTime = DateTime.Now.AddMilliseconds(remaining);

            RemainingClockText.Text =
                "Ends: " + finishTime.ToString("h:mm tt");
        }
    }
}
