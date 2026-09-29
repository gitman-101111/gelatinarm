namespace Gelatinarm.SignIn
{
    /// <summary>
    ///     A user profile cached on this device for instant switching.
    ///     One entry per user who has signed in on this device; the access token
    ///     itself is stored separately in the PasswordVault keyed by UserId.
    /// </summary>
    public class UserProfile
    {
        public string ServerUrl { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }
        public string PrimaryImageTag { get; set; }
    }
}
