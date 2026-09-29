using System.Collections.Generic;
using System.ComponentModel;

namespace Gelatinarm.Library
{
    /// <summary>
    ///     A decade in the filter flyout. It sends nothing itself: checking it checks the years in its
    ///     range, and those are what the query uses.
    /// </summary>
    public class DecadeFilterItem : FilterItem
    {
        private readonly int _startYear;
        private readonly int _endYear;
        private readonly IEnumerable<FilterItem> _years;

        public DecadeFilterItem(string name, int startYear, int endYear, IEnumerable<FilterItem> years) : base(name)
        {
            _startYear = startYear;
            _endYear = endYear;
            _years = years;
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.PropertyName == nameof(IsSelected))
            {
                foreach (var yearItem in _years)
                {
                    if (int.TryParse(yearItem.Name, out var year) && year >= _startYear && year <= _endYear)
                    {
                        yearItem.IsSelected = IsSelected;
                    }
                }
            }
        }
    }
}
