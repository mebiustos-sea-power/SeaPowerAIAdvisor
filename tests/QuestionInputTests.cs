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
