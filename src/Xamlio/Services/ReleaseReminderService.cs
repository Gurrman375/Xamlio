using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Stremio.Core.Addons;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Xamlio.Services
{
    public sealed class ReleaseReminder
    {
        [JsonProperty("mediaId")]
        public string MediaId { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("poster")]
        public string Poster { get; set; }

        [JsonProperty("occurrences")]
        public List<ReleaseReminderOccurrence> Occurrences { get; set; } =
            new List<ReleaseReminderOccurrence>();
    }

    public sealed class ReleaseReminderOccurrence
    {
        [JsonProperty("videoId")]
        public string VideoId { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("releaseTime")]
        public DateTimeOffset ReleaseTime { get; set; }
    }

    public sealed class ReleaseReminderResult
    {
        public bool Succeeded { get; set; }
        public int OccurrenceCount { get; set; }
        public int ScheduledCount { get; set; }
        public string Message { get; set; }
    }

    public sealed class ReleaseCalendarEntry
    {
        public string MediaId { get; set; }
        public string Type { get; set; }
        public string ShowTitle { get; set; }
        public string Title { get; set; }
        public string Poster { get; set; }
        public string VideoId { get; set; }
        public DateTimeOffset ReleaseTime { get; set; }

        public bool IsEpisode => !string.IsNullOrWhiteSpace(VideoId);

        public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
            ? ShowTitle
            : Title;
    }

    public static class ReleaseReminderService
    {
        private const string NotificationTagPrefix = "xrl";
        private const int DefaultDateOnlyHour = 9;

        public static string LastSchedulingStatus { get; private set; }

        public static bool IsReminderSet(string mediaId)
        {
            return !string.IsNullOrWhiteSpace(mediaId) &&
                LoadReminders().Any(reminder =>
                    string.Equals(reminder.MediaId, mediaId,
                        StringComparison.OrdinalIgnoreCase));
        }

        public static List<ReleaseCalendarEntry> GetCalendarEntries()
        {
            return LoadReminders()
                .SelectMany(reminder => (reminder.Occurrences ??
                    new List<ReleaseReminderOccurrence>())
                    .Select(occurrence => new ReleaseCalendarEntry
                    {
                        MediaId = reminder.MediaId,
                        Type = reminder.Type,
                        ShowTitle = reminder.Title,
                        Title = occurrence.Title,
                        Poster = reminder.Poster,
                        VideoId = occurrence.VideoId,
                        ReleaseTime = occurrence.ReleaseTime
                    }))
                .OrderBy(entry => entry.ReleaseTime)
                .ThenBy(entry => entry.ShowTitle)
                .ToList();
        }

        public static void UpdateReminderPoster(string mediaId, string poster)
        {
            if (string.IsNullOrWhiteSpace(mediaId) ||
                string.IsNullOrWhiteSpace(poster))
                return;

            var reminders = LoadReminders();
            var reminder = reminders.FirstOrDefault(saved =>
                string.Equals(saved.MediaId, mediaId,
                    StringComparison.OrdinalIgnoreCase));
            if (reminder == null || string.Equals(reminder.Poster, poster,
                StringComparison.OrdinalIgnoreCase))
                return;

            reminder.Poster = poster;
            SaveReminders(reminders);
        }

        public static ReleaseReminderResult SetReminder(
            MetaDetail meta,
            string type)
        {
            if (meta == null || string.IsNullOrWhiteSpace(meta.Id))
            {
                return new ReleaseReminderResult
                {
                    Message = "This title does not have enough information for a reminder."
                };
            }

            if (!AppServices.Settings.ReleaseRemindersEnabled)
            {
                return new ReleaseReminderResult
                {
                    Message = "Release notifications are disabled in Settings."
                };
            }

            var reminder = new ReleaseReminder
            {
                MediaId = meta.Id,
                Type = type,
                Title = meta.Name ?? meta.Id,
                Poster = meta.Poster,
                Occurrences = BuildOccurrences(meta, type)
            };

            AppLog.WriteLine(
                "[ReleaseReminder] Found " + reminder.Occurrences.Count +
                " future release date(s) for " + (meta.Name ?? meta.Id) +
                "; metadata videos=" + (meta.Videos?.Count ?? 0) + ".");

            var reminders = LoadReminders();
            reminders.RemoveAll(existing =>
                string.Equals(existing.MediaId, reminder.MediaId,
                    StringComparison.OrdinalIgnoreCase));
            reminders.Add(reminder);
            SaveReminders(reminders);

            var scheduledCount = RefreshScheduledNotifications();
            var message = reminder.Occurrences.Count == 0
                ? "Reminder saved. An exact release date is not available yet."
                : scheduledCount == 0
                    ? "Found " + reminder.Occurrences.Count +
                      " release date(s), but " +
                      (LastSchedulingStatus ??
                          "Windows did not accept the scheduled notification.")
                : scheduledCount == 1
                    ? "Reminder scheduled for " +
                      FormatReleaseTime(reminder.Occurrences[0].ReleaseTime) + "."
                    : scheduledCount + " release reminders scheduled.";

            return new ReleaseReminderResult
            {
                Succeeded = true,
                OccurrenceCount = reminder.Occurrences.Count,
                ScheduledCount = scheduledCount,
                Message = message
            };
        }

        public static void RemoveReminder(string mediaId)
        {
            if (string.IsNullOrWhiteSpace(mediaId))
                return;

            var reminders = LoadReminders();
            reminders.RemoveAll(reminder =>
                string.Equals(reminder.MediaId, mediaId,
                    StringComparison.OrdinalIgnoreCase));
            SaveReminders(reminders);
            RefreshScheduledNotifications();
        }

        public static int RefreshScheduledNotifications()
        {
            try
            {
                LastSchedulingStatus = null;
                var notifier = ToastNotificationManager.CreateToastNotifier();
                if (notifier.Setting != NotificationSetting.Enabled)
                {
                    LastSchedulingStatus =
                        "Windows notifications are unavailable (" +
                        notifier.Setting + ").";
                    AppLog.WriteLine(
                        "[ReleaseReminder] " + LastSchedulingStatus);
                    return 0;
                }

                var scheduled = notifier.GetScheduledToastNotifications().ToList();
                foreach (var notification in scheduled)
                {
                    if (!string.IsNullOrWhiteSpace(notification.Tag) &&
                        notification.Tag.StartsWith(NotificationTagPrefix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        notifier.RemoveFromSchedule(notification);
                    }
                }

                if (!AppServices.Settings.ReleaseRemindersEnabled)
                    return 0;

                var now = DateTimeOffset.Now;
                var count = 0;
                foreach (var reminder in LoadReminders())
                {
                    foreach (var occurrence in reminder.Occurrences ??
                        new List<ReleaseReminderOccurrence>())
                    {
                        if (occurrence.ReleaseTime <= now.AddMinutes(1))
                            continue;

                        var title = string.IsNullOrWhiteSpace(occurrence.Title)
                            ? reminder.Title
                            : occurrence.Title;
                        var toast = CreateToast(
                            title,
                            occurrence.ReleaseTime,
                            string.Equals(reminder.Type, "series",
                                StringComparison.OrdinalIgnoreCase),
                            reminder.Type,
                            reminder.MediaId);
                        var scheduledToast = new ScheduledToastNotification(
                            toast,
                            occurrence.ReleaseTime.ToLocalTime());
                        // ScheduledToastNotification.Id is backed by the
                        // platform's developer-id field and is size-limited.
                        // Use the documented short Tag/Group key instead.
                        scheduledToast.Tag = NotificationTagPrefix +
                            Guid.NewGuid().ToString("N").Substring(0, 13);
                        scheduledToast.Group = "xamlio";
                        try
                        {
                            notifier.AddToSchedule(scheduledToast);
                            count++;
                        }
                        catch (Exception ex)
                        {
                            LastSchedulingStatus =
                                "Windows rejected a scheduled notification.";
                            AppLog.WriteLine(
                                "[ReleaseReminder] Could not schedule '" +
                                title + "' for " + occurrence.ReleaseTime.ToString("O") +
                                ": " + ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                }

                AppLog.WriteLine(
                    "[ReleaseReminder] Scheduled " + count + " notification(s).");
                return count;
            }
            catch (Exception ex)
            {
                AppLog.WriteLine(
                    "[ReleaseReminder] Scheduling failed: " +
                    ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
        }

        public static string SendTestNotification()
        {
            try
            {
                var notifier = ToastNotificationManager.CreateToastNotifier();
                if (notifier.Setting != NotificationSetting.Enabled)
                {
                    var unavailable =
                        "Windows notifications are unavailable (" +
                        notifier.Setting + ").";
                    AppLog.WriteLine("[ReleaseReminder] " + unavailable);
                    return unavailable;
                }

                var reminder = LoadReminders()
                    .Where(saved => saved.Occurrences != null)
                    .OrderBy(saved => saved.Occurrences
                        .Where(occurrence => occurrence.ReleaseTime >
                            DateTimeOffset.Now)
                        .Select(occurrence => occurrence.ReleaseTime)
                        .DefaultIfEmpty(DateTimeOffset.MaxValue)
                        .First())
                    .FirstOrDefault();
                var testOccurrence = reminder?.Occurrences?
                    .Where(saved => saved.ReleaseTime > DateTimeOffset.Now)
                    .OrderBy(saved => saved.ReleaseTime)
                    .FirstOrDefault();
                var title = reminder?.Title ?? "Example show";
                var isEpisode = testOccurrence != null &&
                    !string.IsNullOrWhiteSpace(testOccurrence.VideoId);
                var episodeTitle = testOccurrence == null
                    ? "New episode"
                    : testOccurrence.Title ?? "New episode";
                var releaseTime = testOccurrence?.ReleaseTime ??
                    DateTimeOffset.Now.AddMinutes(1);
                var launchArguments = reminder == null
                    ? null
                    : BuildToastLaunchArguments(
                        reminder.Type,
                        reminder.MediaId);
                var document = CreateTestToast(
                    title,
                    episodeTitle,
                    releaseTime,
                    isEpisode,
                    launchArguments);
                var notification = new ToastNotification(document)
                {
                    Tag = "xrltest",
                    Group = "xamlio"
                };
                notification.Activated += TestNotification_Activated;
                notifier.Show(notification);
                AppLog.WriteLine(
                    "[ReleaseReminder] Test notification sent for " + title + ".");
                return reminder == null
                    ? "Test notification sent (sample title; no saved reminder to open)."
                    : "Test notification sent for " + title + ". Select it to open the title.";
            }
            catch (Exception ex)
            {
                var failure =
                    "Test notification failed: " + ex.GetType().Name +
                    ": " + ex.Message;
                AppLog.WriteLine("[ReleaseReminder] " + failure);
                return failure;
            }
        }

        private static void TestNotification_Activated(
            ToastNotification sender,
            object args)
        {
            var activatedArgs = args as ToastActivatedEventArgs;
            var argument = activatedArgs?.Arguments;
            AppLog.WriteLine(
                "[Toast] Running-app activation argument: " +
                (argument ?? "<null>") + ".");
            App.HandleToastActivation(argument);
        }

        private static List<ReleaseReminderOccurrence> BuildOccurrences(
            MetaDetail meta,
            string type)
        {
            var occurrences = new List<ReleaseReminderOccurrence>();
            var now = DateTimeOffset.Now;

            if (string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase) &&
                TryParseRelease(meta.ReleaseInfo, out var movieRelease) &&
                movieRelease > now)
            {
                occurrences.Add(new ReleaseReminderOccurrence
                {
                    Title = meta.Name ?? meta.Id,
                    ReleaseTime = movieRelease
                });
            }

            var videos = meta.Videos ?? new List<Video>();
            foreach (var video in videos)
            {
                if (!TryParseRelease(video.Released, out var release))
                {
                    if (!string.IsNullOrWhiteSpace(video.Released))
                    {
                        AppLog.WriteLine(
                            "[ReleaseReminder] Could not parse release date: " +
                            video.Released);
                    }
                    continue;
                }

                if (release <= now)
                    continue;

                var episodeTitle = video.DisplayTitle;
                occurrences.Add(new ReleaseReminderOccurrence
                {
                    VideoId = video.Id,
                    Title = string.IsNullOrWhiteSpace(episodeTitle)
                        ? meta.Name ?? meta.Id
                        : (meta.Name ?? meta.Id) + " — " + episodeTitle,
                    ReleaseTime = release
                });
            }

            return occurrences
                .OrderBy(occurrence => occurrence.ReleaseTime)
                .GroupBy(occurrence =>
                    (occurrence.VideoId ?? "movie") + "|" +
                    occurrence.ReleaseTime.ToUniversalTime().Ticks)
                .Select(group => group.First())
                .ToList();
        }

        private static bool TryParseRelease(
            string value,
            out DateTimeOffset releaseTime)
        {
            releaseTime = default(DateTimeOffset);
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();
            if (value.Length == 4 && int.TryParse(value, out _))
                return false;

            var hasTime = value.Contains("T") || value.Contains(":");
            if (hasTime && value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                var explicitUtcOffset = value.Substring(0, value.Length - 1) +
                    "+00:00";
                if (DateTimeOffset.TryParse(
                    explicitUtcOffset,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out releaseTime))
                {
                    return true;
                }
            }

            var styles = hasTime
                ? DateTimeStyles.AllowWhiteSpaces |
                  DateTimeStyles.AssumeUniversal |
                  DateTimeStyles.AdjustToUniversal
                : DateTimeStyles.AllowWhiteSpaces |
                  DateTimeStyles.AssumeLocal;

            var formats = new[]
            {
                "o",
                "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
                "yyyy-MM-dd",
                "yyyy/MM/dd",
                "MM/dd/yyyy",
                "MMM d, yyyy",
                "MMMM d, yyyy"
            };

            if (!DateTimeOffset.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                styles,
                out releaseTime) &&
                !DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    styles,
                    out releaseTime))
            {
                return false;
            }

            if (!hasTime)
            {
                var localDate = releaseTime.ToLocalTime().Date.AddHours(
                    DefaultDateOnlyHour);
                releaseTime = new DateTimeOffset(
                    DateTime.SpecifyKind(localDate, DateTimeKind.Local));
            }

            return true;
        }

        private static XmlDocument CreateToast(
            string title,
            DateTimeOffset releaseTime,
            bool isEpisode,
            string type,
            string mediaId)
        {
            var document = new XmlDocument();
            var releaseText = FormatReleaseTime(releaseTime);
            var heading = isEpisode
                ? "New episode release"
                : "Release reminder";
            var body = title + " releases " + releaseText + ".";
            var launchArguments = BuildToastLaunchArguments(type, mediaId);
            document.LoadXml(
                "<toast launch=\"" + EscapeXml(launchArguments) + "\"><visual><binding template=\"ToastGeneric\">" +
                "<text>" + EscapeXml(heading) + "</text>" +
                "<text>" + EscapeXml(body) + "</text>" +
                "</binding></visual></toast>");
            return document;
        }

        private static XmlDocument CreateTestToast(
            string title,
            string episodeTitle,
            DateTimeOffset releaseTime,
            bool isEpisode,
            string launchArguments)
        {
            var document = new XmlDocument();
            var launch = string.IsNullOrWhiteSpace(launchArguments)
                ? string.Empty
                : " launch=\"" + EscapeXml(launchArguments) + "\"";
            var releaseLabel = isEpisode
                ? episodeTitle + " releases " + FormatReleaseTime(releaseTime) + "."
                : "Releases " + FormatReleaseTime(releaseTime) + ".";
            document.LoadXml(
                "<toast" + launch + "><visual><binding template=\"ToastGeneric\">" +
                "<text>" + EscapeXml(isEpisode
                    ? "New episode release"
                    : "Movie release reminder") + "</text>" +
                "<text>" + EscapeXml(title) + "</text>" +
                "<text>" + EscapeXml(releaseLabel) + "</text>" +
                "</binding></visual></toast>");
            return document;
        }

        private static string BuildToastLaunchArguments(
            string type,
            string mediaId)
        {
            return "type=" + Uri.EscapeDataString(type ?? "series") +
                "&id=" + Uri.EscapeDataString(mediaId ?? string.Empty);
        }

        private static string FormatReleaseTime(DateTimeOffset releaseTime)
        {
            return releaseTime.ToLocalTime().ToString(
                "dddd, MMMM d 'at' h:mm tt", CultureInfo.CurrentCulture);
        }

        private static string EscapeXml(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static List<ReleaseReminder> LoadReminders()
        {
            var json = AppServices.Settings.ReleaseRemindersJson;
            if (string.IsNullOrWhiteSpace(json))
                return new List<ReleaseReminder>();

            try
            {
                return JsonConvert.DeserializeObject<List<ReleaseReminder>>(json) ??
                    new List<ReleaseReminder>();
            }
            catch (Exception ex)
            {
                AppLog.WriteLine(
                    "[ReleaseReminder] Could not read saved reminders: " + ex.Message);
                return new List<ReleaseReminder>();
            }
        }

        private static void SaveReminders(List<ReleaseReminder> reminders)
        {
            AppServices.Settings.ReleaseRemindersJson =
                JsonConvert.SerializeObject(reminders ?? new List<ReleaseReminder>());
        }
    }
}
