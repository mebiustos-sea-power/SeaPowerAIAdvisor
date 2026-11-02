using System;

namespace SeaPowerAIAdvisor.Shared;

// Callers choose severity from the outcome; the host owns the logging destination.
internal sealed class DiagnosticLog
{
    internal Action<string> Info { get; }
    internal Action<string> Warning { get; }
    internal Action<string> Error { get; }

    internal DiagnosticLog(Action<string> info, Action<string> warning, Action<string> error)
    { Info = info; Warning = warning; Error = error; }
}
