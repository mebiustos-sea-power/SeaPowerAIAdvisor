using System;
using System.Collections.Generic;

namespace SeaPowerAIAdvisor;

internal sealed class ChatExchange
{
    internal string Question { get; }
    internal string Answer { get; }

    internal ChatExchange(string question, string answer)
    {
        Question = question;
        Answer = answer;
    }
}

internal sealed class PrototypeConversation
{
    internal const int MaxQuestionLength = 2000;
    internal const int MaxExchanges = 20;
    private readonly List<ChatExchange> exchanges = new();
    internal IReadOnlyList<ChatExchange> Exchanges => exchanges;

    internal bool TrySend(string? question, out string error)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            error = "質問を入力してください。";
            return false;
        }
        if (question.Length > MaxQuestionLength)
        {
            error = "質問は2000文字以内で入力してください。";
            return false;
        }
        // Store plain text; input must never be interpolated into XAML.
        exchanges.Add(new ChatExchange(question,
            "質問を受け取りました。\n\n入力確認：\n" + question +
            "\n\nこれは表示確認用の仮回答です。実際の戦況に基づく助言は、後の段階で対応します。"));
        if (exchanges.Count > MaxExchanges) exchanges.RemoveAt(0);
        error = string.Empty;
        return true;
    }

    internal void Clear() => exchanges.Clear();
}
