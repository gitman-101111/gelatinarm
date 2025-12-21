using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Helpers
{
    internal sealed class ResumeRetryCoordinator
    {
        private readonly ILogger _logger;

        public ResumeRetryCoordinator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<(bool Success, int Retries)> ExecuteAsync(
            Func<Task<bool>> tryApplyAsync,
            Func<bool> isStillPending,
            bool isHlsStream)
        {
            var resumeResult = await tryApplyAsync();
            if (resumeResult)
            {
                return (true, 0);
            }

            var retryCount = 0;
            var maxRetries = isHlsStream ? 15 : 8;
            var retryDelay = isHlsStream ? 5000 : 1000;
            var streamType = isHlsStream ? "HLS-RESUME" : "DirectPlay";

            while (!resumeResult && retryCount < maxRetries)
            {
                retryCount++;

                if (!isStillPending())
                {
                    _logger.LogInformation("Resume no longer pending, stopping retries");
                    break;
                }

                _logger.LogInformation($"[{streamType}] Retry {retryCount}/{maxRetries} in {retryDelay}ms");
                await Task.Delay(retryDelay);

                resumeResult = await tryApplyAsync();
                if (resumeResult)
                {
                    break;
                }
            }

            return (resumeResult, retryCount);
        }
    }
}
