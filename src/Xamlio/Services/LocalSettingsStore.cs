using Stremio.Core.Settings;
using Windows.Storage;

namespace Xamlio.Services
{
    public class LocalSettingsStore : ISettingsStore
    {
        private readonly ApplicationDataContainer _container = ApplicationData.Current.LocalSettings;

        public string Get(string key)
        {
            return _container.Values.TryGetValue(key, out var value) ? value as string : null;
        }

        public void Set(string key, string value)
        {
            if (value == null)
                _container.Values.Remove(key);
            else
                _container.Values[key] = value;
        }
    }
}
