namespace SeaPowerAIAdvisor;

// Ordinary notices use fixed codes; redacted provider details are read only in the dedicated history tab.
internal sealed class AdvisorNotice
{
    internal string TextKey { get; }
    internal string Code { get; }
    internal int? HttpStatus { get; }
    private readonly bool usingFallback, hasDetails;
    private AdvisorNotice(string key, string code, int? status = null, bool fallback = false, bool details = false)
    { TextKey = key; Code = code; HttpStatus = status; usingFallback = fallback; hasDetails = details; }

    internal static AdvisorNotice Connection(AiConnectionException error)
    {
        var key = error.Code switch
        {
            "settings_missing" or "settings_unreadable" => "settings_missing",
            "api_key_missing" => "api_key_missing",
            "fallback_key_missing" => "fallback_key_missing",
            "model_invalid" => "model_invalid",
            "fallback_model_invalid" => "fallback_model_invalid",
            "settings_invalid" => "settings_invalid",
            "prompt_invalid" or "request_timeout_invalid" or "provider_unavailable" or "server_url_invalid" or "ollama_model_invalid" or "context_length_invalid" => error.Code,
            "openrouter_model_invalid" or "nanogpt_model_invalid" or "tool_call_test_failed" or "insufficient_credits" or "subscription_unavailable" => error.Code,
            "api_configuration_name_invalid" or "api_main_missing" or "api_selection_invalid" or "api_selection_same" => error.Code,
            "fallback_policy_unavailable" => "fallback_policy_unavailable",
            "api_access_denied" => "api_access_denied",
            "api_bad_request" => "api_bad_request",
            "api_error" => "api_error",
            "model_unavailable" => "model_unavailable",
            "diagnostic_log_error" => "diagnostic_log_error",
            "rate_limited" => "rate_limited",
            "request_timeout" => "request_timeout",
            "network_error" => "network_error",
            "output_limit" => "output_limit",
            "response_blocked" or "generation_failed" => "response_blocked",
            "request_budget" or "request_limit" => "request_budget",
            "tool_call_limit" => "tool_call_limit",
            _ => "unknown"
        };
        return new AdvisorNotice("connection." + key, error.Code, error.HttpStatus, error.UsingFallback, error.HasDetails);
    }

    internal static AdvisorNotice Options(AdvisorOptionsException error) => new("settings.error." + error.Code, error.Code);
    internal static AdvisorNotice Situation(string code) => new("situation.error." + code, code);
    internal static AdvisorNotice Prompts(AdvisorPromptException error) => new("prompts.error." + error.Code, error.Code);
    internal static AdvisorNotice Unexpected() => new("connection.unknown", "unexpected_error");
    internal string Message(AdvisorText text, bool inSettings = false) => (usingFallback ? text["connection.fallback_failed"] + " " : "") + (Code == "tool_call_limit" ? text.Format(TextKey, AdvisorEngine.MaxToolCallsPerResponse) : text[TextKey]) + " [" + Code +
        (HttpStatus.HasValue ? " / HTTP " + HttpStatus : "") + "]" +
        (hasDetails ? "\n" + text[inSettings ? "errorlog.test_hint" : "errorlog.hint"] : "");
}
