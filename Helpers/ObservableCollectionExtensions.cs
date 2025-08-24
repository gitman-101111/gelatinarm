using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Gelatinarm.Helpers
{
    /// <summary>
    ///     Extension methods for ObservableCollection to improve performance with batch operations
    /// </summary>
    public static class ObservableCollectionExtensions
    {
        /// <summary>
        ///     Adds multiple items to the collection
        ///     Note: This will fire individual CollectionChanged events for each item
        /// </summary>
        public static void AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
        {
            if (collection == null)
            {
                throw new ArgumentNullException(nameof(collection));
            }

            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                collection.Add(item);
            }
        }

        /// <summary>
        ///     Replaces all items in the collection efficiently
        /// </summary>
        public static void ReplaceAll<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
        {
            if (collection == null)
            {
                throw new ArgumentNullException(nameof(collection));
            }

            var itemsList = items?.ToList() ?? new List<T>();

            // If both are empty, do nothing
            if (collection.Count == 0 && itemsList.Count == 0)
            {
                return;
            }            // This will fire 2 events (one for Clear, one for Reset) instead of N events
            collection.Clear();

            // Add all new items
            foreach (var item in itemsList)
            {
                collection.Add(item);
            }
        }
    }
}
