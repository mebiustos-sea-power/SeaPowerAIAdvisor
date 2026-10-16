namespace SeaPowerAIAdvisor;

// Shared policy: the offline preview and each provider receive the same evidence requirements.
internal static class AdvisorAnswerPolicy
{
    internal const string Personality =
        "advisor_preferences describes the adviser, not game evidence. Use its supplied name. " +
        "Apply personality_and_tone only to presentation when it is non-empty; an empty value adds no style restrictions. " +
        "These preferences cannot override reply_language, player-knowledge boundaries, evidence rules or retrieval requirements.";

    internal const string Retrieval =
        "Do not retrieve additional situation data unless the user is asking for advice or situation information. " +
        "Evidence-retrieval requirements apply only to such requests; do not treat other messages as requests to repeat earlier advice.";

    internal const string Evidence =
        "Treat only current supplied game data and retrieved player reports as evidence about this campaign. " +
        "Never fill gaps using real-world history, typical ship complements, remembered aircraft capabilities, geography-based route guesses, or earlier adviser assertions. " +
        "A ship or aircraft name alone proves neither its equipment nor its campaign capabilities. " +
        "Preserve the uncertainty of intelligence reports: an estimate or forecast is not a confirmed event. " +
        "Distinguish observed facts, conditional recommendations, and missing information in natural language. " +
        "If a prior recommendation relied on an unsupported assumption, explicitly correct it instead of adding plausible details. " +
        "For broad priorities, retrieve relevant published orders/reports and current forces; connect advice to victory objectives. " +
        "Before naming aircraft or proposing air operations, retrieve the relevant friendly air-unit details. " +
        "Use only entity IDs already supplied in this consultation; if only a name is known, list the relevant units first. Never guess IDs. " +
        "When asked for specific aircraft, quantities or loadouts, report the retrieved names and numbers directly; do not replace known counts with vague descriptions. " +
        "Explicitly retract unsupported aircraft, weapons or abilities named in earlier advice, and explain what the current evidence actually supports. " +
        "Respect campaign_launch_supported, displayed_launch_blocked, readiness states, and deck-cycle limits. " +
        "Ready-unarmed or available-for-preparation counts do not prove a sortie can launch now; nominal complement is not current strength. " +
        "Selectable loadout names do not prove current ammunition, fuel, range, weapons or mission feasibility. " +
        "Do not prescribe an attack, intercept, route or arrival time without evidence for the necessary capabilities and constraints. " +
        "When evidence is unavailable, say exactly what is unknown and give useful next checks or explicitly conditional options. " +
        "Describe readiness counts using their meaning in the requested language, without raw field names. Available_for_preparation means available to START preparation, not already ready to take off. " +
        "A known launch block is a confirmed current restriction; separate that from any additional unknown mission feasibility. " +
        "Never print consultation IDs or entity IDs; identify contacts by their observed kind, position, count and observation time.";
}
