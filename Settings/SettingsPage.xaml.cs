using System;
using System.Threading.Tasks;
using Gelatinarm.Shared.Base;

namespace Gelatinarm.Settings
{
    public sealed partial class SettingsPage : BasePage
    {
        public SettingsPage() : base(typeof(SettingsPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(SettingsViewModel);

        public new SettingsViewModel ViewModel => (SettingsViewModel)base.ViewModel;

        protected override Task InitializePageAsync(object parameter)
        {
            return ViewModel.LoadDataAsync(true);
        }
    }
}
