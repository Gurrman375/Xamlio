using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stremio.Core.Addons;
using Stremio.Core.Streaming;
using LibVLCSharp.Platforms.Windows;
using LibVLCSharp.Shared;
using Xamlio.Services;
using Windows.System.Display;
using Windows.System;
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
        private DateTime _lastLocalSaveUtc = DateTime.MinValue;
        private long _lastServerSavedTime = -1;
        private bool _serverSaveInProgress;
        private bool _pendingServerSync;
        private long _pendingServerTime;
        private long _pendingServerDuration;
        private bool _pendingServerForce;
        private long _resumeTimeMs;
        private bool _resumeApplied;
        private bool _completionHandled;
        private string _lastLibVlcError;
        private bool _audioSwitchPending;
        private int _audioSwitchRequest;
        private DateTime _audioSwitchStartedUtc;
        private bool _seekInProgress;
        private bool _pauseForSeek;
        private DateTime _lastSeekUtc = DateTime.MinValue;
        private long _lastSeekTargetMs;
        private long? _pendingSeekTarget;
        private string _pendingSeekSource;
        private Video _nextEpisode;
        private List<Video> _episodeVideos = new List<Video>();
        private Task _nextDiscoveryTask;
        private Task<PreparedEpisode> _nextPreparationTask;
        private CancellationTokenSource _lifecycleCancellation;
        private readonly HashSet<string> _loadedSubtitleUrls =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _defaultTracksApplied;
        private bool _additionalSubtitlesLoading;

        private const long PlaybackSaveIntervalMs = 5000;
        private const long ServerSyncIntervalMs = 60000;
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
            _lifecycleCancellation?.Dispose();
            _lifecycleCancellation = new CancellationTokenSource();
            _args = (PlayerArgs)e.Parameter;
            LogAppMemory("navigated-to");
            _resumeTimeMs = LoadSavedPlaybackTime(_args?.VideoId);
            _resumeApplied = false;
            _completionHandled = false;
            _nextEpisode = null;
            _episodeVideos.Clear();
            _nextPreparationTask = null;
            NextEpisodeButton.Visibility = Visibility.Collapsed;
            _nextDiscoveryTask = LoadNextEpisodeAsync(_lifecycleCancellation.Token);
            TitleText.Text = _args.Title ?? "";
            _displayRequest.RequestActive();
            ShowOverlay();
            _gamepadTimer.Start();
            _clockTimer.Start();
            _suppressGamepadA = false;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            AppLog.WriteLine("[PlayerLifecycle] navigated-from, disposing playback resources");
            LogAppMemory("before-dispose");
            SavePlaybackPosition("navigated-from", true);
            _lifecycleCancellation?.Cancel();
            _lifecycleCancellation?.Dispose();
            _lifecycleCancellation = null;
            _nextDiscoveryTask = null;
            _nextPreparationTask = null;
            _nextEpisode = null;
            _episodeVideos.Clear();
            _loadedSubtitleUrls.Clear();
            _additionalSubtitlesLoading = false;
            _overlayTimer.Stop();
            try { _displayRequest.RequestRelease(); } catch (Exception) { }

            var player = _player;
            var libVlc = _libVlc;
            _player = null;
            _libVlc = null;
            if (player != null)
            {
                AppLog.WriteLine("[PlayerLifecycle] detaching VideoView from MediaPlayer");
                Video.MediaPlayer = null;
                try
                {
                    AppLog.WriteLine("[PlayerLifecycle] stopping MediaPlayer");
                    player.Stop();
                }
                catch (Exception ex)
                {
                    AppLog.WriteLine("[PlayerLifecycle] MediaPlayer.Stop failed: " + ex.Message);
                }

                try
                {
                    AppLog.WriteLine("[PlayerLifecycle] disposing MediaPlayer");
                    player.Dispose();
                }
                catch (Exception ex)
                {
                    AppLog.WriteLine("[PlayerLifecycle] MediaPlayer.Dispose failed: " + ex.Message);
                }
            }

            try
            {
                if (libVlc != null)
                {
                    AppLog.WriteLine("[PlayerLifecycle] disposing LibVLC");
                    libVlc.Dispose();
                }
            }
            catch (Exception ex)
            {
                AppLog.WriteLine("[PlayerLifecycle] LibVLC.Dispose failed: " + ex.Message);
            }
            _gamepadTimer.Stop();
            _clockTimer.Stop();
            _args = null;
            LogAppMemory("after-dispose");
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
            AppLog.WriteLine("[PlayerLifecycle] VideoView initialized; creating LibVLC");
            var options = new List<string>(e.SwapChainOptions)
            {
                "--avcodec-hw=none",
                "--network-caching=3000",
                "--sub-track-id=-1",
                "--verbose=2",
                // Temporary crash-isolation mode: remove WASAPI/audio-clock resets while seeking.
                "--aout=dummy",
                "--audio-track=-1",
            };
            if (AppServices.Settings.AudioPassthrough)
                options.Add("--spdif"); // bitstream AC3/E-AC3/DTS to the TV or receiver

            _libVlc = new LibVLC(false, options.ToArray());
            _libVlc.Log += (s, a) => LogLibVlc(a.Level, a.Module, a.Message);
            _player = new MediaPlayer(_libVlc) { EnableHardwareDecoding = false };
            AppLog.WriteLine("[Diagnostics] LibVLC dummy audio output enabled for seek-crash isolation");
            AppLog.WriteLine("[PlayerLifecycle] LibVLC and MediaPlayer created; hardware decoder disabled");
            LogAppMemory("libvlc-created");
            _player.Playing += (s, a) => OnUi(() =>
            {
                ApplyDefaultTracks();
                OnPlaying();
            });
            _player.Buffering += (s, a) => OnUi(() => OnBuffering(a.Cache));
            _player.TimeChanged += (s, a) => OnUi(() => UpdatePosition(a.Time));
            _player.EncounteredError += (s, a) => OnUi(() =>
            {
                ReleaseSeek("playback-error");
                LogAudio("event=error, pending=" + _audioSwitchPending +
                    ", message=Playback error");
                var detail = string.IsNullOrWhiteSpace(_lastLibVlcError)
                    ? "The stream may be offline or unsupported."
                    : _lastLibVlcError;
                ShowError("Playback failed: " + detail);
            });
            _player.EndReached += (s, a) => OnUi(() =>
            {
                if (IsRecentFailedSeek())
                {
                    AppLog.WriteLine("[SeekDiagnostics] ignoring EndReached after failed seek, " +
                        "targetMs=" + _lastSeekTargetMs + ", currentMs=" + (_player?.Time ?? 0) +
                        ", lengthMs=" + (_player?.Length ?? 0));
                    ShowSeekError("Seek failed: LibVLC could not read the requested video position.");
                    ReleaseSeek("false-end-reached");
                    return;
                }

                ReleaseSeek("end-reached");
                OnPlaybackEnded();
            });
            _player.Vout += (s, a) => OnUi(MatchDisplay);
            Video.MediaPlayer = _player;

            AppLog.WriteLine("[PlayerLifecycle] assigning MediaPlayer to VideoView and starting media");
            PlayMedia(_args.Url, _args.Headers, _args.Subtitles);
            _player.Paused += (s, a) => OnUi(() =>
            {
                if (!_pauseForSeek)
                    ReleaseSeek("paused");
                SavePlaybackPosition("paused", true);
                PlayPauseIcon.Symbol = Symbol.Play;
                Window.Current.CoreWindow.PointerCursor =
                    new CoreCursor(CoreCursorType.Arrow, 0);
                ShowOverlay();
            });
        }

        private void PlayMedia(
            string url,
            IDictionary<string, string> headers,
            IList<Subtitle> subtitles)
        {
            if (_player == null || string.IsNullOrWhiteSpace(url))
                return;

            _lastLibVlcError = null;
            SeekStatusText.Visibility = Visibility.Collapsed;
            _defaultTracksApplied = false;
            _loadedSubtitleUrls.Clear();
            AppLog.WriteLine("[PlaybackDiagnostics] media-start, videoId=" +
                (_args?.VideoId ?? "") + ", urlHost=" + GetUrlHost(url) +
                ", subtitlesProvided=" + (subtitles?.Count ?? 0));
            _player.Stop();
            var media = new Media(_libVlc, new Uri(url));
            AppLog.WriteLine("[PlaybackDiagnostics] media-created, videoId=" +
                (_args?.VideoId ?? "") + ", subtitleTrackOption=" +
                (!AppServices.Settings.SubtitlesEnabled ? "disabled" : "default"));
            if (headers != null && headers.TryGetValue("User-Agent", out var userAgent))
                media.AddOption(":http-user-agent=" + userAgent);
            if (!AppServices.Settings.SubtitlesEnabled)
                media.AddOption(":sub-track-id=-1");
            _player.Play(media);
            AppLog.WriteLine("[PlaybackDiagnostics] media-play-called, videoId=" +
                (_args?.VideoId ?? "") + ", playerTimeMs=" + _player.Time);
            LogAppMemory("media-play-called");
            media.Dispose();

            var validSubtitles = (subtitles ?? new List<Subtitle>())
                .Where(s => !string.IsNullOrEmpty(s.Url))
                .GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (AppServices.Settings.SubtitlesEnabled)
            {
                var preferred = AppServices.Settings.PreferredSubtitleLanguage;
                var selected = validSubtitles.FirstOrDefault(s =>
                    string.Equals(s.Lang, preferred, StringComparison.OrdinalIgnoreCase));

                if (selected != null)
                {
                    AppLog.WriteLine("[PlaybackDiagnostics] subtitle-addslave, videoId=" +
                        (_args?.VideoId ?? "") + ", language=" + (selected.Lang ?? "") +
                        ", source=default-external");
                    _player.AddSlave(MediaSlaveType.Subtitle, selected.Url, true);
                    _loadedSubtitleUrls.Add(selected.Url);
                    AppLog.WriteLine("[SubtitleDiagnostics] default subtitle attached, language=" +
                        (selected.Lang ?? "") + ", candidates=" + validSubtitles.Count);
                }
                else
                {
                    AppLog.WriteLine("[SubtitleDiagnostics] preferred subtitle unavailable, language=" +
                        preferred + ", candidates=" + validSubtitles.Count);
                }
            }
            else
            {
                AppLog.WriteLine("[SubtitleDiagnostics] subtitles disabled by default, candidates=" +
                    validSubtitles.Count);
            }
        }

        private void ApplyDefaultTracks()
        {
            if (_defaultTracksApplied || _player == null)
                return;

            _defaultTracksApplied = true;

            var preferredAudio = AppServices.Settings.PreferredAudioLanguage;
            var audioTracks = _player.AudioTrackDescription;
            var audioTrackId = -1;
            if (audioTracks != null)
            {
                foreach (var track in audioTracks)
                {
                    if (TrackMatchesLanguage(track.Name, preferredAudio))
                    {
                        audioTrackId = track.Id;
                        break;
                    }
                }
            }

            if (audioTrackId >= 0 && audioTrackId != _player.AudioTrack)
                _player.SetAudioTrack(audioTrackId);

            if (AppServices.Settings.SubtitlesEnabled)
                ApplyDefaultSubtitleTrack();

            AppLog.WriteLine("[TrackDiagnostics] defaults applied, audio=" +
                preferredAudio + ", audioTrack=" + (_player.AudioTrack) +
                ", subtitles=" + (AppServices.Settings.SubtitlesEnabled ? "enabled" : "disabled"));
            LogAppMemory("default-tracks-applied");
        }

        private void ApplyDefaultSubtitleTrack()
        {
            var tracks = _player?.SpuDescription;
            if (tracks == null || tracks.Length == 0)
                return;

            var language = AppServices.Settings.PreferredSubtitleLanguage;
            var preferSignsAndSongs = AppServices.Settings.PreferSignsAndSongs;
            var selectedId = -1;
            var selectedName = "";
            foreach (var track in tracks)
            {
                if (TrackMatchesLanguage(track.Name, language) &&
                    (!preferSignsAndSongs || IsSignsAndSongsTrack(track.Name)))
                {
                    selectedId = track.Id;
                    selectedName = track.Name;
                    break;
                }
            }

            if (selectedId < 0)
            {
                foreach (var track in tracks)
                {
                    if (TrackMatchesLanguage(track.Name, language))
                    {
                        selectedId = track.Id;
                        selectedName = track.Name;
                        break;
                    }
                }
            }

            if (selectedId >= 0)
            {
                _player.SetSpu(selectedId);
                AppLog.WriteLine("[SubtitleDiagnostics] default embedded subtitle selected, id=" +
                    selectedId + ", signsAndSongs=" + preferSignsAndSongs +
                    ", name=" + (selectedName ?? ""));
                LogAppMemory("default-embedded-subtitle-selected");
            }
        }

        private static bool IsSignsAndSongsTrack(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var normalized = name.ToLowerInvariant();
            return normalized.Contains("signs") ||
                normalized.Contains("songs") ||
                normalized.Contains("signs & songs") ||
                normalized.Contains("sdh") ||
                normalized.Contains("hearing impaired");
        }

        private static bool TrackMatchesLanguage(string name, string language)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language))
                return false;

            var normalized = name.ToLowerInvariant();
            switch (language.ToLowerInvariant())
            {
                case "eng": return normalized.Contains("english") || normalized.Contains("[eng]");
                case "jpn": return normalized.Contains("japanese") || normalized.Contains("[jpn]");
                case "spa": return normalized.Contains("spanish") || normalized.Contains("[spa]");
                case "por": return normalized.Contains("portuguese") || normalized.Contains("[por]");
                case "fre": return normalized.Contains("french") || normalized.Contains("[fre]") || normalized.Contains("[fra]");
                case "ger": return normalized.Contains("german") || normalized.Contains("[ger]") || normalized.Contains("[deu]");
                case "ita": return normalized.Contains("italian") || normalized.Contains("[ita]");
                case "kor": return normalized.Contains("korean") || normalized.Contains("[kor]");
                case "chi": return normalized.Contains("chinese") || normalized.Contains("[chi]") || normalized.Contains("[zho]");
                default: return normalized.Contains(language.ToLowerInvariant());
            }
        }

        private async void OnPlaybackEnded()
        {
            if (_completionHandled)
                return;

            _completionHandled = true;
            SavePlaybackPosition("ended", true);
            await MarkCurrentVideoWatchedAsync();

            if (await StartPreparedNextEpisodeAsync())
                return;

            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private async void NextEpisode_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (_completionHandled)
                return;

            _completionHandled = true;
            SavePlaybackPosition("next-episode-requested", true);
            await MarkCurrentVideoWatchedAsync();

            if (await StartPreparedNextEpisodeAsync())
                return;

            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private async Task LoadNextEpisodeAsync(CancellationToken cancellationToken)
        {
            NextEpisodeButton.Visibility = Visibility.Collapsed;

            if (!string.Equals(_args?.Type, "series", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(_args?.MetaId) ||
                string.IsNullOrWhiteSpace(_args?.VideoId))
                return;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AppServices.EnsureAddonsLoadedAsync();
                cancellationToken.ThrowIfCancellationRequested();
                var meta = await AppServices.Addons.GetMetaAsync(_args.Type, _args.MetaId);
                cancellationToken.ThrowIfCancellationRequested();
                _episodeVideos = WatchStateHelper.OrderEpisodes(meta?.Videos);
                var index = _episodeVideos.FindIndex(v =>
                    string.Equals(v.Id, _args.VideoId, StringComparison.OrdinalIgnoreCase));

                if (index < 0 || index + 1 >= _episodeVideos.Count)
                {
            AppLog.WriteLine("Next episode not found: videoId=" + (_args.VideoId ?? ""));
                    return;
                }

                _nextEpisode = _episodeVideos[index + 1];
                NextEpisodeButton.Visibility = Visibility.Visible;
                AppLog.WriteLine("Next episode found: videoId=" + _nextEpisode.Id +
                    ", title=" + (_nextEpisode.DisplayTitle ?? ""));
                _nextPreparationTask = PrepareNextEpisodeAsync(
                    _nextEpisode, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                AppLog.WriteLine("[PreloadDiagnostics] discovery-cancelled");
            }
            catch (Exception ex)
            {
                AppLog.WriteLine("Next episode discovery failed: " + ex.Message);
            }
        }

        private async Task<PreparedEpisode> PrepareNextEpisodeAsync(
            Video episode,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppLog.WriteLine("[PreloadDiagnostics] started, videoId=" +
                    (episode?.Id ?? "") + ", stage=stream-lookup");
                var streams = await AppServices.Addons.GetStreamsFromAddonAsync(
                    _args.AddonUrl, _args.Type, episode.Id);
                cancellationToken.ThrowIfCancellationRequested();

                AppLog.WriteLine("[PreloadDiagnostics] stream-lookup-complete, videoId=" +
                    (episode?.Id ?? "") + ", source=preferred-addon, count=" +
                    (streams?.Count ?? 0));

                if (streams == null || streams.Count == 0)
                {
                    AppLog.WriteLine("[PreloadDiagnostics] stream-fallback-start, videoId=" +
                        (episode?.Id ?? "") + ", source=all-addons");
                    streams = await AppServices.Addons.GetStreamsAsync(_args.Type, episode.Id);
                    cancellationToken.ThrowIfCancellationRequested();
                    AppLog.WriteLine("[PreloadDiagnostics] stream-fallback-complete, videoId=" +
                        (episode?.Id ?? "") + ", count=" + (streams?.Count ?? 0));
                }

                var stream = streams.FirstOrDefault(s =>
                    StreamResolver.IsPlayable(s, AppServices.Settings.StreamingServerUrl));
                if (stream == null)
                {
                    AppLog.WriteLine("[PreloadDiagnostics] finished, videoId=" +
                        (episode?.Id ?? "") + ", result=no-playable-stream");
                    return null;
                }

                AppLog.WriteLine("[PreloadDiagnostics] playable-stream-selected, videoId=" +
                    (episode?.Id ?? "") + ", addon=" + (stream.AddonName ?? "") +
                    ", label=" + (stream.Label ?? ""));

                var resolved = StreamResolver.Resolve(
                    stream, AppServices.Settings.StreamingServerUrl);
                cancellationToken.ThrowIfCancellationRequested();
                if (resolved == null)
                {
                    AppLog.WriteLine("[PreloadDiagnostics] finished, videoId=" +
                        (episode?.Id ?? "") + ", result=resolve-failed");
                    return null;
                }

                var subtitles = new List<Subtitle>(resolved.Subtitles ?? new List<Subtitle>());

                AppLog.WriteLine("[PreloadDiagnostics] resolved, videoId=" +
                    (episode?.Id ?? "") + ", urlHost=" + GetUrlHost(resolved.Url) +
                    ", headers=" + (resolved.Headers?.Count ?? 0) +
                    ", subtitles=" + subtitles.Count +
                    ", mediaCreated=false, mediaPlayerCreated=false, playCalled=false, subtitleAttached=false");
                LogAppMemory("preload-resolved");

            AppLog.WriteLine("Next episode stream prepared: videoId=" + episode.Id +
                    ", addon=" + (stream.AddonName ?? "") +
                    ", title=" + (stream.Label ?? ""));

                return new PreparedEpisode
                {
                    Episode = episode,
                    Url = resolved.Url,
                    Headers = resolved.Headers,
                    Subtitles = subtitles,
                    LooksHdr = ((stream.Name ?? "") + " " + (stream.Title ?? "") + " " +
                        (stream.Description ?? "")).ToUpperInvariant().Contains("HDR")
                };
            }
            catch (OperationCanceledException)
            {
                AppLog.WriteLine("[PreloadDiagnostics] cancelled, videoId=" +
                    (episode?.Id ?? ""));
                return null;
            }
            catch (Exception ex)
            {
                AppLog.WriteLine("[PreloadDiagnostics] failed, videoId=" +
                    (episode?.Id ?? "") + ", exception=" + ex.GetType().Name +
                    ", message=" + ex.Message);
                AppLog.WriteLine("Next episode stream preparation failed: " + ex.Message);
                return null;
            }
        }

        private async Task<bool> StartPreparedNextEpisodeAsync()
        {
            if (_nextPreparationTask == null)
            {
                if (_nextDiscoveryTask != null)
                    await _nextDiscoveryTask;
                else
                    await LoadNextEpisodeAsync(
                        _lifecycleCancellation?.Token ?? CancellationToken.None);
            }

            if (_nextPreparationTask == null)
                return false;

            var prepared = await _nextPreparationTask;
            if (prepared == null || _player == null)
                return false;

            _args = new PlayerArgs
            {
                Url = prepared.Url,
                VideoId = prepared.Episode.Id,
                Type = _args.Type,
                MetaId = _args.MetaId,
                Title = prepared.Episode.DisplayTitle,
                Background = _args.Background,
                Headers = prepared.Headers,
                Subtitles = prepared.Subtitles,
                LooksHdr = prepared.LooksHdr,
                AddonUrl = _args.AddonUrl
            };

            _resumeTimeMs = 0;
            _resumeApplied = true;
            _lastSavedTime = -1;
            _lastLocalSaveUtc = DateTime.MinValue;
            _lastServerSavedTime = -1;
            _completionHandled = false;
            _displayMatched = false;
            _nextEpisode = null;
            _nextPreparationTask = null;
            NextEpisodeButton.Visibility = Visibility.Collapsed;
            TitleText.Text = _args.Title ?? "";

            _nextDiscoveryTask = LoadNextEpisodeAsync(
                _lifecycleCancellation?.Token ?? CancellationToken.None);

            AppLog.WriteLine("[PreloadDiagnostics] activating-prepared-stream, videoId=" +
                _args.VideoId + ", mediaCreated=true, mediaPlayerReused=true, playCalled=true");
            LogAppMemory("activate-prepared-stream");
            AppLog.WriteLine("Starting next episode in existing player: videoId=" + _args.VideoId);
            PlayMedia(_args.Url, _args.Headers, _args.Subtitles);
            return true;
        }

        private static string GetUrlHost(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.Host;
            return "<invalid>";
        }

        private void LogAppMemory(string reason)
        {
            try
            {
                var report = MemoryManager.GetAppMemoryReport();
                const double megabyte = 1024.0 * 1024.0;
                var totalUsageMb = report.TotalCommitUsage / megabyte;
                var privateUsageMb = report.PrivateCommitUsage / megabyte;
                var peakPrivateMb = report.PeakPrivateCommitUsage / megabyte;
                var totalLimitMb = report.TotalCommitLimit / megabyte;
                var expectedLimitMb = report.ExpectedTotalCommitLimit / megabyte;
                var effectiveLimitMb = totalLimitMb > 0 ? totalLimitMb : expectedLimitMb;
                var usagePercent = effectiveLimitMb > 0
                    ? totalUsageMb / effectiveLimitMb * 100.0
                    : 0.0;
                var remainingMb = effectiveLimitMb > 0
                    ? Math.Max(0, effectiveLimitMb - totalUsageMb)
                    : 0.0;

                AppLog.WriteLine("[MemoryDiagnostics] reason=" + reason +
                    ", videoId=" + (_args?.VideoId ?? "") +
                    ", totalUsageMb=" + totalUsageMb.ToString("F1") +
                    ", privateUsageMb=" + privateUsageMb.ToString("F1") +
                    ", peakPrivateMb=" + peakPrivateMb.ToString("F1") +
                    ", totalLimitMb=" + totalLimitMb.ToString("F1") +
                    ", expectedLimitMb=" + expectedLimitMb.ToString("F1") +
                    ", remainingMb=" + remainingMb.ToString("F1") +
                    ", usagePercent=" + usagePercent.ToString("F1"));

                if (usagePercent >= 90.0)
                {
                    AppLog.WriteLine("[MemoryDiagnostics] CRITICAL, reason=" + reason +
                        ", usagePercent=" + usagePercent.ToString("F1") +
                        ", remainingMb=" + remainingMb.ToString("F1"));
                }
                else if (usagePercent >= 80.0)
                {
                    AppLog.WriteLine("[MemoryDiagnostics] WARNING, reason=" + reason +
                        ", usagePercent=" + usagePercent.ToString("F1") +
                        ", remainingMb=" + remainingMb.ToString("F1"));
                }
            }
            catch (Exception ex)
            {
                AppLog.WriteLine("[MemoryDiagnostics] unavailable, reason=" + reason +
                    ", exception=" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private sealed class PreparedEpisode
        {
            public Video Episode { get; set; }
            public string Url { get; set; }
            public IDictionary<string, string> Headers { get; set; }
            public IList<Subtitle> Subtitles { get; set; }
            public bool LooksHdr { get; set; }
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

                AppLog.WriteLine(
                    "Episode marked watched on completion: videoId=" +
                    _args.VideoId +
                    ", libraryId=" + libraryItem.Id);
            }
            catch (Exception ex)
            {
                AppLog.WriteLine(
                    "Completion watch update failed: " + ex.Message);
            }
        }

        private void OnPlaying()
        {
            ReleaseSeek("playing");

            if (_audioSwitchPending)
            {
                var elapsed = DateTime.UtcNow - _audioSwitchStartedUtc;
                LogAudio("event=playing-after-switch, request=" + _audioSwitchRequest +
                    ", elapsedMs=" + (long)elapsed.TotalMilliseconds +
                    ", track=" + (_player?.AudioTrack ?? -1) +
                    ", timeMs=" + (_player?.Time ?? 0));
                _audioSwitchPending = false;
            }

            ApplySavedPlaybackPosition();
            Window.Current.CoreWindow.PointerCursor = null;

            Buffering.IsActive = false;
            if (string.Equals(SeekStatusText.Text, "Seeking…", StringComparison.Ordinal))
                SeekStatusText.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            PlayPauseIcon.Symbol = Symbol.Pause;
            DurationText.Text = Format(_player?.Length ?? 0);

            RefreshChapters();

            ShowOverlay();
        }

        private void OnBuffering(float cache)
        {
            Buffering.IsActive = cache < 100;

            if (_seekInProgress && !_pauseForSeek && cache >= 100)
                ReleaseSeek("buffering-complete");

            if (_audioSwitchPending)
            {
                LogAudio("event=buffering, request=" + _audioSwitchRequest +
                    ", cache=" + cache.ToString("F1") +
                    ", track=" + (_player?.AudioTrack ?? -1) +
                    ", timeMs=" + (_player?.Time ?? 0));
            }
        }

        private static void LogAudio(string message)
        {
            AppLog.WriteLine("[AudioDiagnostics] " + DateTime.UtcNow.ToString("O") +
                ", " + message);
        }

        private static string RedactNativeLog(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message ?? "";

            message = Regex.Replace(
                message,
                @"https?://[^\s]+",
                match =>
                {
                    if (!Uri.TryCreate(match.Value.TrimEnd('.', ',', ';', ')', ']'),
                        UriKind.Absolute, out var uri))
                        return "<redacted-url>";

                    return uri.Scheme + "://" + uri.Host + "/<redacted>";
                },
                RegexOptions.IgnoreCase);

            message = Regex.Replace(
                message,
                @"(?i)([?&](?:token|auth|authKey|apiKey|apikey|access_token|refresh_token)=)[^\s&]+",
                "$1<redacted>");

            return Regex.Replace(
                message,
                @"(?i)(torbox[\\/])[^\\/\s]+",
                "$1<redacted>");
        }

        private void LogLibVlc(object level, object module, string message)
        {
            if (string.Equals(module?.ToString(), "http", StringComparison.OrdinalIgnoreCase) &&
                (message ?? "").IndexOf("request:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            if (string.Equals(module?.ToString(), "http", StringComparison.OrdinalIgnoreCase) &&
                (message ?? "").IndexOf("response:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            var safeMessage = RedactNativeLog(message)
                .Replace("\r", " ")
                .Replace("\n", " ");

            if (string.Equals(level?.ToString(), "Error", StringComparison.OrdinalIgnoreCase))
                _lastLibVlcError = (module?.ToString() ?? "LibVLC") + ": " + safeMessage;

            if (string.Equals(module?.ToString(), "mkv", StringComparison.OrdinalIgnoreCase) &&
                safeMessage.IndexOf("unable to read KaxCluster", StringComparison.OrdinalIgnoreCase) >= 0 &&
                _seekInProgress)
            {
                OnUi(() =>
                {
                    ShowSeekError("Seek failed: LibVLC could not read the requested video position.");
                    ReleaseSeek("kaxcluster-read-failed");
                });
            }

            AppLog.WriteLine("[LibVLC] level=" + level +
                ", module=" + module +
                ", message=" + safeMessage);
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

            AppLog.WriteLine(
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
                var chapters = _player.FullChapterDescriptions(_player.Title);
                AppLog.WriteLine("[ChapterDiagnostics] refresh, title=" + _player.Title +
                    ", count=" + (chapters?.Length ?? 0));
                ChapterButton.Visibility = chapters != null && chapters.Length > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                FillChapterMenu(chapters);
            }
            catch (Exception ex)
            {
                AppLog.WriteLine("Chapter discovery failed: " + ex.Message);
                ChapterButton.Visibility = Visibility.Collapsed;
            }
        }

        private void FillChapterMenu(
            LibVLCSharp.Shared.Structures.ChapterDescription[] chapters)
        {
            ChapterMenu.Items.Clear();
            AppLog.WriteLine("[ChapterDiagnostics] menu-build, count=" + (chapters?.Length ?? 0));

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
                var chapterTimeOffset = chapter.TimeOffset;
                var chapterName = string.IsNullOrWhiteSpace(chapter.Name)
                    ? "Chapter " + chapterNumber
                    : chapter.Name;

                var item = new MenuFlyoutItem
                {
                    Text = chapterNumber + ". " + chapterName,
                    Tag = chapterTimeOffset
                };
                item.Click += (s, e) =>
                {
                    AppLog.WriteLine("[ChapterDiagnostics] menu-click, number=" +
                        chapterNumber + ", offsetMs=" + chapterTimeOffset +
                        ", name=" + chapterName);
                    SelectChapter(chapterTimeOffset);
                };
                ChapterMenu.Items.Add(item);
            }
        }

        private void SelectChapter(long chapterTimeOffset)
        {
            TrySeekTo(chapterTimeOffset, "chapter");
        }

        private void ChapterMenu_Opening(object sender, object e)
        {
            AppLog.WriteLine("[ChapterDiagnostics] menu-opening");
            RefreshChapters();
        }

        private void SelectAdjacentChapter(int direction)
        {
            if (_player == null)
                return;

            try
            {
                var chapters = _player.FullChapterDescriptions(_player.Title);
                if (chapters == null || chapters.Length == 0)
                    return;

                var current = _player.Time;
                var currentIndex = -1;
                for (var i = 0; i < chapters.Length; i++)
                {
                    if (chapters[i].TimeOffset <= current)
                        currentIndex = i;
                    else
                        break;
                }

                int targetIndex;
                if (direction < 0)
                {
                    // Back always means the previous chapter, even when the
                    // current position is already inside a chapter.
                    targetIndex = currentIndex - 1;
                }
                else
                {
                    // Forward advances to the next chapter. If playback is
                    // before the first chapter, select that first chapter.
                    targetIndex = currentIndex < 0 ? 0 : currentIndex + 1;
                }

                if (targetIndex < 0)
                {
                    AppLog.WriteLine("[ChapterDiagnostics] bumper ignored, direction=" +
                        direction + ", currentMs=" + current + ", reason=no-target");
                    return;
                }

                var target = chapters[targetIndex];
                // Seek just before the recorded boundary so LibVLC's MKV
                // preroll cannot leave playback at the end of the prior
                // chapter. The visible chapter still begins at its metadata
                // offset after decoding catches up.
                var targetMs = Math.Max(0, target.TimeOffset - 1000);
                AppLog.WriteLine("[ChapterDiagnostics] bumper, direction=" +
                    direction + ", currentIndex=" + currentIndex +
                    ", targetIndex=" + targetIndex +
                    ", currentName=" + (currentIndex >= 0 ? chapters[currentIndex].Name : "") +
                    ", targetName=" + (target.Name ?? "") +
                    ", targetChapterMs=" + target.TimeOffset +
                    ", targetSeekMs=" + targetMs);
                TrySeekTo(targetMs, "chapter-bumper");
            }
            catch (Exception ex)
            {
                AppLog.WriteLine("[ChapterDiagnostics] bumper failed: " + ex.Message);
            }
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


            // Bumpers = previous/next chapter.
            if (Pressed(buttons, GamepadButtons.LeftShoulder))
                SelectAdjacentChapter(-1);

            if (Pressed(buttons, GamepadButtons.RightShoulder))
                SelectAdjacentChapter(1);

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
            if (_player == null || !_player.IsSeekable || _player.Length <= 1000)
                return;
            var target = Math.Max(0, Math.Min(_player.Length - 1000, _player.Time + deltaMs));
            TrySeekTo(target, "button");
        }

        private void TrySeekTo(long targetMs, string source)
        {
            if (_player == null || !_player.IsSeekable)
                return;

            if (_seekInProgress)
            {
                _pendingSeekTarget = targetMs;
                _pendingSeekSource = source;
                Buffering.IsActive = true;
                AppLog.WriteLine("Seek queued: source=" + source +
                    ", targetMs=" + targetMs + ", reason=seek-in-progress");
                return;
            }

            var now = DateTime.UtcNow;
            if (_lastSeekUtc != DateTime.MinValue &&
                (now - _lastSeekUtc).TotalMilliseconds < 350)
            {
                AppLog.WriteLine("Seek rejected: source=" + source + ", reason=debounced");
                return;
            }

            var length = _player.Length;
            if (length <= 1000)
                return;

            var target = Math.Max(0, Math.Min(length - 1000, targetMs));
            _seekInProgress = true;
            _pauseForSeek = _player.IsPlaying;
            _lastSeekUtc = now;
            _lastSeekTargetMs = target;
            SeekStatusText.Text = "Seeking…";
            SeekStatusText.Visibility = Visibility.Visible;
            Buffering.IsActive = true;

            try
            {
                AppLog.WriteLine("Seek: source=" + source +
                    ", targetMs=" + target +
                    ", currentMs=" + _player.Time +
                    ", lengthMs=" + length);
                LogAppMemory("seek-before");

                if (_pauseForSeek)
                    _player.SetPause(true);

                _player.Time = target;
                UpdatePosition(target);
                SavePlaybackPosition("seek");
                ShowOverlay();

                if (_pauseForSeek && _player != null)
                    _player.Play();
                LogAppMemory("seek-after");
            }
            catch (Exception ex)
            {
                _seekInProgress = false;
                _pauseForSeek = false;
                Buffering.IsActive = false;
                ShowSeekError("Seek failed: " + ex.Message);
                AppLog.WriteLine("Seek failed: source=" + source +
                    ", type=" + ex.GetType().Name +
                    ", message=" + ex.Message);
            }
        }

        private void ReleaseSeek(string reason)
        {
            if (!_seekInProgress)
                return;

            _seekInProgress = false;
            _pauseForSeek = false;
            Buffering.IsActive = false;
            if (string.Equals(SeekStatusText.Text, "Seeking…", StringComparison.Ordinal))
                SeekStatusText.Visibility = Visibility.Collapsed;
            AppLog.WriteLine("Seek released: reason=" + reason);

            if (_pendingSeekTarget.HasValue)
            {
                var target = _pendingSeekTarget.Value;
                var source = _pendingSeekSource ?? "queued";
                _pendingSeekTarget = null;
                _pendingSeekSource = null;
                AppLog.WriteLine("Seek dequeued: source=" + source +
                    ", targetMs=" + target);

                var _ = Dispatcher.RunAsync(
                    CoreDispatcherPriority.Normal,
                    () => TrySeekTo(target, source));
            }
        }

        private bool IsRecentFailedSeek()
        {
            if (_lastSeekUtc == DateTime.MinValue || _player == null)
                return false;

            var elapsed = DateTime.UtcNow - _lastSeekUtc;
            var length = _player.Length;
            var current = Math.Max(0, _player.Time);
            return elapsed.TotalSeconds <= 3 &&
                length > 10000 &&
                _lastSeekTargetMs < length - 5000 &&
                current < length - 5000;
        }

        private void ShowSeekError(string message)
        {
            Buffering.IsActive = false;
            SeekStatusText.Text = message;
            SeekStatusText.Visibility = Visibility.Visible;
            ShowOverlay();
            AppLog.WriteLine("[SeekDiagnostics] error-visible, message=" + message);
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

            if (string.Equals(reason, "time-changed", StringComparison.OrdinalIgnoreCase) &&
                _lastLocalSaveUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _lastLocalSaveUtc).TotalMilliseconds < PlaybackSaveIntervalMs)
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
            _lastLocalSaveUtc = DateTime.UtcNow;

            AppLog.WriteLine(
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
                 Math.Abs(time - _lastServerSavedTime) < ServerSyncIntervalMs))
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

                AppLog.WriteLine(
                    "Playback synced: videoId=" +
                    _args.VideoId +
                    ", libraryId=" + _args.MetaId +
                    ", timeMs=" + time +
                    ", durationMs=" + duration);
            }
            catch (Exception ex)
            {
                AppLog.WriteLine(
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
            var tracks = _player?.AudioTrackDescription;
            LogAudio("event=menu-open, currentTrack=" + (_player?.AudioTrack ?? -1) +
                ", trackCount=" + (tracks?.Length ?? 0) +
                ", passthrough=" + AppServices.Settings.AudioPassthrough);
            if (tracks != null)
            {
                foreach (var track in tracks)
                {
                    LogAudio("event=track, id=" + track.Id +
                        ", name=" + (track.Name ?? "<unnamed>"));
                }
            }

            FillTrackMenu(
                AudioMenu,
                tracks,
                _player?.AudioTrack ?? -1,
                SelectAudioTrack);
        }

        private void SelectAudioTrack(int id)
        {
            if (_player == null)
                return;

            var current = _player.AudioTrack;
            if (current == id)
            {
                LogAudio("event=selection-ignored, reason=already-selected, track=" + id);
                return;
            }

            _audioSwitchRequest++;
            _audioSwitchPending = true;
            _audioSwitchStartedUtc = DateTime.UtcNow;
            LogAudio("event=switch-start, request=" + _audioSwitchRequest +
                ", fromTrack=" + current +
                ", toTrack=" + id +
                ", timeMs=" + _player.Time +
                ", lengthMs=" + _player.Length +
                ", isPlaying=" + _player.IsPlaying);

            try
            {
                var accepted = _player.SetAudioTrack(id);
                LogAudio("event=switch-call-returned, request=" + _audioSwitchRequest +
                    ", accepted=" + accepted +
                    ", currentTrack=" + _player.AudioTrack);
            }
            catch (Exception ex)
            {
                _audioSwitchPending = false;
                LogAudio("event=switch-exception, request=" + _audioSwitchRequest +
                    ", type=" + ex.GetType().Name +
                    ", message=" + ex.Message);
            }
        }

        private void SubtitleMenu_Opening(object sender, object e)
        {
            FillTrackMenu(
                SubtitleMenu,
                _player?.SpuDescription,
                _player?.Spu ?? -1,
                SelectSubtitleTrack);

            SubtitleMenu.Items.Add(new MenuFlyoutSeparator());
            var load = new MenuFlyoutItem { Text = "Load additional subtitles" };
            load.Click += async (s, args) => await LoadAdditionalSubtitlesAsync();
            SubtitleMenu.Items.Add(load);
        }

        private void SelectSubtitleTrack(int id)
        {
            if (_player == null)
                return;

            var wasPlaying = _player.IsPlaying;
            var currentTime = Math.Max(0, _player.Time);
            var accepted = _player.SetSpu(id);

            AppLog.WriteLine("[SubtitleDiagnostics] track selected, id=" + id +
                ", accepted=" + accepted + ", timeMs=" + currentTime +
                ", wasPlaying=" + wasPlaying);
            LogAppMemory("subtitle-track-selected");

            if (!accepted || currentTime <= 0)
                return;

            // LibVLC does not always redraw a subtitle cue that began before
            // the track was selected. Reposition one second locally so the
            // current cue is evaluated without restarting the stream.
            var refreshTime = Math.Max(0, currentTime - 1000);
            _player.Time = refreshTime;
            if (wasPlaying)
                _player.Play();
            else
                _player.SetPause(true);

            AppLog.WriteLine("[SubtitleDiagnostics] track refresh, fromMs=" +
                currentTime + ", toMs=" + refreshTime);
        }

        private async Task LoadAdditionalSubtitlesAsync()
        {
            if (_additionalSubtitlesLoading || _player == null || _args == null)
                return;

            _additionalSubtitlesLoading = true;
            try
            {
                AppLog.WriteLine("[SubtitleDiagnostics] loading additional subtitles, videoId=" +
                    (_args.VideoId ?? ""));

                var subtitles = await AppServices.Addons.GetSubtitlesAsync(
                    _args.Type, _args.VideoId);

                var candidates = (subtitles ?? new List<Subtitle>())
                    .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                    .GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .Where(s => !_loadedSubtitleUrls.Contains(s.Url))
                    .Take(5)
                    .ToList();

                foreach (var subtitle in candidates)
                {
                    _player.AddSlave(MediaSlaveType.Subtitle, subtitle.Url, false);
                    _loadedSubtitleUrls.Add(subtitle.Url);
                }

                AppLog.WriteLine("[SubtitleDiagnostics] additional subtitles loaded, returned=" +
                    (subtitles?.Count ?? 0) + ", attached=" + candidates.Count);
            }
            catch (Exception ex)
            {
                AppLog.WriteLine("[SubtitleDiagnostics] additional subtitle load failed: " +
                    ex.Message);
                ShowError("Could not load additional subtitles: " + ex.Message);
            }
            finally
            {
                _additionalSubtitlesLoading = false;
            }
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
