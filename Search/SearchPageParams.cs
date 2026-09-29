using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Search
{
    public class SearchPageParams
    {
        // The library the search was opened from: its kind of item is searched first
        public BaseItemDto_CollectionType? LibraryType { get; set; }
    }
}
