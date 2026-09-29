using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;

namespace Gelatinarm.Details
{
    public abstract class DetailsPage : BasePage
    {
        protected DetailsPage(Type loggerType) : base(loggerType)
        {
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            try
            {
                base.OnNavigatedTo(e);

                if (e.NavigationMode == NavigationMode.Back)
                {
                    await InitializeViewModelAsync(null);
                }
                else
                {
                    await InitializeViewModelAsync(e.Parameter);
                }

                // A controller user starts on the page's first action (Play, Resume, or what the
                // page names in OnMoveToContentArea); nothing else puts focus on the content
                await UiHelper.WhenIdleAsync(Dispatcher);
                MoveToContentArea();
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnNavigatedTo", ErrorCategory.User), false);
            }
        }

        protected virtual Task InitializeViewModelAsync(object parameter)
        {
            return InitializeFromParameterAsync(parameter);
        }

        /// <summary>
        ///     Initializes the view model from the navigation parameter, or on back navigation from
        ///     the saved one. False when there was neither.
        /// </summary>
        protected async Task<bool> InitializeFromParameterAsync(object parameter)
        {
            var resolvedParameter = ResolveNavigationParameter(parameter);
            if (resolvedParameter == null || ViewModel is not IItemDetailsViewModel viewModel)
            {
                return false;
            }

            await viewModel.InitializeAsync(resolvedParameter);
            return true;
        }

        // By name: the pages' own generated PlayButton and ResumeButton fields are not visible here
        private void MoveToContentArea()
        {
            if (FindName("PlayButton") is Button playButton && playButton.Visibility == Visibility.Visible)
            {
                playButton.Focus(FocusState.Programmatic);
            }
            else if (FindName("ResumeButton") is Button resumeButton && resumeButton.Visibility == Visibility.Visible)
            {
                resumeButton.Focus(FocusState.Programmatic);
            }
            else
            {
                OnMoveToContentArea();
            }
        }

        protected virtual void OnMoveToContentArea()
        {
        }
    }
}
