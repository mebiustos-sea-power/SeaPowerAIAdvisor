using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Detached consultation inputs and the host-controlled knowledge gateway; no API credentials or wire state.
internal sealed class AdvisorContext
{
    internal IAdvisorQuerySession Knowledge { get; }
    internal AdvisorProviderInput Input { get; }
    internal Action<AdvisorProgress>? Progress { get; }

    internal AdvisorContext(IAdvisorQuerySession knowledge, AdvisorProviderInput input, Action<AdvisorProgress>? progress = null)
    { Knowledge = knowledge; Input = input; Progress = progress; }

    internal static AdvisorContext Create(IAdvisorQuerySession session, IReadOnlyList<ChatExchange> history,
        AdvisorProfile? profile = null, string commonPrompt = "", Action<AdvisorProgress>? progress = null,
        int historyExchanges = AdvisorOptions.DefaultHistoryExchanges, string userName = "")
    {
        historyExchanges = AdvisorOptions.ValidateHistoryExchanges(historyExchanges);
        var initial = session.InitialInput();
        profile?.AddAnswerPreferences(initial);
        initial["common_prompt"] = AdvisorOptions.ValidateCommonPrompt(commonPrompt);
        initial["user_name"] = AdvisorOptions.ValidateUserName(userName);
        var tool = (JObject)initial["query_tool"]!;
        var instructions = initial["instructions"]!.DeepClone();
        initial.Remove("instructions"); initial.Remove("query_tool");
        initial["prior_dialogue"] = new JArray(history.Skip(Math.Max(0, history.Count - historyExchanges))
            .Select(h => new JObject { ["question"] = h.Question, ["answer"] = h.Answer, ["advisor_name"] = h.AdvisorName }));
        initial["omitted_prior_exchange_count"] = Math.Max(0, history.Count - historyExchanges);
        return new AdvisorContext(session, new AdvisorProviderInput(initial, session.Prompts.SystemInstruction(instructions), tool), progress);
    }
}
