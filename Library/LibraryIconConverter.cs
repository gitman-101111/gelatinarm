using System;
using System.Collections.Generic;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Library
{
    public class LibraryIconConverter : OneWayConverter
    {
        private const string FolderIcon = "";

        private static readonly Dictionary<BaseItemDto_CollectionType, string> IconByCollectionType = new()
        {
            [BaseItemDto_CollectionType.Movies] = "",
            [BaseItemDto_CollectionType.Tvshows] = "",
            [BaseItemDto_CollectionType.Music] = "",
            [BaseItemDto_CollectionType.Musicvideos] = "",
            [BaseItemDto_CollectionType.Homevideos] = "",
            [BaseItemDto_CollectionType.Books] = "",
            [BaseItemDto_CollectionType.Photos] = "",
            [BaseItemDto_CollectionType.Livetv] = "",
            [BaseItemDto_CollectionType.Playlists] = ""
        };

        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not BaseItemDto library)
            {
                return FolderIcon;
            }

            // No collection type is a mixed library: a folder, whatever its name says
            return library.CollectionType.HasValue && IconByCollectionType.TryGetValue(library.CollectionType.Value, out var icon)
                ? icon
                : FolderIcon;
        }
    }
}
