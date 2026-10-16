using System;
using System.Collections.Generic;

namespace SeaPowerAIAdvisor;

internal sealed class ChatExchange
{
    internal string Question { get; }
    internal string Answer { get; }
    internal string DisplayQuestion { get; }
    internal ChatExchange(string question, string answer, string? displayQuestion = null)
    { Question = question; Answer = answer; DisplayQuestion = displayQuestion ?? question; }
}

internal sealed class AdvisorConversation
{
    internal const int MaxQuestionLength = 2000;
    internal const int MaxExchanges = 20;
    private readonly List<ChatExchange> exchanges = new();
    internal IReadOnlyList<ChatExchange> Exchanges => exchanges;

    internal static bool Validate(string? question, out string error)
    {
        error = string.IsNullOrWhiteSpace(question) ? "validation.question_required" :
            question!.Length > MaxQuestionLength ? "validation.question_too_long" : "";
        return error.Length == 0;
    }

    internal bool TryAdd(string? question, string answer, out string error)
    {
        if (!Validate(question, out error)) return false;
        return TryAdd(new AdvisorQuestion(question!), answer, out error);
    }

    internal bool TryAdd(AdvisorQuestion question, string answer, out string error)
    {
        if (!Validate(question.Text, out error)) return false;
        if (string.IsNullOrWhiteSpace(answer)) { error = "validation.answer_empty"; return false; }
        if (string.IsNullOrWhiteSpace(question.DisplayText)) throw new ArgumentException("The question caption is missing.");
        // Store plain text; neither user nor model output is interpolated into XAML.
        exchanges.Add(new ChatExchange(question.Text, answer, question.DisplayText));
        if (exchanges.Count > MaxExchanges) exchanges.RemoveAt(0);
        return true;
    }

    internal void Clear() => exchanges.Clear();
}
