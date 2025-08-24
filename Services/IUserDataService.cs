using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Service for managing user data operations like favorites and watched status
    /// </summary>
    public interface IUserDataService
    {
        /// <summary>
        ///     Toggle favorite status for an item
        /// </summary>
        /// <param name="itemId">The item ID</param>
        /// <param name="isFavorite">True to mark as favorite, false to unmark</param>
        /// <param name="userId">Optional user ID, uses current user if not provided</param>
        /// <returns>The updated user data</returns>
        Task<UserItemDataDto> ToggleFavoriteAsync(Guid itemId, bool isFavorite, Guid? userId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        ///     Toggle watched status for an item
        /// </summary>
        /// <param name="itemId">The item ID</param>
        /// <param name="isWatched">True to mark as watched, false to unmark</param>
        /// <param name="userId">Optional user ID, uses current user if not provided</param>
        /// <returns>The updated user data</returns>
        Task<UserItemDataDto> ToggleWatchedAsync(Guid itemId, bool isWatched, Guid? userId = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        ///     Get user data for an item
        /// </summary>
        /// <param name="itemId">The item ID</param>
        /// <param name="userId">Optional user ID, uses current user if not provided</param>
        /// <returns>The user data for the item</returns>
        Task<UserItemDataDto> GetUserDataAsync(Guid itemId, Guid? userId = null,
            CancellationToken cancellationToken = default);

    }
}
