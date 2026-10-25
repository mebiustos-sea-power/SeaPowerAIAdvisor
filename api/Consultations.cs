using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SeaPowerAIAdvisor.Api;

public sealed class ConsultationInput
{
    public string DataJson { get; }
    public string SystemInstruction { get; }
    public IReadOnlyList<string> ToolsJson { get; }
    public ConsultationInput(string dataJson, string systemInstruction, IEnumerable<string> toolsJson)
    { DataJson = dataJson; SystemInstruction = systemInstruction;
        ToolsJson = Array.AsReadOnly(toolsJson.ToArray()); }
}

// Contains only executed queries/results. Provider conversation IDs and thinking are excluded.
public sealed class RetrievalHistory
{
    public string RetrievalsJson { get; }
    public int RoundsUsed { get; }
    public int MaxRounds { get; }
    public string OperationsJson { get; }
    public RetrievalHistory(string retrievalsJson, int roundsUsed, int maxRounds, string operationsJson = "[]")
    { RetrievalsJson = retrievalsJson; RoundsUsed = roundsUsed; MaxRounds = maxRounds; OperationsJson = operationsJson; }
}

public interface IAdvisorKnowledge
{
    string ConsultationId { get; }
    int RoundsUsed { get; }
    int MaxRounds { get; }
    // A query envelope with a requests array. The host supplies consultation_id.
    string Execute(string requestJson);
}

public enum AdvisorPhase { Waiting, Retrieving, FinalAnswer, SwitchingConnection }

public sealed class AdvisorProgressUpdate
{
    public AdvisorPhase Phase { get; }
    public int ApiRequests { get; }
    public IReadOnlyList<string> Queries { get; }
    public AdvisorProgressUpdate(AdvisorPhase phase, int apiRequests, IEnumerable<string>? queries = null)
    { Phase = phase; ApiRequests = apiRequests; Queries = Array.AsReadOnly((queries ?? Array.Empty<string>()).ToArray()); }
}

public sealed class ConsultationContext
{
    public ConsultationInput Input { get; }
    public IAdvisorKnowledge Knowledge { get; }
    public RetrievalHistory Continuation { get; }
    public IAdvisorOperations? Operations { get; }
    private readonly Action<AdvisorProgressUpdate> progress;
    public ConsultationContext(ConsultationInput input, IAdvisorKnowledge knowledge, RetrievalHistory continuation,
        Action<AdvisorProgressUpdate> progress, IAdvisorOperations? operations = null)
    { Input = input; Knowledge = knowledge; Continuation = continuation; this.progress = progress; Operations = operations; }
    public void ReportProgress(AdvisorProgressUpdate update) => progress(update);
}

public sealed class AdvisorAnswer
{
    public string Text { get; }
    public string Model { get; }
    public int ApiRequests { get; }
    public long? TotalTokens { get; }
    public AdvisorAnswer(string text, string model = "", int apiRequests = 0, long? totalTokens = null)
    { Text = text; Model = model; ApiRequests = apiRequests; TotalTokens = totalTokens; }
}

// Implementations own their request sequence and must honor the supplied cancellation token.
// Dispose releases only resources owned by this consultation, never the shared host services.
public interface IAdvisorConsultation : IDisposable
{
    Task<AdvisorAnswer> ConsultAsync(ConsultationContext context, CancellationToken cancellation);
}
