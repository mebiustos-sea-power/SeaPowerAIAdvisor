using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

// Routes independent workflows without imposing the standard engine's API loop.
// A sub handoff retains host-executed knowledge and never carries opaque provider state.
internal sealed class AdvisorExtensionBackend : IAdvisorBackend
{
    private readonly AdvisorService mainService;
    private readonly AdvisorService? subService;
    private readonly AdvisorApiConfiguration main;
    private readonly AdvisorApiConfiguration? sub;
    private readonly AdvisorConnectionOptions options;
    private readonly AdvisorHttpTransport transport;
    private readonly Action<AdvisorConnectionException> switched;
    internal AdvisorExtensionBackend(AdvisorService mainService, AdvisorService? subService,
        AdvisorApiConfiguration main, AdvisorApiConfiguration? sub, AdvisorConnectionOptions options,
        AdvisorHttpTransport transport, Action<AdvisorConnectionException> switched)
    { this.mainService = mainService; this.subService = subService; this.main = main; this.sub = sub;
        this.options = options; this.transport = transport; this.switched = switched; }

    public async Task<AdviceResult> ConsultAsync(AdvisorContext context, CancellationToken cancellation)
    {
        var switchedToSub = false;
        using var knowledge = new KnowledgeLedger(context.Knowledge);
        var detached = new AdvisorContext(knowledge, context.Input.Copy(), context.Progress);
        async Task<AdviceResult> Run(AdvisorService service, AdvisorApiConfiguration configuration, bool fallback)
        {
            cancellation.ThrowIfCancellationRequested();
            var continuation = knowledge.Continuation();
            var backend = service.Independent
                ? service.OpenIndependent(configuration, options, fallback, transport, continuation)
                : new AdvisorEngine(service.Open(configuration, options, fallback, transport), continuation: fallback ? continuation : null);
            try { return await backend.ConsultAsync(detached, cancellation).ConfigureAwait(false); }
            finally { (backend as IDisposable)?.Dispose(); }
        }
        try
        {
            try
            {
                var answer = await Run(mainService, main, false).ConfigureAwait(false);
                return Complete(answer, false);
            }
            catch (AdvisorConnectionException error) when (sub != null && AdvisorConnection.CanSwitch(error))
            {
                cancellation.ThrowIfCancellationRequested(); switchedToSub = true; switched(error);
                context.Progress?.Invoke(new AdvisorProgress(AdvisorStage.SwitchingKey, transport.RequestCount,
                    knowledge.RoundsUsed, knowledge.MaxRounds, usingFallback: true, model: subService!.Model(sub)));
                await Task.Delay(1000, cancellation).ConfigureAwait(false);
                return Complete(await Run(subService!, sub, true).ConfigureAwait(false), true);
            }
        }
        catch (AdvisorConnectionException error) when (sub != null && switchedToSub)
        { throw new AdvisorConnectionException(error.Code, error.HttpStatus, true); }

        AdviceResult Complete(AdviceResult answer, bool fallback) => new(answer.Text,
            Math.Max(transport.RequestCount, answer.ApiRequests), knowledge.RoundsUsed,
            fallback ? null : answer.TotalTokens, knowledge.Queries(), answer.Model.Length == 0
                ? (fallback ? subService!.Model(sub!) : mainService.Model(main)) : answer.Model);
    }

    private sealed class KnowledgeLedger : IAdvisorQuerySession, IDisposable
    {
        private readonly IAdvisorQuerySession inner;
        private readonly object sync = new();
        private readonly JArray retrievals = new();
        private bool disposed;
        internal KnowledgeLedger(IAdvisorQuerySession inner) { this.inner = inner; }
        public string Id => inner.Id;
        public int RoundsUsed => inner.RoundsUsed;
        public int MaxRounds => inner.MaxRounds;
        public AdvisorPrompts Prompts => inner.Prompts;
        public JObject InitialInput() => inner.InitialInput();
        public JObject Execute(JObject request)
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(KnowledgeLedger));
                var result = inner.Execute((JObject)request.DeepClone());
                retrievals.Add(new JObject { ["request"] = request.DeepClone(), ["response"] = result.DeepClone() });
                return result;
            }
        }
        internal AdvisorContinuation Continuation() { lock (sync) return new(retrievals, RoundsUsed, MaxRounds); }
        internal IReadOnlyList<string> Queries()
        { lock (sync) return retrievals.OfType<JObject>().SelectMany(r => (r["request"]?["requests"] as JArray ?? new JArray()).OfType<JObject>()).Select(q => (string?)q["query"] ?? "unknown_query").ToArray(); }
        public void Dispose() { lock (sync) disposed = true; }
    }
}
