using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal enum ContactAssumption { Friendly, Enemy }

// Binds a copied request to the current permitted contact, never to an old consultation ID or display name.
internal sealed class BattleContactAssessment
{
    internal const string Query = "list_identification_candidates";
    internal static bool IsAvailable(AdvisorInformation information, bool identified, bool busy, string posture, bool destroyed) =>
        !identified && !busy && information.Allows(InformationKind.BattleContacts) &&
        (posture != "neutral" || information.Allows(InformationKind.BattleNeutralContacts)) &&
        (!destroyed || information.Allows(InformationKind.BattleDestroyedContacts));
    internal static bool CanReassess(AdvisorTurn turn) => turn.State == AdvisorTurnState.Answered && turn.Request.HasContext;
    internal static string AffiliationCode(ContactAssumption assumption) => assumption switch
    {
        ContactAssumption.Friendly => "friendly", ContactAssumption.Enemy => "enemy",
        _ => throw new ArgumentOutOfRangeException(nameof(assumption))
    };
    internal static AdvisorQuestion Reassess(AdvisorTurn turn, ContactAssumption assumption, string caption, AdvisorPrompts? prompts = null)
    {
        if (!CanReassess(turn)) throw new InvalidOperationException("Only a completed contact assessment can be reassessed.");
        var context = turn.Request.Context!;
        var affiliation = AffiliationCode(assumption);
        context["assumed_affiliation"] = affiliation;
        var track = (int)(context["track_number"] ?? throw new InvalidOperationException("The assessment track number is missing."));
        return new AdvisorQuestion((prompts ?? AdvisorPrompts.Current).Format("requests.json", "contact_reassessment", track, affiliation),
            caption, true, context);
    }
    internal JObject Focus { get; }
    internal JArray Candidates { get; }
    internal BattleContactAssessment(JObject snapshot, JObject context, AdvisorInformation information)
    {
        if (context["track_number"]?.Type != JTokenType.Integer || !(context["evidence"] is JObject evidence) ||
            !(evidence["candidates"] is JArray candidates) || !(evidence["observed_features"] is JArray observed))
            throw new InvalidOperationException("The contact assessment context is invalid.");
        var assumption = context["assumed_affiliation"];
        if (assumption != null && (assumption.Type != JTokenType.String ||
            ((string?)assumption != "friendly" && (string?)assumption != "enemy")))
            throw new InvalidOperationException("The assumed contact affiliation is invalid.");
        var contact = ((JArray)snapshot["observed_contacts"]!).OfType<JObject>()
            .SingleOrDefault(c => JToken.DeepEquals(c["track_number"], context["track_number"]));
        if (contact == null || (bool?)contact["identified"] == true)
            throw new SituationUnavailableException("battle_contact_unavailable");
        Candidates = (JArray)candidates.DeepClone();
        var selected = (JObject)contact.DeepClone();
        if (!information.Allows(InformationKind.BattleThreats)) selected.Remove("public_class_reference_available");
        Focus = new JObject
        {
            ["contact"] = selected,
            ["observed_features"] = observed.DeepClone(),
            ["candidate_count"] = candidates.Count,
            ["list_query"] = Query,
            ["candidates_truncated"] = evidence["candidates_truncated"]!.DeepClone(),
            ["undisplayed_candidate_count"] = evidence["undisplayed_candidate_count"]!.DeepClone(),
            ["evidence_captured_at_utc"] = context["captured_at_utc"]!.DeepClone()
        };
        if (assumption != null) Focus["assumed_affiliation"] = assumption.DeepClone();
    }
}
