using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stremio.Core.Api;
using Xamlio.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Xamlio.Views
{
    public sealed partial class SettingsPage : Page
    {
        private static readonly string[] Languages =
        {
            "eng",
            "spa",
            "por",
            "fre",
            "ger",
            "ita",
            "dut",
            "pol",
            "rus",
            "tur",
            "ara",
            "hin",
            "jpn",
            "kor",
            "chi"
        };

        private bool _initializing;
        private CancellationTokenSource _qrLoginCts;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(
            NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var settings = AppServices.Settings;

            _initializing = true;

            ServerUrl.Text =
                settings.StreamingServerUrl ?? "";

            SubtitleLang.ItemsSource = Languages;

            SubtitleLang.SelectedItem =
                Languages.Contains(
                    settings.PreferredSubtitleLanguage)
                    ? settings.PreferredSubtitleLanguage
                    : "eng";

            Passthrough.IsOn =
                settings.AudioPassthrough;

            MatchDisplay.IsOn =
                settings.MatchDisplayMode;

            TvSafeMarginsToggle.IsOn =
                settings.UseTvSafeMargin;

            _initializing = false;

            UpdateAccount();

            await AppServices.EnsureAddonsLoadedAsync();

            RefreshAddons();
        }

        protected override void OnNavigatedFrom(
            NavigationEventArgs e)
        {
            _qrLoginCts?.Cancel();
            base.OnNavigatedFrom(e);
        }

        private void UpdateAccount()
        {
            var loggedIn =
                AppServices.Settings.IsLoggedIn;

            LoginPanel.Visibility =
                loggedIn
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            QrLoginButton.Visibility =
                loggedIn
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            QrLoginPanel.Visibility =
                loggedIn
                    ? Visibility.Collapsed
                    : QrLoginPanel.Visibility;

            AccountPanel.Visibility =
                loggedIn
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            AccountText.Text =
                loggedIn
                    ? "Logged in to Stremio"
                    : "";
        }

        private void RefreshAddons()
        {
            AddonList.ItemsSource =
                AppServices.Addons.Addons
                    .Select(addon => addon.Manifest)
                    .ToList();
        }

        private async void Login_Click(
            object sender,
            RoutedEventArgs e)
        {
            LoginButton.IsEnabled = false;
            AccountStatus.Text = "Logging in...";

            try
            {
                var result =
                    await AppServices.Api.LoginAsync(
                        Email.Text.Trim(),
                        Password.Password);

                AppServices.Settings.AuthKey =
                    result.AuthKey;

                AppServices.Settings.Email =
                    result.User?.Email ??
                    Email.Text.Trim();

                Password.Password = "";

                AccountStatus.Text =
                    "Loading your addons...";

                await AppServices.ReloadAddonsAsync();

                AccountStatus.Text =
                    AppServices.Addons.Addons.Count +
                    " addon(s) loaded from your account.";

                RefreshAddons();
            }
            catch (StremioApiException ex)
            {
                AccountStatus.Text =
                    "Login failed: " + ex.Message;
            }
            catch (Exception ex)
            {
                AccountStatus.Text =
                    "Couldn't reach Stremio: " +
                    ex.Message;
            }
            finally
            {
                LoginButton.IsEnabled = true;
                UpdateAccount();
            }
        }

        private async void Logout_Click(
            object sender,
            RoutedEventArgs e)
        {
            var key =
                AppServices.Settings.AuthKey;

            AppServices.Settings.AuthKey = null;
            AppServices.Settings.Email = null;

            UpdateAccount();

            AccountStatus.Text = "";

            try
            {
                await AppServices.Api.LogoutAsync(key);
            }
            catch
            {
                // Continue logging out locally.
            }

            await AppServices.ReloadAddonsAsync();

            RefreshAddons();
        }

        private void ServerUrl_LostFocus(
            object sender,
            RoutedEventArgs e)
        {
            AppServices.Settings.StreamingServerUrl =
                ServerUrl.Text.Trim();
        }

        private void SubtitleLang_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (!_initializing &&
                SubtitleLang.SelectedItem is string language)
            {
                AppServices.Settings
                    .PreferredSubtitleLanguage = language;
            }
        }

        private void Passthrough_Toggled(
            object sender,
            RoutedEventArgs e)
        {
            if (!_initializing)
            {
                AppServices.Settings.AudioPassthrough =
                    Passthrough.IsOn;
            }
        }

        private void MatchDisplay_Toggled(
            object sender,
            RoutedEventArgs e)
        {
            if (!_initializing)
            {
                AppServices.Settings.MatchDisplayMode =
                    MatchDisplay.IsOn;
            }
        }

        private void TvSafeMarginsToggle_Toggled(
            object sender,
            RoutedEventArgs e)
        {
            if (_initializing)
                return;

            AppServices.Settings.UseTvSafeMargin =
                TvSafeMarginsToggle.IsOn;

            var shell =
                App.RootFrame?.Content as ShellPage;

            shell?.ApplyDisplayMargins();
        }

        private async void AddAddon_Click(
            object sender,
            RoutedEventArgs e)
        {
            var url =
                AddonUrl.Text.Trim();

            if (url.Length == 0)
                return;

            AddonStatus.Text =
                "Checking addon...";

            try
            {
                var manifest =
                    await AppServices.Addons.Client
                        .GetManifestAsync(url);

                if (manifest == null)
                {
                    AddonStatus.Text =
                        "That URL didn't return an addon manifest.";

                    return;
                }

                var urls =
                    AppServices.Settings.ExtraAddonUrls;

                urls.Add(url);

                AppServices.Settings.ExtraAddonUrls =
                    urls;

                await AppServices.ReloadAddonsAsync();

                RefreshAddons();

                AddonUrl.Text = "";

                AddonStatus.Text =
                    "Added " + manifest.Name + ".";
            }
            catch (Exception ex)
            {
                AddonStatus.Text =
                    "Couldn't add addon: " +
                    ex.Message;
            }
        }

        private async void QrLogin_Click(
            object sender,
            RoutedEventArgs e)
        {
            _qrLoginCts?.Cancel();
            _qrLoginCts =
                new CancellationTokenSource();

            var token =
                _qrLoginCts.Token;

            QrLoginButton.IsEnabled = false;
            QrLoginPanel.Visibility =
                Visibility.Visible;

            QrLoginStatus.Text =
                "Creating login code...";

            QrCodeImage.Source = null;
            QrCodeText.Text = "";

            try
            {
                var result =
                    await AppServices.Api
                        .CreateLinkCodeAsync(token);

                if (result == null ||
                    string.IsNullOrWhiteSpace(
                        result.Code))
                {
                    QrLoginStatus.Text =
                        "Stremio did not return a login code.";

                    return;
                }

                var qrUrl = result.QrCode;

                if (string.IsNullOrWhiteSpace(qrUrl))
                {
                    qrUrl =
                        "https://link.stremio.com/qr?data=" +
                        Uri.EscapeDataString(result.Code);
                }

                QrCodeImage.Source =
                    new BitmapImage(
                        new Uri(
                            qrUrl,
                            UriKind.Absolute));

                QrCodeText.Text =
                    "Code: " + result.Code;

                QrLoginStatus.Text =
                    "Scan the QR code or open the link on another device.";

                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        token);

                    var authResult =
                        await AppServices.Api
                            .ReadLinkCodeAsync(
                                result.Code,
                                token);

                    if (authResult == null ||
                        string.IsNullOrWhiteSpace(
                            authResult.AuthKey))
                    {
                        continue;
                    }

                    AppServices.Settings.AuthKey =
                        authResult.AuthKey;

                    await AppServices.ReloadAddonsAsync();

                    QrLoginStatus.Text =
                        "Login successful.";

                    QrLoginPanel.Visibility =
                        Visibility.Collapsed;

                    UpdateAccount();
                    RefreshAddons();

                    break;
                }
            }
            catch (OperationCanceledException)
            {
                QrLoginStatus.Text =
                    "QR login canceled.";
            }
            catch (Exception ex)
            {
                QrLoginStatus.Text =
                    "QR login failed: " +
                    ex.Message;
            }
            finally
            {
                QrLoginButton.IsEnabled = true;
            }
        }

        private void CancelQrLogin_Click(
            object sender,
            RoutedEventArgs e)
        {
            _qrLoginCts?.Cancel();

            QrLoginPanel.Visibility =
                Visibility.Collapsed;

            QrLoginStatus.Text = "";
        }
    }
}