using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Gelatinarm.Shared.Base;

namespace Gelatinarm.SignIn
{
    public sealed partial class LoginPage : BasePage
    {
        public LoginPage() : base(typeof(LoginPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(LoginViewModel);
        public new LoginViewModel ViewModel => (LoginViewModel)base.ViewModel;

        protected override Control InitialFocusControl => UsernameTextBox;

        protected override Task InitializePageAsync(object parameter)
        {
            Bindings.Update();
            return base.InitializePageAsync(parameter);
        }
    }
}
