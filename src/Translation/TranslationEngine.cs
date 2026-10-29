using System;
using System.Threading;
using System.Threading.Tasks;
using SeaPowerAIAdvisor.Shared;

namespace SeaPowerAIAdvisor.Translation;

// A result submission completes this task; it does not need another model-generated closing message.
internal static class TranslationEngine
{
    internal const int MaxRequestBytes = 1_000_000;
    internal static async Task<TranslationResult> RunAsync(TranslationBatch batch, IAiProvider main, IAiProvider? fallback,
        string prompt, string fallbackPrompt, Action<AiConnectionException> switched, CancellationToken cancellation)
    {
        using var connection = new AiConnection(main, fallback, sub => batch.Input(sub ? fallbackPrompt : prompt), switched);
        var reply = await connection.GenerateAsync(true, null, cancellation, (_, _, _, bytes) =>
        { if (bytes > MaxRequestBytes) throw new TranslationException("too_large"); }).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return new TranslationResult(batch.Read(reply), connection.Model, connection.ApiRequests, connection.UsingFallback ? null : reply.TotalTokens);
    }
}
