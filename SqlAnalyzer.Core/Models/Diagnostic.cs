namespace SqlAnalyzer.Core.Models
{
    public enum DiagnosticSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    /// <summary>
    /// A structured diagnostic message produced during the analysis pipeline.
    /// Replaces the previous <c>List&lt;string&gt;</c> approach for diagnostics.
    /// </summary>
    public sealed class Diagnostic
    {
        public DiagnosticSeverity Severity { get; }
        public string Message { get; }
        public string? Source { get; }

        private Diagnostic(DiagnosticSeverity severity, string message, string? source)
        {
            Severity = severity;
            Message  = message ?? string.Empty;
            Source   = source;
        }

        public static Diagnostic Error(string message, string? source = null)
            => new Diagnostic(DiagnosticSeverity.Error, message, source);

        public static Diagnostic Warning(string message, string? source = null)
            => new Diagnostic(DiagnosticSeverity.Warning, message, source);

        public static Diagnostic Info(string message, string? source = null)
            => new Diagnostic(DiagnosticSeverity.Info, message, source);

        public override string ToString()
            => Source != null
                ? $"[{Severity.ToString().ToUpperInvariant()}] [{Source}] {Message}"
                : $"[{Severity.ToString().ToUpperInvariant()}] {Message}";
    }
}
