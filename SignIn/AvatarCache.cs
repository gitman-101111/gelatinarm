using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Server;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.SignIn
{
    public interface IAvatarCache
    {
        /// <summary>
        ///     The cached picture for the profile's current image tag, as a URI an image control
        ///     opens; null when none is cached.
        /// </summary>
        Uri TryGetCached(UserProfile profile);

        /// <summary>
        ///     Fetches the profile's picture when the cache lacks its current tag. A profile with
        ///     no picture has its cached files removed.
        /// </summary>
        Task RefreshAsync(UserProfile profile, string accessToken, CancellationToken cancellationToken);

        void Remove(string userId);
    }

    /// <summary>
    ///     Profile pictures for the "Who's watching?" picker, kept on disk so a profile whose
    ///     server is out of reach (a remote server, a LAN address away from home) still shows
    ///     its picture. Files are named by user id and image tag: a new picture has a new tag,
    ///     so the old file is simply stale and goes when the new one arrives.
    /// </summary>
    public class AvatarCache : BaseService, IAvatarCache
    {
        private const string FolderName = "Avatars";
        private readonly IHttpClientFactory _httpClientFactory;

        public AvatarCache(ILogger<AvatarCache> logger, IHttpClientFactory httpClientFactory) : base(logger)
        {
            _httpClientFactory = httpClientFactory;
        }

        private static string FolderPath => Path.Combine(ApplicationData.Current.LocalFolder.Path, FolderName);

        private static string FileName(UserProfile profile)
        {
            // Tags are hex, but a file name takes no chances
            var tag = new string(profile.PrimaryImageTag.Where(char.IsLetterOrDigit).ToArray());
            return $"{profile.UserId}-{tag}.img";
        }

        public Uri TryGetCached(UserProfile profile)
        {
            if (string.IsNullOrEmpty(profile?.UserId) || string.IsNullOrEmpty(profile.PrimaryImageTag))
            {
                return null;
            }

            var fileName = FileName(profile);
            if (!File.Exists(Path.Combine(FolderPath, fileName)))
            {
                return null;
            }

            // BitmapImage opens app data through this scheme; a plain path would need a StorageFile
            return new Uri($"ms-appdata:///local/{FolderName}/{fileName}");
        }

        public async Task RefreshAsync(UserProfile profile, string accessToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(profile?.UserId) || string.IsNullOrEmpty(profile.ServerUrl))
            {
                return;
            }

            if (string.IsNullOrEmpty(profile.PrimaryImageTag))
            {
                // The picture was removed on the server
                Remove(profile.UserId);
                return;
            }

            if (TryGetCached(profile) != null || !Guid.TryParse(profile.UserId, out var userGuid))
            {
                return;
            }

            var context = CreateErrorContext("RefreshAvatar", ErrorCategory.Media, ErrorSeverity.Warning);
            try
            {
                var url = ImageHelper.BuildUserImageUrl(profile.ServerUrl, userGuid, profile.PrimaryImageTag);
                if (url == null)
                {
                    return;
                }

                var client = _httpClientFactory.CreateClient(SystemConstants.JellyfinHttpClientName);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(accessToken))
                {
                    // For a server that insists on it; the picker's own load goes without
                    request.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{accessToken}\"");
                }

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogDebug("Avatar for {Username} not fetched: {StatusCode}", profile.Username, response.StatusCode);
                    return;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    return;
                }

                Directory.CreateDirectory(FolderPath);
                RemoveFiles(profile.UserId);
                File.WriteAllBytes(Path.Combine(FolderPath, FileName(profile)), bytes);
                Logger.LogDebug("Cached avatar for {Username} ({Bytes} bytes)", profile.Username, bytes.Length);
            }
            catch (Exception ex)
            {
                // The picker loads from the server instead
                await ErrorHandler.HandleErrorAsync(ex, context).ConfigureAwait(false);
            }
        }

        public void Remove(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return;
            }

            ErrorHandler.Run(CreateErrorContext("RemoveAvatar", ErrorCategory.Media, ErrorSeverity.Warning),
                () => RemoveFiles(userId));
        }

        private static void RemoveFiles(string userId)
        {
            if (!Directory.Exists(FolderPath))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(FolderPath, $"{userId}-*.img"))
            {
                File.Delete(file);
            }
        }
    }
}
