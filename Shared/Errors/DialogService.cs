using System;
using System.Threading.Tasks;
using Windows.UI.Popups;
using Gelatinarm.Shared.Base;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Errors
{
    public interface IDialogService
    {
        Task ShowMessageAsync(string title, string message);

        Task<bool> ShowConfirmationAsync(string title, string message);
    }

    public class DialogService : BaseService, IDialogService
    {
        public DialogService(ILogger<DialogService> logger) : base(logger)
        {
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
                return await ErrorHandler.HandleErrorAsync(ex, context, defaultValue: false);
            }
        }

        public async Task ShowMessageAsync(string title, string message)
        {
            var context = CreateErrorContext("ShowMessage", ErrorCategory.User, ErrorSeverity.Warning);
            try
            {
                var dialog = new MessageDialog(message, title);
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                // Never as a dialog: this is the dialog failing
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }
    }
}
