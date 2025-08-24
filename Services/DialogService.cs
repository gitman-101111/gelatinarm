using System;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Popups;
using Windows.UI.Xaml.Controls;

namespace Gelatinarm.Services
{
    public class DialogService : BaseService, IDialogService
    {
        public DialogService(ILogger<DialogService> logger) : base(logger)
        {
        }

        public async Task ShowErrorAsync(string title, string message)
        {
            var context = CreateErrorContext("ShowError", ErrorCategory.User, ErrorSeverity.Warning);
            try
            {
                var dialog = new MessageDialog(message, title);
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context,
                    false); // Don't show error message when dialog itself fails
            }
        }


        public async Task<bool> ShowConfirmationAsync(string title, string message)
        {
            var context = CreateErrorContext("ShowConfirmation", ErrorCategory.User, ErrorSeverity.Warning);
            try
            {
                var dialog = new MessageDialog(message, title);
                dialog.Commands.Add(new UICommand("Yes", null, true));
                dialog.Commands.Add(new UICommand("No", null, false));
                dialog.DefaultCommandIndex = 0;
                dialog.CancelCommandIndex = 1;

                var result = await dialog.ShowAsync();
                return (bool)result.Id;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, false, false);
            }
        }

        public async Task ShowMessageAsync(string message, string title)
        {
            var context = CreateErrorContext("ShowMessage", ErrorCategory.User, ErrorSeverity.Warning);
            try
            {
                var dialog = new MessageDialog(message, title);
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public async Task<ContentDialogResult> ShowCustomAsync(string title, string message,
            string primaryButtonText = null, string secondaryButtonText = null, string closeButtonText = null)
        {
            var context = CreateErrorContext("ShowCustom", ErrorCategory.User, ErrorSeverity.Warning);
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = primaryButtonText ?? string.Empty,
                    SecondaryButtonText = secondaryButtonText ?? string.Empty,
                    CloseButtonText = closeButtonText ?? "Close"
                };

                return await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, ContentDialogResult.None, false);
            }
        }
    }
}
