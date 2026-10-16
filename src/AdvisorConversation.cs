using System;
using System.Collections.Generic;
using System.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class ChatExchange
{
    internal string Question { get; }
    internal string Answer { get; }
    internal string DisplayQuestion { get; }
    internal ChatExchange(string question, string answer, string? displayQuestion = null)
    { Question = question; Answer = answer; DisplayQuestion = displayQuestion ?? question; }
}

internal enum AdvisorTurnState { Waiting, Answered, Failed, Cancelled }

internal sealed class AdvisorTurn
{
    internal AdvisorQuestion Request { get; }
    internal int ContextGeneration { get; private set; }
    internal AdvisorTurnState State { get; private set; } = AdvisorTurnState.Waiting;
    internal ChatExchange? Exchange { get; private set; }
    internal AdvisorNotice? Error { get; private set; }
    internal AdvisorTurn(AdvisorQuestion request, int contextGeneration) { Request = request; ContextGeneration = contextGeneration; }

    internal void Complete(string answer)
    {
        RequireWaiting();
        if (string.IsNullOrWhiteSpace(answer)) throw new ArgumentException("The answer is empty.");
        Exchange = new ChatExchange(Request.Text, answer, Request.DisplayText);
        State = AdvisorTurnState.Answered;
    }

    internal void Fail(AdvisorNotice error)
    { RequireWaiting(); Error = error; State = AdvisorTurnState.Failed; }
    internal void Cancel() { RequireWaiting(); State = AdvisorTurnState.Cancelled; }
    internal void Retry(int contextGeneration)
    {
        if (State != AdvisorTurnState.Failed && State != AdvisorTurnState.Cancelled)
            throw new InvalidOperationException("Only an unsuccessful turn can be retried.");
        Error = null;
        ContextGeneration = contextGeneration;
        State = AdvisorTurnState.Waiting;
    }
    private void RequireWaiting()
    {
        if (State != AdvisorTurnState.Waiting) throw new InvalidOperationException("The turn has already finished.");
    }
}

internal sealed class AdvisorConversation
{
    internal const int MaxQuestionLength = 2000;
    internal const int MaxExchanges = 20;
    private readonly List<AdvisorTurn> turns = new();
    private string? informationKey;
    private int contextGeneration;
    // Keep visible history, but never resend dialogue created under a different information selection.
    internal void UseInformationPolicy(string key)
    {
        if (informationKey != null && informationKey != key) contextGeneration++;
        informationKey = key;
    }
    internal IReadOnlyList<AdvisorTurn> Turns => turns;
    // Only completed dialogue from the current context is sent later. Notices and older settings stay in the UI.
    internal IReadOnlyList<ChatExchange> Exchanges => turns.Where(t => t.Exchange != null && t.ContextGeneration == contextGeneration).Select(t => t.Exchange!).ToArray();

    internal static bool Validate(string? question, out string error)
    {
        error = string.IsNullOrWhiteSpace(question) ? "validation.question_required" :
            question!.Length > MaxQuestionLength ? "validation.question_too_long" : "";
        return error.Length == 0;
    }

    internal AdvisorTurn Begin(AdvisorQuestion request)
    {
        if (!Validate(request.Text, out _) || string.IsNullOrWhiteSpace(request.DisplayText))
            throw new ArgumentException("The question is invalid.");
        if (turns.Any(t => t.State == AdvisorTurnState.Waiting))
            throw new InvalidOperationException("A consultation is already pending.");
        var turn = new AdvisorTurn(request, contextGeneration);
        turns.Add(turn);
        if (turns.Count > MaxExchanges) turns.RemoveAt(0);
        return turn;
    }

    internal bool CanRetry(AdvisorTurn turn) => turns.Count > 0 && ReferenceEquals(turns[turns.Count - 1], turn) &&
        (turn.State == AdvisorTurnState.Failed || turn.State == AdvisorTurnState.Cancelled);

    internal void Retry(AdvisorTurn turn)
    {
        if (!CanRetry(turn)) throw new InvalidOperationException("Only the latest unsuccessful turn can be retried.");
        turn.Retry(contextGeneration);
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
        // Preserve the original question and Markdown answer for history, copy and AI context.
        Begin(question).Complete(answer);
        return true;
    }

    internal void Clear() => turns.Clear();
}
