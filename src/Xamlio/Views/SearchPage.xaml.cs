using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Stremio.Core.Addons;
using Xamlio.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Xamlio.Views
{
    public sealed partial class SearchPage : Page
    {
        private readonly ObservableCollection<MetaPreview> _results = new ObservableCollection<MetaPreview>();
        private int _searchId;

        public SearchPage()
        {
            InitializeComponent();
            Results.ItemsSource = _results;
        }

        private async void Query_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.GamepadMenu)
            {
                e.Handled = true;
                await SearchAsync(Query.Text.Trim());
            }
        }

        private async Task SearchAsync(string text)
        {
            if (text.Length == 0)
                return;

            var id = ++_searchId;
            _results.Clear();
            Message.Visibility = Visibility.Collapsed;
            Loading.IsActive = true;

            await AppServices.EnsureAddonsLoadedAsync();
            var extra = new Dictionary<string, string> { ["search"] = text };
            var seen = new HashSet<string>();
            var tasks = AppServices.Addons.GetCatalogs().Where(c => c.SupportsSearch).Select(async source =>
            {
                try
                {
                    var metas = await AppServices.Addons.Client.GetCatalogAsync(source.Addon, source.Catalog.Type, source.Catalog.Id, extra);
                    if (id != _searchId)
                        return;
                    foreach (var meta in metas.Where(m => seen.Add(m.Type + "/" + m.Id)))
                    {
                        meta.Type = meta.Type ?? source.Catalog.Type;
                        _results.Add(meta);
                    }
                }
                catch (Exception)
                {
                }
            });
            await Task.WhenAll(tasks.ToList());

            if (id != _searchId)
                return;
            Loading.IsActive = false;
            if (_results.Count == 0)
            {
                Message.Text = "No results for \"" + text + "\".";
                Message.Visibility = Visibility.Visible;
            }
            else
            {
                Results.Focus(FocusState.Programmatic);
            }
        }

        private void Results_ItemClick(object sender, ItemClickEventArgs e)
        {
            var meta = (MetaPreview)e.ClickedItem;
            Frame.Navigate(typeof(DetailPage), new MetaArgs { Type = meta.Type, Id = meta.Id });
        }
    }
}
