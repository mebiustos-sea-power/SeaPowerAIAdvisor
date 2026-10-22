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
    internal IAdvisorOperationSession? Operations { get; }
    private AdvisorReadSession? reads;
    internal AdvisorReadSession Reads => reads ??= new AdvisorReadSession(Knowledge, Operations, Input);
    private readonly Action<AdvisorProviderInput>? preparedInput;
    internal AdvisorProviderInput PrepareStandardInput()
    { preparedInput?.Invoke(Reads.Input); return Reads.Input; }
    internal Action<AdvisorProviderInput>? PreparedInput => preparedInput;

    internal AdvisorContext(IAdvisorQuerySession knowledge, AdvisorProviderInput input, Action<AdvisorProgress>? progress = null, IAdvisorOperationSession? operations = null,
        Action<AdvisorProviderInput>? preparedInput = null)
    {
        Knowledge = knowledge; Input = input; Progress = progress; Operations = operations;
        this.preparedInput = preparedInput;
    }

    internal static AdvisorContext Create(IAdvisorQuerySession session, IReadOnlyList<ChatExchange> history,
        AdvisorProfile? profile = null, string commonPrompt = "", Action<AdvisorProgress>? progress = null,
        int historyExchanges = AdvisorOptions.DefaultHistoryExchanges, string userName = "", IAdvisorOperationSession? operations = null, Action<AdvisorProviderInput>? preparedInput = null)
    {
        historyExchanges = AdvisorOptions.ValidateHistoryExchanges(historyExchanges);
        var initial = session.InitialInput();
        profile?.AddAnswerPreferences(initial);
        initial["common_prompt"] = AdvisorOptions.ValidateCommonPrompt(commonPrompt);
        initial["user_name"] = AdvisorOptions.ValidateUserName(userName);
        if (operations?.History is JArray priorOperations && priorOperations.Count > 0)
            initial["prior_operations"] = priorOperations;
        var tool = (JObject)initial["query_tool"]!;
        var instructions = initial["instructions"]!.DeepClone();
        initial.Remove("instructions"); initial.Remove("query_tool");
        initial["prior_dialogue"] = new JArray(history.Skip(Math.Max(0, history.Count - historyExchanges))
            .Select(h => new JObject { ["question"] = h.Question, ["answer"] = h.Answer, ["advisor_name"] = h.AdvisorName }));
        initial["omitted_prior_exchange_count"] = Math.Max(0, history.Count - historyExchanges);
        return new AdvisorContext(session, new AdvisorProviderInput(initial, session.Prompts.SystemInstruction(instructions), tool, operations?.Tool), progress, operations, preparedInput);
    }
}
