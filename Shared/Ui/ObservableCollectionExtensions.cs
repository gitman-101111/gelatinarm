using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Gelatinarm.Shared.Ui
{
    public static class ObservableCollectionExtensions
    {
        /// <summary>
        ///     Replaces the collection's contents (a Reset, then one Add per item)
        /// </summary>
        public static void ReplaceAll<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
        {
            if (collection == null)
            {
                throw new ArgumentNullException(nameof(collection));
            }

            var itemsList = items?.ToList() ?? new List<T>();

            if (collection.Count == 0 && itemsList.Count == 0)
            {
                return;
            }

            collection.Clear();

            foreach (var item in itemsList)
            {
                collection.Add(item);
            }
        }
    }
}
