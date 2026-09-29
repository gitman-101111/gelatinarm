using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Details
{
    public class MovieVersion
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public MediaSourceInfo SourceInfo { get; set; }
    }
}
