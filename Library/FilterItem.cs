using CommunityToolkit.Mvvm.ComponentModel;

namespace Gelatinarm.Library
{
    /// <summary>
    ///     One option in the library's filter flyout: its name is also the value sent to the server
    /// </summary>
    public partial class FilterItem : ObservableObject
    {
        [ObservableProperty] private bool _isSelected;

        public FilterItem(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }
}
