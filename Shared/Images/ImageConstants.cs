namespace Gelatinarm.Shared.Images
{
    public static class ImageConstants
    {
        public const int ImageQuality = 80;

        public const int BackdropQuality = 75;

        // Jellyfin image type names. The server takes these as strings, both in
        // image URLs and as keys into BaseItemDto.ImageTags.
        public const string ImageTypePrimary = "Primary";

        public const string ImageTypeBackdrop = "Backdrop";
    }
}
