using System;

namespace SeaPowerAIAdvisor.Shared;

internal interface IAiTraceObserver
{
    void Request(int request, string json, bool fallback = false, string? model = null);
    void Response(int request, int status, string body, bool complete, long receivedBytes);
    void RequestFailed(int request, string code, int? status = null);
}

internal interface IAiErrorObserver
{
    void Begin(int id, bool fallback, string model);
    void Response(int id, int status, string body, bool complete);
    void Failure(Exception error, int? request = null, Exception? cause = null);
}
