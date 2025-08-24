namespace Gelatinarm.Models
{
    /// <summary>
    ///     Context information for error handling
    /// </summary>
    public class ErrorContext
    {
        /// <summary>
        ///     Create a new error context
        /// </summary>
        public ErrorContext(string source, string operation, ErrorCategory category = ErrorCategory.System,
            ErrorSeverity severity = ErrorSeverity.Error)
        {
            Source = source;
            Operation = operation;
            Category = category;
            Severity = severity;
        }

        /// <summary>
        ///     The source component where the error occurred (e.g., "AuthenticationService", "MediaPlayerViewModel")
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        ///     The operation that was being performed (e.g., "Login", "PlayMedia", "LoadData")
        /// </summary>
        public string Operation { get; set; }

        /// <summary>
        ///     The category of error
        /// </summary>
        public ErrorCategory Category { get; set; }

        /// <summary>
        ///     The severity level
        /// </summary>
        public ErrorSeverity Severity { get; set; }

        /// <summary>
        ///     Additional context data
        /// </summary>
        public object Data { get; set; }

        /// <summary>
        ///     Whether the app is shutting down
        /// </summary>
        public bool IsShuttingDown { get; set; }
    }

    /// <summary>
    ///     Categories of errors for different handling strategies
    /// </summary>
    public enum ErrorCategory
    {
        /// <summary>
        ///     User input or action errors
        /// </summary>
        User,

        /// <summary>
        ///     Network connectivity or API errors
        /// </summary>
        Network,

        /// <summary>
        ///     System or infrastructure errors
        /// </summary>
        System,

        /// <summary>
        ///     Media playback specific errors
        /// </summary>
        Media,

        /// <summary>
        ///     Authentication and authorization errors
        /// </summary>
        Authentication,

        /// <summary>
        ///     Data validation errors
        /// </summary>
        Validation,

        /// <summary>
        ///     Configuration errors
        /// </summary>
        Configuration
    }

    /// <summary>
    ///     Error severity levels
    /// </summary>
    public enum ErrorSeverity
    {
        /// <summary>
        ///     Informational, not really an error
        /// </summary>
        Info,

        /// <summary>
        ///     Warning that doesn't prevent operation
        /// </summary>
        Warning,

        /// <summary>
        ///     Error that affects functionality
        /// </summary>
        Error,

        /// <summary>
        ///     Critical error that may crash the app
        /// </summary>
        Critical
    }

    /// <summary>
    /// Exception thrown when media playback resume operation times out
    /// </summary>
    public class ResumeTimeoutException : System.InvalidOperationException
    {
        public System.TimeSpan Position { get; }
        public System.TimeSpan TargetPosition { get; }
        public System.TimeSpan Elapsed { get; }

        public ResumeTimeoutException(System.TimeSpan position, System.TimeSpan targetPosition, System.TimeSpan elapsed)
            : base($"Resume operation timed out after {elapsed.TotalSeconds:F1}s. Stuck at position {position:mm\\:ss}, target was {targetPosition:mm\\:ss}")
        {
            Position = position;
            TargetPosition = targetPosition;
            Elapsed = elapsed;
        }
    }

    /// <summary>
    /// Exception thrown when media playback is stuck and cannot resume
    /// </summary>
    public class ResumeStuckException : System.InvalidOperationException
    {
        public System.TimeSpan Position { get; }
        public System.TimeSpan TargetPosition { get; }
        public int AttemptCount { get; }

        public ResumeStuckException(System.TimeSpan position, System.TimeSpan targetPosition, int attemptCount)
            : base($"Unable to resume playback at the saved position. This media may have encoding issues that prevent proper seeking. You can try playing it from the beginning instead.")
        {
            Position = position;
            TargetPosition = targetPosition;
            AttemptCount = attemptCount;
        }
    }
}
