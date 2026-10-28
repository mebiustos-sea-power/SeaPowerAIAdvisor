using System;
using SeaPowerAIAdvisor;

internal static class SettingsNavigationTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Clean settings tabs switch freely and do not leave an unused editor hidden", () =>
        {
            var selected = 0; var entered = 0; var left = 0;
            var navigation = new SettingsTabNavigation(index => selected = index, () => entered++);
            navigation.Request(1);
            Require(selected == 0 && entered == 0 && left == 0, "Selection was changed inside its notification");
            navigation.Pump(false, () => { left++; return true; }, _ => throw new InvalidOperationException("Clean tab asked for confirmation"));
            Require(selected == 1 && navigation.Current == 1 && entered == 1 && left == 1, "Clean navigation retained the old tab or its editor");
            navigation.Request(1);
            navigation.Pump(true, () => throw new InvalidOperationException("Same-tab click discarded edits"),
                _ => throw new InvalidOperationException("Same-tab click asked for confirmation"));
            Require(selected == 1 && entered == 1, "Clicking the active tab changed its state");
        });
        check("Continuing an edit keeps its tab and input, while accepting discard restores saved values before navigation", () =>
        {
            var selected = 0; var message = "unsaved"; var saved = "original"; var entered = 0; Action? accept = null;
            var navigation = new SettingsTabNavigation(index => selected = index, () => entered++);
            bool Leave() { message = saved; return true; }
            selected = 1; navigation.Request(selected);
            navigation.Pump(message != saved, Leave, action => accept = action);
            Require(selected == 0 && navigation.Current == 0 && message == "unsaved" && entered == 0 && accept != null,
                "An unconfirmed switch changed the tab or discarded input");
            // Closing the confirmation without accepting has no effect on the form.
            accept = null;
            Require(selected == 0 && message == "unsaved" && saved == "original", "Continuing editing saved or discarded its input");
            navigation.Request(1); navigation.Pump(true, Leave, action => accept = action);
            accept!();
            Require(selected == 1 && navigation.Current == 1 && message == "original" && saved == "original" && entered == 1,
                "Discard did not restore the form before switching, or implicitly saved edits");
            navigation.Request(0); navigation.Pump(false, () => true, _ => throw new InvalidOperationException("Discarded changes prompted again"));
            Require(selected == 0 && entered == 2, "Returning to a clean tab was blocked");
        });
        check("Restoring a selected tab does not recursively confirm, and programmatic setup does not navigate through dirty forms", () =>
        {
            var selected = 0; var confirmations = 0; var leaves = 0; var entered = 0;
            SettingsTabNavigation? navigation = null;
            navigation = new SettingsTabNavigation(index =>
            {
                selected = index; navigation!.Request(index);
            }, () => entered++);
            navigation.Reset(2);
            Require(selected == 2 && navigation.Current == 2 && entered == 1 && !navigation.HasPending,
                "Programmatic setup queued a navigation request");
            navigation.Request(0); navigation.Pump(true, () => { leaves++; return true; }, _ => confirmations++);
            navigation.Pump(true, () => { leaves++; return true; }, _ => confirmations++);
            Require(selected == 2 && navigation.Current == 2 && entered == 1 && confirmations == 1 && leaves == 0 && !navigation.HasPending,
                "Restoring selection caused recursive navigation or premature discard");
        });
        check("A failed discard keeps the original tab and propagates unexpected errors", () =>
        {
            var selected = 0; var entered = 0;
            var navigation = new SettingsTabNavigation(index => selected = index, () => entered++);
            navigation.Request(1); navigation.Pump(true, () => false, accept => accept());
            Require(selected == 0 && navigation.Current == 0 && entered == 0, "A failed reload left the original tab");
            try
            {
                navigation.Request(1); navigation.Pump(false, () => throw new InvalidOperationException("reload failed"), _ => { });
                throw new Exception("Unexpected reload failure was hidden");
            }
            catch (InvalidOperationException error) { Require(error.Message == "reload failed", "The reload error was replaced"); }
            Require(selected == 0 && navigation.Current == 0 && entered == 0, "An exception committed the pending navigation");
        });
        check("Tab headers and content agree after content updates following the selection event, for both continue and discard", () =>
        {
            var selected = 0; var presented = 0; var entered = 0; var message = "unsaved"; var confirmations = 0; Action? accept = null;
            SettingsTabNavigation? navigation = null;
            void Select(int index)
            {
                selected = index;
                navigation!.Request(index);
                presented = index; // The framework finishes displaying content after notifying selection listeners.
            }
            navigation = new SettingsTabNavigation(Select, () => entered++);
            navigation.Reset(); entered = 0;
            void Pump() => navigation.Pump(message == "unsaved", () => { message = "saved"; return true; }, action => { accept = action; confirmations++; });
            Select(1); Pump();
            Require(selected == 0 && presented == 0 && navigation.Current == 0 && message == "unsaved" && accept != null && entered == 0,
                "The original tab header was restored while another tab's content remained visible");
            accept = null; // Continue editing.
            Pump();
            Require(selected == 0 && presented == 0 && message == "unsaved" && confirmations == 1 && !navigation.HasPending,
                "Continuing editing changed displayed content, input or repeated the confirmation");
            Select(1); Pump(); accept!();
            Require(selected == 1 && presented == 1 && navigation.Current == 1 && message == "saved" && entered == 1,
                "Discard left the tab header, content and accepted navigation out of sync");
            Select(0); Select(1); Pump();
            Require(selected == 1 && presented == 1 && confirmations == 2 && !navigation.HasPending,
                "A superseded tab click was applied after the user returned to the current tab");
        });
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
