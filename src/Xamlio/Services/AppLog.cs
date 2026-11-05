using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Xamlio.Services
{
    public static class AppLog
    {
        private const ulong MaxLogBytes = 5 * 1024 * 1024;
        private static readonly SemaphoreSlim WriteGate = new SemaphoreSlim(1, 1);
        private static readonly object Sync = new object();
        private static StorageFile _file;
        private static Task _fileTask;

        public static void Initialize()
        {
            EnsureFileAsync();
        }

        public static void WriteLine(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var line = DateTime.UtcNow.ToString("O") + " " + message;
            if (line.Length > 8000)
                line = line.Substring(0, 8000) + " ...[truncated]";

            _ = AppendAsync(line);
        }

        private static Task EnsureFileAsync()
        {
            lock (Sync)
            {
                if (_fileTask == null)
                    _fileTask = CreateFileAsync();
                return _fileTask;
            }
        }

        private static async Task CreateFileAsync()
        {
            var name = "Xamlio_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".txt";
            _file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                name,
                CreationCollisionOption.OpenIfExists);
        }

        private static async Task AppendAsync(string line)
        {
            try
            {
                await EnsureFileAsync();
                await WriteGate.WaitAsync();
                try
                {
                    var properties = await _file.GetBasicPropertiesAsync();
                    if (properties.Size >= MaxLogBytes)
                        return;

                    await FileIO.AppendTextAsync(_file, line + Environment.NewLine);
                }
                finally
                {
                    WriteGate.Release();
                }
            }
            catch
            {
                // Diagnostics must never affect playback or navigation.
            }
        }
    }
}
