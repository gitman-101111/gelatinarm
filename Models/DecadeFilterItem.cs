using System.Collections.Generic;
using System.ComponentModel;

namespace Gelatinarm.Models
{
    public class DecadeFilterItem : FilterItem
    {
        private int _endYear;
        private int _startYear;

        public DecadeFilterItem()
        {
        }

        public DecadeFilterItem(string name, int startYear, int endYear) : base(name)
        {
            StartYear = startYear;
            EndYear = endYear;
            // Value will contain all years in the range
            UpdateValue();
        }

        public int StartYear
        {
            get => _startYear;
            set => SetProperty(ref _startYear, value);
        }

        public int EndYear
        {
            get => _endYear;
            set => SetProperty(ref _endYear, value);
        }

        public IEnumerable<FilterItem> YearsCollection { get; set; }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            // Update the associated years when selection changes
            if (e.PropertyName == nameof(IsSelected) && YearsCollection != null)
            {
                UpdateYearSelections();
            }
        }

        private void UpdateValue()
        {
            var years = new List<string>();
            for (var year = StartYear; year <= EndYear; year++)
            {
                years.Add(year.ToString());
            }

            Value = string.Join(",", years);
        }

        private void UpdateYearSelections()
        {
            if (YearsCollection == null)
            {
                return;
            }

            foreach (var yearItem in YearsCollection)
            {
                if (int.TryParse(yearItem.Value, out var year))
                {
                    if (year >= StartYear && year <= EndYear)
                    {
                        yearItem.IsSelected = IsSelected;
                    }
                }
            }
        }
    }
}
