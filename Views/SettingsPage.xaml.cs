using System;
using System.Threading.Tasks;
using Gelatinarm.ViewModels;

namespace Gelatinarm.Views
{
    public sealed partial class SettingsPage : BasePage
    {
        static SettingsPage()
        {
        }

        public SettingsPage() : base(typeof(SettingsPage))
        {
            InitializeComponent();
        }

        // Specify the ViewModel type for automatic initialization
        protected override Type ViewModelType => typeof(SettingsViewModel);

        // Typed property for easy access
        public SettingsViewModel TypedViewModel => (SettingsViewModel)ViewModel;

        protected override async Task InitializePageAsync(object parameter)
        {
            await TypedViewModel.InitializeAsync();
        }
    }
}
