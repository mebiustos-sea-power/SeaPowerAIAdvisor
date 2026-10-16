using System.Collections.Generic;
using System.Text;

namespace SeaPowerAIAdvisor;

internal static class AdvisorTranscript
{
    internal static string Format(IEnumerable<ChatExchange> exchanges, string player, string advisor, string empty)
    {
        var text = new StringBuilder();
        foreach (var exchange in exchanges)
        {
            if (text.Length > 0) text.Append("\n\n────────────\n\n");
            text.Append(player).Append('\n').Append(exchange.DisplayQuestion)
                .Append("\n\n").Append(advisor).Append('\n').Append(exchange.Answer);
        }
        return text.Length == 0 ? empty : text.ToString();
    }
}
