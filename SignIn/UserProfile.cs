namespace Gelatinarm.SignIn
{
    /// <summary>
    ///     A user profile cached on this device for instant switching.
    ///     One entry per user who has signed in on this device; the access token
    ///     itself is stored separately in the PasswordVault keyed by UserId.
    ///     Profiles may belong to different servers: each carries its own.
    /// </summary>
    public class UserProfile
    {
        public string ServerUrl { get; set; }

        // The server's own id and display name, from its public system info. Null on profiles
        // saved before multi-server; filled in on the next successful sign-in or switch.
        public string ServerId { get; set; }
        public string ServerName { get; set; }

        public string UserId { get; set; }
        public string Username { get; set; }
        public string PrimaryImageTag { get; set; }
    }
}
