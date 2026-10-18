using System;
using System.Collections.Generic;
using SeaPowerAIAdvisor;

internal static class RuntimeTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Unexpected failures stop adviser work once while HUD updates and error reporting continue in both modes", () =>
        {
            foreach (var mode in new[] { "tactical_battle", "dynamic_campaign" })
            {
                var messages = new List<string>();
                var stops = 0; var work = 0; var hud = 0;
                var runtime = new AdvisorRuntime(mode, messages.Add, () => stops++);
                runtime.Guard(() => work++);
                runtime.Tick(() => throw new InvalidOperationException("simulated render failure"));
                Require(runtime.Failed && stops == 1 && messages.Count == 1, "The first failure did not stop work and report its cause");
                var report = runtime.Report!;
                runtime.Guard(() => work++);
                runtime.Tick(() => hud++);
                string? copied = null;
                runtime.Present(() => copied = runtime.Report);
                Require(work == 1 && hud == 1 && copied == report && report.Contains(mode, StringComparison.Ordinal) &&
                    report.Contains("simulated render failure", StringComparison.Ordinal) && report.Contains("RuntimeTests", StringComparison.Ordinal),
                    "A failed adviser resumed normal work, blocked its HUD/report, or lost the exception stack");
                runtime.Fail(new Exception("later failure"));
                Require(stops == 1 && runtime.Report == report, "A later error replaced the original cause or stopped work repeatedly");
            }
        });
        check("Reporting and cleanup errors retain the first failure without per-frame retry loops or disabling explicit copy attempts", () =>
        {
            var messages = new List<string>(); var ticks = 0; var cleanups = 0; var copies = 0;
            var runtime = new AdvisorRuntime("tactical_battle", messages.Add, () => throw new Exception("teardown failed"));
            runtime.Guard(() => throw new Exception("original failure"));
            var report = runtime.Report;
            Require(messages.Count == 2 && !report!.Contains("teardown failed", StringComparison.Ordinal), "Cleanup replaced the root cause");
            runtime.Tick(() => { ticks++; throw new Exception("error view update failed"); });
            for (var i = 0; i < 10; i++) runtime.Tick(() => ticks++);
            Require(ticks == 1 && messages.Count == 3, "A broken reporting path was retried or logged every frame");
            runtime.Present(() => throw new Exception("clipboard failure"));
            runtime.Present(() => copies++);
            Require(copies == 1 && runtime.Report == report, "An explicit reporting attempt became permanently unavailable or rewrote the original report");
            runtime.Cleanup(() => { cleanups++; throw new Exception("scene cleanup failed"); });
            runtime.Tick(() => ticks++);
            runtime.Guard(() => throw new Exception("must remain stopped"));
            Require(cleanups == 1 && ticks == 2 && runtime.Report == report, "Scene cleanup was blocked, failed to reset presentation, or restarted normal adviser work");
        });
        check("Failure notification is deferred and shown once, manual reopen stays available, and failed display does not loop", () =>
        {
            foreach (var mode in new[] { "tactical_battle", "dynamic_campaign" })
            {
                var runtime = new AdvisorRuntime(mode, _ => { }, () => { });
                var shows = 0; var open = false;
                void Show() { shows++; open = true; }
                runtime.NotifyFailure(Show);
                Require(shows == 0, "A healthy adviser displayed an error");
                runtime.Guard(AdvisorRuntime.ThrowTestException);
                Require(shows == 0, "The click handler immediately constructed its error view");
                for (var i = 0; i < 10; i++) runtime.NotifyFailure(Show);
                Require(shows == 1 && open, "The first failure did not automatically open one error view");
                open = false;
                runtime.NotifyFailure(Show);
                Require(shows == 1 && !open, "A dismissed error reopened itself on the next frame");
                runtime.PresentFailure(Show);
                open = false;
                runtime.NotifyFailure(Show);
                Require(shows == 2 && !open, "Manual reopening was unavailable or queued another automatic popup");
                runtime.Restart(() => { });
                runtime.NotifyFailure(Show);
                Require(shows == 2, "Successful restart retained an old notification");
                runtime.Guard(AdvisorRuntime.ThrowTestException);
                runtime.PresentFailure(Show);
                open = false;
                runtime.NotifyFailure(Show);
                Require(shows == 3 && !open, "Opening and closing manually before the next frame reopened the error");
                runtime.Restart(() => { });
                runtime.Guard(AdvisorRuntime.ThrowTestException);
                var report = runtime.Report; var attempts = 0;
                runtime.NotifyFailure(() => { attempts++; throw new Exception("simulated error-view failure"); });
                for (var i = 0; i < 10; i++) runtime.NotifyFailure(() => attempts++);
                runtime.PresentFailure(Show);
                Require(attempts == 1 && shows == 4 && runtime.Report == report, "A broken automatic error view looped, lost its cause or blocked manual retry");
            }
        });
        check("The developer exception follows the real failure path and manual restart restores normal work without a game restart", () =>
        {
            foreach (var mode in new[] { "tactical_battle", "dynamic_campaign" })
            {
                var stops = 0; var starts = 0; var work = 0; var updates = 0;
                var runtime = new AdvisorRuntime(mode, _ => { }, () => stops++);
                runtime.Guard(AdvisorRuntime.ThrowTestException);
                Require(runtime.Failed && stops == 1 && runtime.Report!.Contains(typeof(InvalidOperationException).FullName!, StringComparison.Ordinal),
                    "The developer action did not reach the regular exception handler");
                runtime.Tick(() => throw new Exception("failed HUD update"));
                Require(starts == 0, "The adviser restarted without a user action");
                Require(runtime.Restart(() => starts++) && !runtime.Failed && runtime.Report == null && stops == 2 && starts == 1,
                    "Manual restart did not tear down old work and clear the failed state");
                runtime.Tick(() => updates++);
                runtime.Guard(() => work++);
                Require(updates == 1 && work == 1, "The reporting latch or normal-operation latch survived restart");
                Require(runtime.Restart(() => starts++) && starts == 1, "A stale second click restarted an already healthy adviser");
                runtime.Guard(AdvisorRuntime.ThrowTestException);
                Require(runtime.Failed && runtime.Restart(() => starts++) && starts == 2,
                    "A second test/restart cycle required a new game runtime");
            }
        });
        check("Failed manual restarts show the new failure, never claim success, and do not retry initialization automatically", () =>
        {
            var messages = new List<string>(); var attempts = 0; var updates = 0; var cleanupFails = false;
            var runtime = new AdvisorRuntime("tactical_battle", messages.Add, () =>
            {
                if (cleanupFails) throw new Exception("restart cleanup failed");
            });
            runtime.Guard(AdvisorRuntime.ThrowTestException);
            var original = runtime.Report;
            Require(!runtime.Restart(() => { attempts++; throw new Exception("replacement window failed"); }) && runtime.Failed &&
                runtime.Report != original && runtime.Report!.Contains("replacement window failed", StringComparison.Ordinal),
                "A failed replacement hid the error or claimed to restore the adviser");
            var notifications = 0;
            runtime.NotifyFailure(() => notifications++);
            runtime.NotifyFailure(() => notifications++);
            Require(notifications == 1, "A failed manual restart did not queue exactly one new error notification");
            for (var i = 0; i < 10; i++) runtime.Tick(() => updates++);
            runtime.Guard(() => attempts++);
            Require(attempts == 1 && updates == 10, "A failed restart was retried automatically or stopped error reporting");
            cleanupFails = true;
            Require(!runtime.Restart(() => attempts++) && attempts == 1 && runtime.Failed &&
                runtime.Report!.Contains("restart cleanup failed", StringComparison.Ordinal), "Failed cleanup allowed a replacement to start");
            cleanupFails = false;
            Require(runtime.Restart(() => attempts++) && !runtime.Failed && attempts == 2, "A later explicit restart could not recover after the cause was removed");
        });
        check("Copyable error details redact credential-shaped values and keep independent mode failures separate", () =>
        {
            var key = "AIza" + new string('X', 35);
            var messages = new List<string>();
            var battle = new AdvisorRuntime("tactical_battle", messages.Add, () => { });
            var campaign = new AdvisorRuntime("dynamic_campaign", messages.Add, () => { });
            battle.Guard(() => throw new Exception("key " + key + " api_key=private-test-value x-goog-api-key: header-test-value"));
            var details = battle.Report! + string.Join("\n", messages);
            Require(!details.Contains(key, StringComparison.Ordinal) && !details.Contains("private-test-value", StringComparison.Ordinal) &&
                !details.Contains("header-test-value", StringComparison.Ordinal) && details.Contains("[REDACTED]", StringComparison.Ordinal),
                "The error report or normal log exposed credentials");
            var work = 0;
            campaign.Guard(() => work++);
            campaign.Tick(() => work++);
            Require(!campaign.Failed && campaign.Report == null && work == 2, "A battle failure disabled the other mode");
        });
    }

    private static void Require(bool condition, string error)
    { if (!condition) throw new InvalidOperationException(error); }
}
