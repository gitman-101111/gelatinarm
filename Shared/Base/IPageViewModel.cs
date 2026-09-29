using System;

namespace Gelatinarm.Shared.Base
{
    // The sign-in pages' view models (server, login, Quick Connect, profile picker): set up from
    // the navigation parameter as the page arrives, and done with when it leaves -- BasePage does both
    public interface IPageViewModel : IDisposable
    {
        void Initialize(object parameter);
    }
}
