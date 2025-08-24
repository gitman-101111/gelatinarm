using System;
using System.Threading.Tasks;
using Gelatinarm.ViewModels;
using Windows.UI.Xaml;

namespace Gelatinarm.Views
{
    public sealed partial class QuickConnectInstructionsPage : BasePage
    {
        public QuickConnectInstructionsPage() : base(typeof(QuickConnectInstructionsPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(QuickConnectInstructionsViewModel);
        public QuickConnectInstructionsViewModel ViewModel => (QuickConnectInstructionsViewModel)base.ViewModel;

        protected override async Task InitializePageAsync(object parameter)
        {
            Bindings.Update();

            // Initialize ViewModel with parameters
            await ViewModel.InitializeAsync(parameter);

            // Set focus for Xbox controller
            BackButton.Focus(FocusState.Programmatic);
        }

        protected override void CleanupResources()
        {
            // Clean up ViewModel
            ViewModel?.Cleanup();

            // Dispose ViewModel
            ViewModel?.Dispose();
        }
    }
}
