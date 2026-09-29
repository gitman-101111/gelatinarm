using System;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Gelatinarm.Shared.Base;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.SignIn
{
    public sealed partial class ServerSelectionPage : BasePage
    {
        public ServerSelectionPage() : base(typeof(ServerSelectionPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(ServerSelectionViewModel);
        public new ServerSelectionViewModel ViewModel => (ServerSelectionViewModel)base.ViewModel;

        protected override Control InitialFocusControl => ServerUrlTextBox;

        protected override Task InitializePageAsync(object parameter)
        {
            Bindings.Update();
            return base.InitializePageAsync(parameter);
        }

        protected override bool HandleBackNavigation(BackRequestedEventArgs e)
        {
            // With nothing to go back to (after sign-out), Back must not leave the app
            if (!Frame.CanGoBack)
            {
                e.Handled = true;
                Logger.LogDebug("ServerSelectionPage: Back navigation blocked - no valid back stack");
                return true;
            }

            return false;
        }
    }
}
