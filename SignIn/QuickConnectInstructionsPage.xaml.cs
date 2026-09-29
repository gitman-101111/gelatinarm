using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Gelatinarm.Shared.Base;

namespace Gelatinarm.SignIn
{
    public sealed partial class QuickConnectInstructionsPage : BasePage
    {
        public QuickConnectInstructionsPage() : base(typeof(QuickConnectInstructionsPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(QuickConnectInstructionsViewModel);
        public new QuickConnectInstructionsViewModel ViewModel => (QuickConnectInstructionsViewModel)base.ViewModel;

        protected override Control InitialFocusControl => CancelButton;

        protected override Task InitializePageAsync(object parameter)
        {
            Bindings.Update();
            return base.InitializePageAsync(parameter);
        }
    }
}
