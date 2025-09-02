using CommunityToolkit.Mvvm.ComponentModel;

namespace Gelatinarm.Models
{
    public class FilterItem : ObservableObject
    {
        private int _count = 0;

        private bool _isSelected = false;
        private string _name;

        private string _value;

        public FilterItem()
        {
        }

        public FilterItem(string name, string value = null, bool isSelected = false)
        {
            Name = name;
            Value = value ?? name;
            IsSelected = isSelected;
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string Value
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public int Count
        {
            get => _count;
            set => SetProperty(ref _count, value);
        }
    }
}
