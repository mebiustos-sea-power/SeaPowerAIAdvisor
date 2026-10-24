using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SeaPowerAIAdvisor;

// A stopped adviser keeps its first failure until an explicit restart; HUD/reporting and cleanup remain independent.
internal sealed class AdvisorRuntime
{
    private readonly string mode;
    private readonly Action<string> log;
    private readonly Action stop;
    private bool presentationStopped, notificationPending;
    internal string? Report { get; private set; }
    internal bool Failed => Report != null;

    internal AdvisorRuntime(string mode, Action<string> log, Action stop)
    { this.mode = mode; this.log = log; this.stop = stop; }

    internal void Guard(Action action)
    {
        if (Failed) return;
        try { action(); }
        catch (Exception error) { Fail(error); }
    }

    internal void Tick(Action action)
    {
        if (presentationStopped) return;
        try { action(); }
        catch (Exception error)
        {
            if (!Failed) Fail(error);
            else
            {
                presentationStopped = true;
                Secondary(error);
            }
        }
    }

    internal void Present(Action action)
    {
        try { action(); }
        catch (Exception error) { Secondary(error); }
    }

    // Consume before UI creation so a broken error view is not retried every frame.
    internal void PresentFailure(Action show)
    {
        if (!Failed) return;
        notificationPending = false;
        Present(show);
    }

    internal void NotifyFailure(Action show)
    {
        if (notificationPending) PresentFailure(show);
    }

    internal void Cleanup(Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            if (!Failed) Fail(error);
            else Secondary(error);
        }
        finally { presentationStopped = false; }
    }

    // Attempt every teardown stage; the existing runtime boundary reports all original failures.
    internal static void CleanupAll(params Action[] actions)
    {
        var errors = new List<Exception>();
        foreach (var action in actions)
            try { action(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Advisor cleanup failed.", errors);
    }

    internal bool Restart(Action initialize)
    {
        if (!Failed) return true;
        Report = null;
        presentationStopped = false;
        notificationPending = false;
        Guard(() => { stop(); initialize(); });
        return !Failed;
    }

    internal static void ThrowTestException() =>
        throw new InvalidOperationException("Intentional exception raised by the developer test button.");

    internal void Fail(Exception error)
    {
        if (Failed) return;
        var details = Redact(error.ToString());
        var version = typeof(AdvisorRuntime).Assembly.GetName().Version!.ToString(3);
        Report = "Sea Power AI Advisor v" + version + "\nMode: " + mode +
            "\nTime (UTC): " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n\n" + details;
        notificationPending = true;
        log((mode == "tactical_battle" ? "Battle" : "DCM") + " advisor stopped until restart: " + details);
        try { stop(); }
        catch (Exception cleanup) { Secondary(cleanup); }
    }

    private void Secondary(Exception error) => log("Advisor error reporting/cleanup failed: " + Redact(error.ToString()));

    private static string Redact(string value)
    {
        value = Regex.Replace(value, @"AIza[0-9A-Za-z_-]{30,}", "[REDACTED]");
        return Regex.Replace(value, @"(?i)(""?(?:api_key|fallback_api_key|x-goog-api-key)""?\s*[:=]\s*""?)[^\s""&,;}]+", "$1[REDACTED]");
    }
}
