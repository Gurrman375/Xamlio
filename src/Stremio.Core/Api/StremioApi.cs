using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Stremio.Core.Addons;

namespace Stremio.Core.Api
{
    public class StremioApi
    {
        public static Action<string> LogWriter { get; set; }
        public const string DefaultEndpoint =
            "https://api.strem.io/api/";

        private readonly HttpClient _http;
        private readonly string _endpoint;

        public StremioApi(
            HttpClient http,
            string endpoint = DefaultEndpoint)
        {
            _http = http;
            _endpoint = endpoint;
        }

        public async Task<LoginResult> LoginAsync(
            string email,
            string password,
            CancellationToken ct = default(CancellationToken))
        {
            var result = await CallAsync(
                "login",
                new JObject
                {
                    ["type"] = "Login",
                    ["email"] = email,
                    ["password"] = password,
                    ["facebook"] = false,
                },
                ct).ConfigureAwait(false);

            return result.ToObject<LoginResult>();
        }

        public Task LogoutAsync(
            string authKey,
            CancellationToken ct = default(CancellationToken))
        {
            return CallAsync(
                "logout",
                new JObject
                {
                    ["type"] = "Logout",
                    ["authKey"] = authKey,
                },
                ct);
        }

        public async Task<List<InstalledAddon>>
            GetAddonCollectionAsync(
                string authKey,
                CancellationToken ct = default(CancellationToken))
        {
            var result = await CallAsync(
                "addonCollectionGet",
                new JObject
                {
                    ["type"] = "AddonCollectionGet",
                    ["authKey"] = authKey,
                    ["update"] = true,
                },
                ct).ConfigureAwait(false);

            var addons = new List<InstalledAddon>();

            foreach (var item in
                (JArray)result["addons"] ?? new JArray())
            {
                var url = (string)item["transportUrl"];
                var manifest =
                    item["manifest"]?.ToObject<Manifest>();

                if (url != null && manifest != null)
                {
                    addons.Add(
                        new InstalledAddon(url, manifest));
                }
            }

            return addons;
        }

        public async Task<List<LibraryItem>> GetLibraryAsync(
            string authKey,
            CancellationToken ct = default(CancellationToken))
        {
            var result = await CallAsync(
                "datastoreGet",
                new JObject
                {
                    ["authKey"] = authKey,
                    ["collection"] = "libraryItem",
                    ["ids"] = new JArray(),
                    ["all"] = true,
                },
                ct).ConfigureAwait(false);

            return result.ToObject<List<LibraryItem>>()
                ?? new List<LibraryItem>();
        }

        public async Task UpdateLibraryPlaybackAsync(
            string authKey,
            string libraryItemId,
            string videoId,
            long timeOffset,
            long duration,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(authKey) ||
                string.IsNullOrWhiteSpace(libraryItemId))
            {
                return;
            }

            var result = await CallAsync(
                "datastoreGet",
                new JObject
                {
                    ["authKey"] = authKey,
                    ["collection"] = "libraryItem",
                    ["ids"] = new JArray(libraryItemId),
                    ["all"] = false,
                },
                ct).ConfigureAwait(false);

            var items = result as JArray;
            JObject libraryItem = null;

            if (items != null)
            {
                foreach (var item in items.OfType<JObject>())
                {
                    if (string.Equals(
                        (string)item["_id"],
                        libraryItemId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        libraryItem = item;
                        break;
                    }
                }
            }

            if (libraryItem == null)
                return;

            var state = libraryItem["state"] as JObject ?? new JObject();
            state["timeOffset"] = timeOffset;
            state["duration"] = duration;
            state["video_id"] = videoId ?? libraryItemId;
            libraryItem["state"] = state;

            await CallAsync(
                "datastorePut",
                new JObject
                {
                    ["authKey"] = authKey,
                    ["collection"] = "libraryItem",
                    ["changes"] = new JArray(libraryItem),
                },
                ct).ConfigureAwait(false);
        }

        public async Task UpdateLibraryWatchedAsync(
            string authKey,
            string libraryItemId,
            string watched,
            bool markWatched = true,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(authKey) ||
                string.IsNullOrWhiteSpace(libraryItemId) ||
                string.IsNullOrWhiteSpace(watched))
            {
                return;
            }

            var result = await CallAsync(
                "datastoreGet",
                new JObject
                {
                    ["authKey"] = authKey,
                    ["collection"] = "libraryItem",
                    ["ids"] = new JArray(libraryItemId),
                    ["all"] = false,
                },
                ct).ConfigureAwait(false);

            var items = result as JArray;
            JObject libraryItem = null;

            if (items != null)
            {
                foreach (var item in items.OfType<JObject>())
                {
                    if (string.Equals(
                        (string)item["_id"],
                        libraryItemId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        libraryItem = item;
                        break;
                    }
                }
            }

            if (libraryItem == null)
                return;

            var state = libraryItem["state"] as JObject ?? new JObject();
            state["watched"] = watched;
            libraryItem["state"] = state;
            libraryItem["_mtime"] = DateTime.UtcNow.ToString("O");

            await CallAsync(
                "datastorePut",
                new JObject
                {
                    ["authKey"] = authKey,
                    ["collection"] = "libraryItem",
                    ["changes"] = new JArray(libraryItem),
                },
                ct).ConfigureAwait(false);

            var verification = await CallAsync(
                "datastoreGet",
                new JObject
                {
                    ["authKey"] = authKey,
                    ["collection"] = "libraryItem",
                    ["ids"] = new JArray(libraryItemId),
                    ["all"] = false,
                },
                ct).ConfigureAwait(false);

            var verifiedItem = (verification as JArray)?
                .OfType<JObject>()
                .FirstOrDefault(item => string.Equals(
                    (string)item["_id"],
                    libraryItemId,
                    StringComparison.OrdinalIgnoreCase));

            var verifiedWatched = (string)verifiedItem?["state"]?["watched"];
            if (!string.Equals(verifiedWatched, watched,
                StringComparison.Ordinal))
            {
                throw new StremioApiException(
                    "Stremio did not confirm the watched-state update.");
            }
        }

        // Step 1:
        // Create a QR login code.
        public async Task<LinkCodeResult> CreateLinkCodeAsync(
            CancellationToken ct = default(CancellationToken))
        {
            const string url =
                "https://link.stremio.com/api/v2/create?=Create";

            var request = new HttpRequestMessage(
                HttpMethod.Get,
                url);

            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Xamlio/1.0");

            request.Headers.TryAddWithoutValidation(
                "Accept",
                "application/json");

            using (var response = await _http.SendAsync(
                request,
                ct).ConfigureAwait(false))
            {
                var text = await response.Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false);

                Log("QR create response status: " + response.StatusCode +
                    ", bytes=" + text.Length);

                response.EnsureSuccessStatusCode();

                var json = JObject.Parse(text);
                var data = json["result"] as JObject ?? json;

                var code = (string)data["code"];
                var link = (string)data["link"];
                var qrcode = (string)data["qrcode"];

                code = code ?? (string)data["Code"];
                link = link ?? (string)data["Link"];
                qrcode = qrcode ?? (string)data["QrCode"];

                if (string.IsNullOrWhiteSpace(code) &&
                    !string.IsNullOrWhiteSpace(link))
                {
                    var lastSlash = link.LastIndexOf('/');

                    if (lastSlash >= 0 &&
                        lastSlash < link.Length - 1)
                    {
                        code = link.Substring(lastSlash + 1);
                    }
                }

                if (string.IsNullOrWhiteSpace(code))
                {
                    throw new InvalidOperationException(
                        "Stremio did not return a login code.");
                }

                return new LinkCodeResult
                {
                    Code = code,
                    Link = link,
                    QrCode = qrcode
                };
            }
        }

        // Step 3:
        // Check whether the user has completed login.
        // A null result means the user has not completed login yet.
        public async Task<LinkAuthKeyResult> ReadLinkCodeAsync(
            string code,
            CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException(
                    "A login code is required.",
                    nameof(code));

            var url =
                "https://link.stremio.com/api/v2/read" +
                "?=Read&code=" +
                Uri.EscapeDataString(code);

            var request = new HttpRequestMessage(
                HttpMethod.Get,
                url);

            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Xamlio/1.0");

            request.Headers.TryAddWithoutValidation(
                "Accept",
                "application/json");

            using (var response = await _http.SendAsync(
                request,
                ct).ConfigureAwait(false))
            {
                var text = await response.Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false);

                Log("QR read response bytes=" + text.Length);

                response.EnsureSuccessStatusCode();

                var json = JObject.Parse(text);
                var data = json["result"] as JObject;

                if (data == null)
                    return null;

                var authKey =
                    (string)data["authKey"] ??
                    (string)data["auth_key"] ??
                    (string)data["AuthKey"];

                if (string.IsNullOrWhiteSpace(authKey))
                    return null;

                return new LinkAuthKeyResult
                {
                    AuthKey = authKey
                };
            }
        }

        // Polls the read endpoint after the QR code has been displayed.
        // Polls every five seconds for up to five minutes.
        public async Task<LinkAuthKeyResult>
            WaitForLinkAuthKeyAsync(
                string code,
                CancellationToken ct = default(CancellationToken))
        {
            const int maximumAttempts = 60;
            const int delayMilliseconds = 5000;

            for (var attempt = 0;
                 attempt < maximumAttempts;
                 attempt++)
            {
                await Task.Delay(
                    delayMilliseconds,
                    ct).ConfigureAwait(false);

                var result = await ReadLinkCodeAsync(
                    code,
                    ct).ConfigureAwait(false);

                if (result != null &&
                    !string.IsNullOrWhiteSpace(result.AuthKey))
                {
                    return result;
                }
            }

            return null;
        }

        private static string RedactSecrets(string jsonText)
        {
            try
            {
                var token = JToken.Parse(jsonText);
                RedactSecrets(token);
                return token.ToString(Formatting.None);
            }
            catch (Exception)
            {
                return jsonText;
            }
        }

        private static void RedactSecrets(JToken token)
        {
            var objectToken = token as JObject;
            if (objectToken != null)
            {
                foreach (var property in objectToken.Properties().ToList())
                {
                    if (string.Equals(property.Name, "authKey",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(property.Name, "password",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(property.Name, "accessToken",
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(property.Name, "transportUrl",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        property.Value = "<redacted>";
                    }
                    else
                    {
                        RedactSecrets(property.Value);
                    }
                }

                return;
            }

            var arrayToken = token as JArray;
            if (arrayToken != null)
            {
                foreach (var child in arrayToken)
                    RedactSecrets(child);
            }
        }

        private async Task<JToken> CallAsync(
            string method,
            JObject body,
            CancellationToken ct)
        {
            var debugBody = (JObject)body.DeepClone();
            if (debugBody["password"] != null)
                debugBody["password"] = "<redacted>";
            if (debugBody["authKey"] != null)
                debugBody["authKey"] = "<redacted>";

            Log(
                "[StremioApi] REQUEST " + method +
                " body=" + debugBody.ToString(Formatting.None));

            var content = new StringContent(
                body.ToString(Formatting.None),
                Encoding.UTF8,
                "application/json");

            using (var response = await _http.PostAsync(
                _endpoint + method,
                content,
                ct).ConfigureAwait(false))
            {
                var text = await response.Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false);

                Log(
                    "[StremioApi] RESPONSE " + method +
                    " status=" + (int)response.StatusCode +
                    " " + response.StatusCode +
                    " bytes=" + text.Length);

                var json = JObject.Parse(text);
                var error = json["error"];

                if (error != null &&
                    error.Type != JTokenType.Null)
                {
                    throw new StremioApiException(
                        (string)error["message"]
                        ?? error.ToString());
                }

                return json["result"];
            }
        }

        private static void Log(string message)
        {
            LogWriter?.Invoke(message);
        }
    }

    public class StremioApiException : Exception
    {
        public StremioApiException(string message)
            : base(message)
        {
        }
    }

    public class LoginResult
    {
        [JsonProperty("authKey")]
        public string AuthKey { get; set; }

        [JsonProperty("user")]
        public User User { get; set; }
    }

    public class LinkCodeResult
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("link")]
        public string Link { get; set; }

        [JsonProperty("qrcode")]
        public string QrCode { get; set; }
    }

    public class LinkAuthKeyResult
    {
        [JsonProperty("authKey")]
        public string AuthKey { get; set; }
    }

    public class User
    {
        [JsonProperty("_id")]
        public string Id { get; set; }

        [JsonProperty("email")]
        public string Email { get; set; }
    }

    public class LibraryItem
    {
        [JsonProperty("_id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("poster")]
        public string Poster { get; set; }

        [JsonProperty("removed")]
        public bool Removed { get; set; }

        [JsonProperty("temp")]
        public bool Temp { get; set; }

        [JsonProperty("_mtime")]
        public DateTime? ModifiedTime { get; set; }

        [JsonProperty("state")]
        public LibraryItemState State { get; set; }
    }

    public class LibraryItemState
    {
        [JsonProperty("timeOffset")]
        public long TimeOffset { get; set; }

        [JsonProperty("duration")]
        public long Duration { get; set; }

        [JsonProperty("video_id")]
        public string VideoId { get; set; }

        [JsonProperty("watched")]
        public string Watched { get; set; }
    }
}
