using System;
using System.Linq;
using SeaPowerAIAdvisor;

internal static class UnitPickerTests
{
    internal static void Run(Action<string, Action> check)
    {
        check("Mention completion finds the token at the caret and supports Japanese input", () =>
        {
            foreach (var value in new[] { "@", "@cal", "Move @Hob", "対象@カリ" })
            {
                var token = AdvisorMentionQuery.At(value, value.Length, 0);
                Require(token.HasValue && token.Value.Query == value.Substring(token.Value.Start + 1), "The current mention was not found");
            }
            foreach (var value in new[] { "＠", "＠ダール", "ordinary message", "@Hobby move", "@Hobby\n", "@Hobby、" })
                Require(!AdvisorMentionQuery.At(value, value.Length, 0).HasValue, "Completion remained active outside the mention");
            Require(!AdvisorMentionQuery.At("@Hobby", 6, 1).HasValue && !AdvisorMentionQuery.At("@Hobby", 0, 0).HasValue,
                "A selection or a caret before the marker opened completion");
        });
        check("Completing a mention replaces just its token and adds the selected unit identity", () =>
        {
            const string message = "Move (@Hobby) toward point A; keep @Scott.";
            var caret = message.IndexOf("bb", StringComparison.Ordinal);
            var token = AdvisorMentionQuery.At(message, caret, 0)!.Value;
            var candidate = AdvisorUnitSearch.Find(new[] { (10, "Hobby 1"), (20, "Scott") }, token.Query, Array.Empty<int>()).Single();
            var draft = new AdvisorDraft(); draft.Add(candidate.Id, candidate.Name);
            var result = draft.Prepare(message.Remove(token.Start, token.Length));
            Require(result.DisplayBody == "Move () toward point A; keep @Scott." && result.Units.Single().Id == 10,
                "Completion removed unrelated text, left a partial token or chose the wrong unit");
            var last = AdvisorMentionQuery.At(message, message.Length - 1, 0)!.Value;
            Require(last.Query == "Scott" && message.Remove(last.Start, last.Length) == "Move (@Hobby) toward point A; keep .",
                "Completing a later mention changed the earlier one or removed its surrounding punctuation");
        });
        check("Unit search orders names, combines case-insensitive terms and excludes already inserted identities", () =>
        {
            var units = new[] { (3, "Scott DDG-995"), (2, "California CGN-36"), (1, "California CGN-36"), (4, "ダールグレン DDG-43") };
            var all = AdvisorUnitSearch.Find(units, "  ", Array.Empty<int>());
            Require(all.Where(u => u.Name.StartsWith("California", StringComparison.Ordinal)).Select(u => u.Id).SequenceEqual(new[] { 1, 2 }),
                "Same-name units were merged or their identities became unstable");
            Require(all.Select(u => u.Name).SequenceEqual(all.Select(u => u.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)), "Names were not ordered");
            Require(AdvisorUnitSearch.Find(units, " cALifornia\t36 ", new[] { 1 }).Single().Id == 2, "Search ignored case, terms or an inserted identity");
            Require(AdvisorUnitSearch.Find(units, "ダール", Array.Empty<int>()).Single().Id == 4 &&
                AdvisorUnitSearch.Find(units, "missing", Array.Empty<int>()).Count == 0, "Partial or unmatched search returned the wrong units");
            Require(AdvisorUnitSearch.Find(units, "", units.Select(u => u.Item1)).Count == 0 && units[0].Item1 == 3, "Exclusions failed or search changed the source order");
        });
        check("A searched selection adds only its exact badge and preserves the question text", () =>
        {
            var draft = new AdvisorDraft(); draft.Add(1, "Existing");
            var candidate = AdvisorUnitSearch.Find(new[] { (1, "Existing"), (2, "Same name"), (3, "Same name") }, "same", draft.Units.Select(u => u.Id))[1];
            draft.Add(candidate.Id, candidate.Name);
            var question = draft.Prepare("Keep this draft.");
            Require(question.Units.Select(u => u.Id).SequenceEqual(new[] { 1, 3 }) && question.DisplayBody == "Keep this draft." &&
                question.Text.Contains("unit_id: 3", StringComparison.Ordinal) && !question.Text.Contains("unit_id: 2", StringComparison.Ordinal),
                "Search inserted a same-name unit or replaced the message");
        });
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
