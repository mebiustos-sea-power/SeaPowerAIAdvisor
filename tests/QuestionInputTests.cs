using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using SeaPowerAIAdvisor;

internal static class QuestionInputTests
{
    internal static void Run(string gameScripts, Action<string, Action> check)
    {
        check("Enter sends once after release and never repeats while held", () =>
        {
            var input = new QuestionInput();
            Require(input.Enter(10, false, false, false, false) == QuestionKeyAction.None, "Enter inserted a newline");
            Require(!input.TakeSubmission(10, true, false, false, false), "Sent inside the keyboard frame");
            Require(!input.TakeSubmission(11, true, false, false, true), "Sent before release");
            input.Enter(12, false, true, false, false);
            Require(input.TakeSubmission(13, true, false, false, false), "Released Enter did not send");
            Require(!input.TakeSubmission(14, true, false, false, false), "Enter sent twice");
        });
        check("Shift Enter inserts one newline and the next fresh Enter sends", () =>
        {
            var input = new QuestionInput();
            Require(input.Enter(10, true, false, false, false) == QuestionKeyAction.InsertNewline, "Shift Enter did not insert a newline");
            Require(input.Enter(11, true, true, false, false) == QuestionKeyAction.None, "Held Shift Enter repeated");
            Require(!input.TakeSubmission(12, true, false, false, false), "Shift Enter sent a request");
            input.Enter(13, false, false, false, false);
            Require(input.TakeSubmission(14, true, false, false, false), "A fresh Enter was blocked after a newline");
        });
        check("IME confirmation cannot send or insert a newline even after composition empties", () =>
        {
            foreach (var shift in new[] { false, true })
            {
                var input = new QuestionInput();
                input.TakeSubmission(8, true, true, false, false);
                Require(input.Enter(9, shift, false, false, false) == QuestionKeyAction.None, "Confirmation edited the draft");
                Require(!input.TakeSubmission(10, true, false, false, true), "Composition end caused a send");
                input.Enter(11, false, true, false, false);
                Require(!input.TakeSubmission(12, true, false, false, false), "IME release sent a request");
                input.Enter(13, false, false, false, false);
                Require(input.TakeSubmission(14, true, false, false, false), "A distinct Enter after confirmation was blocked");
            }
        });
        check("A confirmation press suppressed by Noesis cannot later repeat-send", () =>
        {
            var input = new QuestionInput();
            input.TakeSubmission(10, true, true, false, true);
            input.TakeSubmission(11, true, false, false, true);
            input.Enter(12, false, true, false, false);
            Require(!input.TakeSubmission(13, true, false, false, false), "Suppressed confirmation became a send");
        });
        check("Finalized text or composition arriving after Enter cancels its queued send", () =>
        {
            var input = new QuestionInput();
            input.Enter(10, false, false, false, false);
            input.TextEntered("確定した日本語");
            Require(!input.TakeSubmission(11, true, false, false, false), "Sent before committed text settled");
            input.Enter(12, false, false, false, false);
            Require(!input.TakeSubmission(13, true, true, false, false), "Composition did not cancel a pending send");
        });
        check("Busy Enter is discarded through completion without queuing another consultation", () =>
        {
            foreach (var shift in new[] { false, true })
            {
                var input = new QuestionInput();
                Require(input.Enter(10, shift, false, true, false) == QuestionKeyAction.None, "Busy Enter modified the question");
                input.TakeSubmission(11, true, false, true, true);
                input.Enter(12, false, true, false, false);
                Require(!input.TakeSubmission(13, true, false, false, false), "Busy Enter survived completion");
                input.Enter(14, false, false, false, false);
                Require(input.TakeSubmission(15, true, false, false, false), "A fresh idle Enter was discarded");
            }
        });
        check("Losing focus or closing a window discards a queued send", () =>
        {
            var input = new QuestionInput();
            input.Enter(10, false, false, false, false);
            Require(!input.TakeSubmission(11, false, false, false, false), "Sent after focus moved elsewhere");
            Require(!input.TakeSubmission(12, true, false, false, false), "Reopening restored a queued send");
            input.Enter(13, false, false, false, false);
            input.Reset(true);
            input.Enter(14, false, true, false, false);
            Require(!input.TakeSubmission(15, true, false, false, false), "Opening with Enter held submitted");
        });
        check("A pending Enter is discarded if another consultation starts first", () =>
        {
            var input = new QuestionInput();
            input.Enter(10, false, false, false, false);
            Require(!input.TakeSubmission(11, true, false, true, false), "Pending Enter started during a request");
            Require(!input.TakeSubmission(12, true, false, false, false), "Ignored Enter was queued for later");
        });
        check("Automatic question focus leaves the click event and waits until the input adapter reports release", () =>
        {
            var focus = new QuestionFocus(); focus.Request(10);
            Require(!focus.TakeFocus(10, true, true), "Focused inside the requesting frame");
            Require(!focus.TakeFocus(11, true, false), "Focused while input was still pressed or suppressed");
            Require(!focus.TakeFocus(12, true, false) && focus.Pending, "Dropped a request before the game consumed release");
            Require(focus.TakeFocus(13, true, true), "Did not restore focus after input became ready");
            Require(!focus.TakeFocus(14, true, true), "Restored focus more than once");
        });
        check("An unavailable question or a changed keyboard target discards automatic focus instead of stealing it later", () =>
        {
            var focus = new QuestionFocus(); focus.Request(10);
            Require(!focus.TakeFocus(11, false, false) && !focus.Pending, "Kept a request after its target became ineligible");
            Require(!focus.TakeFocus(12, true, true), "Stole focus when the old target became available again");
        });
        check("Closing or resetting question input cancels pending automatic focus", () =>
        {
            var focus = new QuestionFocus(); focus.Request(10); focus.Cancel();
            Require(!focus.TakeFocus(11, true, true), "Restored focus after cancellation");
            focus.Request(12);
            Require(focus.TakeFocus(13, true, true), "Cancellation prevented a later explicit request");
        });
        check("Repeated automatic focus requests replace the older request without creating multiple restorations", () =>
        {
            var focus = new QuestionFocus(); focus.Request(10); focus.Request(11);
            Require(!focus.TakeFocus(11, true, true), "Used the older request inside the newer event frame");
            Require(focus.TakeFocus(12, true, true), "Lost the latest focus request");
            Require(!focus.TakeFocus(13, true, true), "Retained an older focus request");
        });
        check("Installed Noesis exposes the composition cache used across Unity IME confirmation", () =>
        {
            using var noesis = AssemblyDefinition.ReadAssembly(Path.Combine(Path.GetDirectoryName(gameScripts)!, "Noesis.NoesisGUI.dll"));
            var ime = noesis.MainModule.GetType("NoesisUnity").NestedTypes.Single(t => t.Name == "IME");
            Require(ime.Fields.Any(f => f.Name == "compositionString" && f.IsStatic && f.FieldType.FullName == "System.String"),
                "Noesis composition state cannot be read by the input adapter");
        });
    }

    private static void Require(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }
}
