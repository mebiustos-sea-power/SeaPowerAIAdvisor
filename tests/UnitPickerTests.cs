using System;
using System.Linq;
using SeaPowerAIAdvisor;

internal static class UnitPickerTests
{
    internal static void Run(Action<string, Action> check)
    {
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
