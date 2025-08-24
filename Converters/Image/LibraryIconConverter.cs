using System;
using Jellyfin.Sdk.Generated.Models;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Converters.Image
{
    /// <summary>
    ///     Converts library collection type to icon glyph
    /// </summary>
    public class LibraryIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            // Handle BaseItemDto objects
            if (value is BaseItemDto library)
            {
                // First try CollectionType enum
                if (library.CollectionType.HasValue)
                {
                    switch (library.CollectionType.Value)
                    {
                        case BaseItemDto_CollectionType.Movies:
                            return "\uE8B2"; // Movie icon
                        case BaseItemDto_CollectionType.Tvshows:
                            return "\uE7F4"; // TV icon
                        case BaseItemDto_CollectionType.Music:
                            return "\uE8D6"; // Music icon
                        case BaseItemDto_CollectionType.Homevideos:
                            return "\uE714"; // Video icon
                        case BaseItemDto_CollectionType.Musicvideos:
                            return "\uE93C"; // Music video icon
                    }
                }

                // If no CollectionType or unknown type, check the library name
                if (!string.IsNullOrEmpty(library.Name))
                {
                    var name = library.Name;

                    if (name.Contains("movie", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("film", StringComparison.OrdinalIgnoreCase))
                    {
                        return "\uE8B2"; // Movie icon
                    }

                    if (name.Contains("tv", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("show", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("series", StringComparison.OrdinalIgnoreCase))
                    {
                        return "\uE7F4"; // TV icon
                    }

                    if (name.Contains("music", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("song", StringComparison.OrdinalIgnoreCase))
                    {
                        return "\uE8D6"; // Music icon
                    }

                    if (name.Contains("video", StringComparison.OrdinalIgnoreCase))
                    {
                        return "\uE714"; // Video icon
                    }
                }
            }
            // Handle string collection type
            else if (value is string collectionType)
            {
                return collectionType.ToLower() switch
                {
                    "movies" => "\uE8B2", // Film icon
                    "tvshows" => "\uE7F4", // TV icon
                    "music" => "\uE8D6", // Music note icon
                    "books" => "\uE8A4", // Book icon
                    "homevideos" => "\uE714", // Video camera icon
                    "photos" => "\uEB9F", // Photo icon
                    "livetv" => "\uE787", // Broadcast icon
                    "playlists" => "\uE142", // List icon
                    "channels" => "\uE789", // Channel icon
                    "recordings" => "\uE7C8", // Record icon
                    _ => "\uE8F1" // Folder icon (default)
                };
            }

            return "\uE8F1"; // Default folder icon
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
