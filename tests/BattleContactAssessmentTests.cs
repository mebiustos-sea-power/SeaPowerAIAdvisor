using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using SeaPowerAIAdvisor;

internal static partial class BattleTests
{
    private static void RunContactAssessment(Action<string, Action> check)
    {
        check("Contact candidate evidence preserves visible matches, unconfirmed references and UI order without reading hidden data", () =>
        {
            var evidence = Evidence();
            var observed = (JArray)evidence["observed_features"]!;
            var candidates = (JArray)evidence["candidates"]!;
            Require(observed.Count == 2 && (string?)observed[0]!["reference_key"] == "small_rcs" &&
                (string?)observed[1]!["reference_key"] == "helicopter_category",
                "Undisplayed signature bits were exposed or a visible observed family was lost");
            Require((string?)candidates[0]!["display_name"] == "候補A" && (string?)candidates[1]!["display_name"] == "候補B" &&
                candidates[0]!["matching_features"]!.Count() == 2 &&
                (string?)candidates[0]!["unconfirmed_features"]![0]!["display_name"] == "表示：radar_model",
                "Candidate equipment was treated as observed, translated away or ranked by an invented score");
        });
        check("A contextual consultation binds the current contact, freezes its evidence and keeps candidate pages on demand", () =>
        {
            var f = new Fixture(); f.Track.Identified.Value = false;
            var context = AssessmentContext();
            var request = new AdvisorQuestion("Assess this contact", "Contact 7についての見解", true, context);
            context["track_number"] = 999;
            var source = new BattleAdvisorSource(() => f.Capture());
            var session = source.CreateSession("ja", request.Text, context: request.Context);
            var initial = session.InitialInput();
            Require((string?)initial["contact_assessment"]!["contact"]!["id"] == "observed_contact_1" &&
                (int?)initial["contact_assessment"]!["contact"]!["track_number"] == 7 &&
                (int?)initial["contact_assessment"]!["candidate_count"] == 2 &&
                initial["contact_assessment"]!["candidates"] == null,
                "The wrong contact was selected or large candidate details were forced into the initial input");
            var altered = request.Context!; altered["evidence"]!["candidates"]![0]!["display_name"] = "Changed later";
            var data = session.Execute(Request(session, new JObject { ["query"] = BattleContactAssessment.Query, ["limit"] = 1 },
                new JObject { ["query"] = "list_contacts" }));
            Require((string?)data["results"]![0]!["data"]!["items"]![0]!["display_name"] == "候補A" &&
                (bool?)data["results"]![0]!["data"]!["has_more"] == true && session.RoundsUsed == 1 &&
                data["results"]![1]!["data"]!["items"]!.Count() == 1,
                "Evidence mutated after capture or candidates could not batch with existing known-contact retrieval");
            var normal = source.CreateSession("en", "What now?");
            Require(normal.InitialInput()["contact_assessment"] == null &&
                !normal.InitialInput()["query_tool"]!["parameters"]!["properties"]!["requests"]!["items"]!["properties"]!["query"]!["enum"]!
                    .Values<string>().Contains(BattleContactAssessment.Query),
                "Normal consultations acquired an unrelated fixed candidate-tool burden");
        });
        check("Identified, excluded and missing contacts cannot start an assessment, while hostile unidentified contacts can", () =>
        {
            var defaults = AdvisorInformation.Default;
            Require(BattleContactAssessment.IsAvailable(defaults, false, false, "enemy", false) &&
                !BattleContactAssessment.IsAvailable(defaults, true, false, "enemy", false) &&
                !BattleContactAssessment.IsAvailable(defaults, false, true, "unknown", false) &&
                !BattleContactAssessment.IsAvailable(defaults, false, false, "neutral", false) &&
                !BattleContactAssessment.IsAvailable(defaults, false, false, "unknown", true),
                "Button availability confused allegiance with identification or bypassed busy/exclusion states");
            void Refused(Fixture f, AdvisorInformation policy, JObject? context = null)
            {
                try
                {
                    _ = new BattleQuerySession(f.Capture(), "en", "Assess", information: policy, context: context ?? AssessmentContext());
                    throw new Exception("A refused contact became visible through a contextual request");
                }
                catch (SituationUnavailableException e) { Require(e.Code == "battle_contact_unavailable", "The contact refusal reason was hidden"); }
            }
            Refused(new Fixture(), defaults);
            var unknown = new Fixture(); unknown.Track.Identified.Value = false;
            Refused(unknown, defaults.With(InformationKind.BattleContacts, false));
            var foreign = AssessmentContext(); foreign["track_number"] = 8; Refused(unknown, defaults, foreign);
            unknown.ContactPosture = "Neutral"; Refused(unknown, defaults);
            var allowed = new BattleQuerySession(unknown.Capture(), "en", "Assess",
                information: defaults.With(InformationKind.BattleNeutralContacts, true).With(InformationKind.BattleThreats, false),
                context: AssessmentContext());
            Require(allowed.InitialInput()["contact_assessment"]!["contact"]!["public_class_reference_available"] == null,
                "The focused contact bypassed public-reference exclusions");
        });
        check("Reassessment preserves target evidence and candidate access while keeping friendly/enemy assumptions separate from observations", () =>
        {
            var f = new Fixture(); f.Track.Identified.Value = false;
            var original = new AdvisorQuestion("Assess Contact 7", "Contact 7", true, AssessmentContext());
            var evidence = original.Context!["evidence"]!.DeepClone();
            var chat = new AdvisorConversation();
            var turn = chat.Begin(original); turn.Complete("A prior interpretation, not observation evidence");
            foreach (var assumption in new[] { ContactAssumption.Friendly, ContactAssumption.Enemy })
            {
                var code = BattleContactAssessment.AffiliationCode(assumption);
                var request = BattleContactAssessment.Reassess(turn, assumption, "Reassess " + code);
                var session = new BattleAdvisorSource(() => f.Capture()).CreateSession("en", request.Text, context: request.Context);
                var focus = session.InitialInput()["contact_assessment"]!;
                Require((int?)focus["contact"]!["track_number"] == 7 && (string?)focus["assumed_affiliation"] == code &&
                    (string?)focus["contact"]!["posture"] == "unknown" && (bool?)focus["contact"]!["identified"] == false &&
                    JToken.DeepEquals(request.Context!["evidence"], evidence) &&
                    request.Text.Contains("Contact 7", StringComparison.Ordinal) && request.Text.Contains(code, StringComparison.Ordinal),
                    "Reassessment lost its target/evidence, changed observed affiliation or omitted its hypothesis from later dialogue");
                var results = session.Execute(Request(session, new JObject { ["query"] = BattleContactAssessment.Query }));
                Require(results["results"]![0]!["data"]!["items"]!.Count() == 2 &&
                    (string?)results["results"]![0]!["data"]!["items"]![0]!["display_name"] == "候補A",
                    "Reassessment could not retrieve the original candidate evidence");
                Require(chat.Turns.Count == (assumption == ContactAssumption.Friendly ? 1 : 2) &&
                    original.Context!["assumed_affiliation"] == null, "Preparing reassessment mutated an earlier turn");
                turn = chat.Begin(request); turn.Complete("A hypothetical interpretation");
            }
            f.ContactPosture = "Neutral";
            try
            {
                _ = new BattleQuerySession(f.Capture(), "en", "Assess", context: turn.Request.Context);
                throw new Exception("An assumed enemy affiliation bypassed the actual neutral-contact exclusion");
            }
            catch (SituationUnavailableException e) { Require(e.Code == "battle_contact_unavailable", "Exclusion was concealed"); }
        });
        check("Only completed contact assessments expose reassessment actions and all actions disable while a consultation is pending", () =>
        {
            foreach (var language in new[] { "en", "ja" })
            {
                var text = AdvisorText.Load(language); var chat = new AdvisorConversation();
                XElement[] Actions() => XDocument.Parse(AdvisorTranscript.Render(chat, text, "Adviser", "empty"))
                    .Descendants().Where(e => e.Name.LocalName == "Button" && e.Attributes()
                        .Any(a => a.Name.LocalName == "Name" && a.Value.StartsWith("Reassess", StringComparison.Ordinal))).ToArray();
                var assessment = chat.Begin(new AdvisorQuestion("Assess Contact 7", "Contact 7", true, AssessmentContext()));
                Require(Actions().Length == 0, "A pending assessment exposed completed-answer actions");
                assessment.Complete("Assessment"); chat.TryAdd("Ordinary follow-up", "Ordinary answer", out _);
                Require(Actions().Length == 2 && Actions().All(a => (bool?)a.Attribute("IsEnabled") == true),
                    "A completed assessment lost its actions or an ordinary reply acquired them");
                var next = chat.Begin(BattleContactAssessment.Reassess(assessment, ContactAssumption.Enemy, "Reassess"));
                Require(Actions().Length == 2 && Actions().All(a => (bool?)a.Attribute("IsEnabled") == false),
                    "Reassessment buttons could start a second consultation while one is pending");
                next.Cancel();
                Require(Actions().Length == 2 && Actions().All(a => (bool?)a.Attribute("IsEnabled") == true),
                    "Cancellation left actions disabled or added actions to an unanswered turn");
            }
        });
        check("Candidate pages retain existing pagination, batching and argument limits", () =>
        {
            var f = new Fixture(); f.Track.Identified.Value = false;
            var context = AssessmentContext();
            var candidates = (JArray)context["evidence"]!["candidates"]!;
            for (var i = 2; i < 25; i++) candidates.Add(new JObject { ["display_name"] = "Candidate " + i });
            var session = new BattleQuerySession(f.Capture(), "en", "Assess", context: context);
            var response = session.Execute(Request(session, new JObject { ["query"] = BattleContactAssessment.Query, ["limit"] = 20 },
                new JObject { ["query"] = BattleContactAssessment.Query, ["offset"] = 20, ["limit"] = 20 },
                new JObject { ["query"] = "list_contacts" }));
            Require(response["results"]![0]!["data"]!["items"]!.Count() == 20 &&
                response["results"]![1]!["data"]!["items"]!.Count() == 5 &&
                (bool?)response["results"]![1]!["data"]!["has_more"] == false && session.RoundsUsed == 1,
                "Candidate pages truncated data or bypassed the batch/round contract");
            var invalid = session.Execute(Request(session, new JObject { ["query"] = BattleContactAssessment.Query, ["limit"] = 21 }));
            Require((string?)invalid["error"]?["code"] == "invalid_arguments", "A candidate page bypassed the existing page limit");
        });
    }

    private static JObject AssessmentContext() => new()
    { ["track_number"] = 7, ["captured_at_utc"] = "1985-06-01T12:00:00Z", ["evidence"] = Evidence() };
    private static JObject Evidence()
    {
        var observed = new SignatureBits(1, 3, 65, 130);
        var model = new SignaturePanel
        {
            Signatures = new object[] { new { Family = new SignatureBits(1, 2, 3, 65) } },
            Candidates = new object[] {
                new CandidateRow("候補A", "class_a", observed, new SignatureBits(1, 2, 65, 130)),
                new CandidateRow("候補B", "class_b", observed, new SignatureBits(65)) }
        };
        var metadata = new
        {
            SignatureIdMap = new Dictionary<int, string> { [1] = "small_rcs", [2] = "radar_model", [65] = "helicopter_category" },
            FamilyMap = new Dictionary<string, string> { ["small_rcs"] = "rcs", ["radar_model"] = "radar", ["helicopter_category"] = "category" }
        };
        return BattleContactEvidenceReader.Capture(model, metadata, key => "表示：" + key);
    }
    private sealed class SignaturePanel
    {
        public object[] Signatures { get; set; } = Array.Empty<object>();
        public object[] Candidates { get; set; } = Array.Empty<object>();
        public bool Truncated => false;
        public int NumTruncated => 0;
    }
    private sealed class SignatureBits
    {
        public SignatureBlocks Bits { get; }
        internal SignatureBits(params int[] bits)
        {
            var blocks = new ulong[3];
            foreach (var bit in bits) blocks[bit / 64] |= 1UL << (bit % 64);
            Bits = new SignatureBlocks(blocks);
        }
    }
    // Match the game collection's supported indexed access and its unsupported interface enumeration.
    private sealed class SignatureBlocks : IEnumerable<ulong>
    {
        private readonly ulong[] blocks;
        internal SignatureBlocks(ulong[] blocks) { this.blocks = blocks; }
        public int Length => blocks.Length;
        public ulong this[int index] => blocks[index];
        public IEnumerator<ulong> GetEnumerator() => throw new NotImplementedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new NotImplementedException();
    }
    private sealed class CandidateRow
    {
        public string ClassName { get; }
        public string ClassRef { get; }
        public (SignatureBits, SignatureBits) DetectedReference { get; }
        public object Object => throw new InvalidOperationException("Read a hidden target through a candidate row");
        internal CandidateRow(string name, string reference, SignatureBits observed, SignatureBits expected)
        { ClassName = name; ClassRef = reference; DetectedReference = (observed, expected); }
    }
}
