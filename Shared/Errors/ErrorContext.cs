namespace Gelatinarm.Shared.Errors
{
    public class ErrorContext
    {
        public ErrorContext(string source, string operation, ErrorCategory category = ErrorCategory.System,
            ErrorSeverity severity = ErrorSeverity.Error)
        {
            Source = source;
            Operation = operation;
            Category = category;
            Severity = severity;
        }

        public string Source { get; }

        public string Operation { get; }

        public ErrorCategory Category { get; }

        public ErrorSeverity Severity { get; }
    }

    public enum ErrorCategory
    {
        User,
        Network,
        System,
        Media,
        Authentication,
        Configuration
    }

    public enum ErrorSeverity
    {
        Warning,
        Error
    }
}
