using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Gelatinarm.Music
{
    public static class ShuffleHelper
    {
        private static int _seed = Environment.TickCount;

        // One Random per thread: Random is not thread-safe, and two created in the same tick
        // would shuffle alike
        private static readonly ThreadLocal<Random> Random =
            new ThreadLocal<Random>(() => new Random(Interlocked.Increment(ref _seed)));

        /// <summary>
        ///     A new list holding <paramref name="items" /> in random order (Fisher-Yates)
        /// </summary>
        public static List<T> Shuffled<T>(IEnumerable<T> items)
        {
            var list = items?.ToList() ?? new List<T>();
            var rng = Random.Value;
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }

            return list;
        }

        public static int RandomIndex(int count)
        {
            return Random.Value.Next(count);
        }
    }
}
