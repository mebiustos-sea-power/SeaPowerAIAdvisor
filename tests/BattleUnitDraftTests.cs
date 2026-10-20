using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunUnitDraft(Action<string, Action> check)
    {
        check("Badge targets deduplicate by identity, remove independently and freeze into submitted questions", () =>
        {
            var draft = new AdvisorUnitDraft();
            const string text = "この2隻を比較して。";
            Require(draft.Add(1001, "同じ表示名") && !draft.Add(1001, "再追加") && draft.Add(2002, "同じ表示名"),
                "Targets were deduplicated by name or the same target acquired duplicate badges");
            var request = draft.Prepare(text);
            Require(request.Text.Contains("(unit_id: 1001)") && request.Text.Contains("(unit_id: 2002)") &&
                request.DisplayText.Contains(text) && !request.DisplayText.Contains("unit_id:") && !request.HasContext &&
                request.DisplayBody == text && request.Units.Select(unit => unit.Id).SequenceEqual(new[] { 1001, 2002 }),
                "Badge identities were missing from the AI question, visible as technical tokens, or mistaken for contact assessments");
            draft.Remove(1001);
            Require(draft.Units.Count == 1 && draft.Units[0].Id == 2002 && !draft.Prepare(text).Text.Contains("(unit_id: 1001)"),
                "Removing one badge removed another target or left the removed identity in the question");
            draft.Clear();
            Require(draft.Units.Count == 0 && draft.Prepare(text).Text == text && request.Text.Contains("(unit_id: 1001)") &&
                request.Units.Count == 2 && request.Units[0].Name == "同じ表示名",
                "Clearing badges changed a submitted question or left a target in a new question");
            var chat = new AdvisorConversation(); chat.Begin(request).Complete("比較結果");
            Require(chat.Exchanges.Single().Question == request.Text && chat.Exchanges.Single().DisplayQuestion == request.DisplayText,
                "Conversation history lost the frozen targets or showed the transport-only identities");
        });
        check("Badge questions clear only their matching draft and do not alter quick-consultation draft preservation", () =>
        {
            var draft = new AdvisorUnitDraft(); draft.Add(1001, "対象");
            var request = draft.Prepare("質問");
            Require(request.ConsumesDraft("質問") && !request.ConsumesDraft("変更済み"),
                "Hidden target metadata prevented clearing the sent draft or cleared an independently edited message");
            var shortcut = AdvisorShortcut.NextSteps.Prepare("次の行動");
            Require(!shortcut.ConsumesDraft("質問") && draft.Units.Count == 1,
                "A shortcut consumed the unsent draft or its targets");
            var onlyTargets = draft.Prepare("");
            Require(AdvisorConversation.Validate(onlyTargets.Text, out _) && onlyTargets.DisplayText == "対象" &&
                onlyTargets.DisplayBody == "" && onlyTargets.Units.Count == 1 && onlyTargets.ConsumesDraft(""),
                "A targets-only draft lost its identity or visible name");
            Require(!AdvisorConversation.Validate(draft.Prepare(new string('a', AdvisorConversation.MaxQuestionLength)).Text, out var error) &&
                error == "validation.question_too_long", "Target metadata bypassed the consultation input-size limit");
            draft.Clear();
            Require(!AdvisorConversation.Validate(draft.Prepare("").Text, out _), "An entirely empty draft became sendable");
        });
        check("Badge-generated requests retain the current permitted unit bindings without sending removed targets", () =>
        {
            var f = new Fixture(); f.Track.Id = 7018;
            var draft = new AdvisorUnitDraft(); draft.Add(1001, "自軍"); draft.Add(7018, "Contact 7018");
            var snapshot = f.Capture();
            var request = draft.Prepare("どう対処する？");
            var session = new BattleQuerySession(snapshot, "ja", request.Text);
            var mentions = (JArray)session.InitialInput()["referenced_units"]!;
            Require(mentions.Select(m => (string)m["id"]!).SequenceEqual(new[] { "own_unit_1", "observed_contact_1" }),
                "Badges changed their target identities between the draft and consultation snapshot");
            draft.Remove(7018);
            var filtered = new BattleQuerySession(snapshot, "ja", draft.Prepare("どう対処する？").Text,
                information: AdvisorInformation.Default.With(InformationKind.BattleUnits, false));
            var remaining = (JArray)filtered.InitialInput()["referenced_units"]!;
            Require(remaining.Count == 1 && (int?)remaining[0]!["unit_id"] == 1001 &&
                (string?)remaining[0]!["status"] == "unavailable" && remaining[0]!["id"] == null,
                "Removed targets remained in the payload or a badge bypassed the information policy");
        });
    }
}
