using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal sealed class AdvisorHttpTransport : IDisposable
{
    internal const int MaxResponseBytes = 2_000_000;
    private readonly HttpClient client;
    private readonly AdvisorTrace? trace;
    private int requests;
    private AdvisorErrorCapture? errors;
    internal void ObserveErrors(AdvisorErrorCapture capture) => errors = capture;
    internal void CaptureFailure(Exception error) => errors?.Failure(error);
    internal static string ErrorCode(int status) => status == 401 || status == 403 ? "api_access_denied" :
        status == 429 ? "rate_limited" : status == 400 ? "api_bad_request" : status == 404 ? "model_unavailable" : "api_error";
    internal int RequestCount => Volatile.Read(ref requests);
    internal int BeginRequest(string json, bool fallback, string model)
    {
        var index = Interlocked.Increment(ref requests); errors?.Begin(index, fallback, model); trace?.Request(index, json, fallback, model); return index;
    }
    internal void EndRequest(int request, int status, string body, bool complete)
    { errors?.Response(request, status, body, complete); trace?.Response(request, status, body, complete, Encoding.UTF8.GetByteCount(body)); }
    internal void FailRequest(int request, string code, int? status)
    { errors?.Failure(new AdvisorConnectionException(code, status), request); trace?.RequestFailed(request, code, status); }
    internal AdvisorHttpTransport(AdvisorTrace? trace = null) : this(new HttpClientHandler { AllowAutoRedirect = false }, trace) { }
    internal AdvisorHttpTransport(HttpMessageHandler handler, AdvisorTrace? trace = null)
    { client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; this.trace = trace; }

    internal async Task<JObject> SendAsync(Uri endpoint, JObject body, int timeoutSeconds, bool fallback, string model,
        CancellationToken cancellation, IReadOnlyDictionary<string, string>? headers = null)
    {
        var index = Interlocked.Increment(ref requests);
        errors?.Begin(index, fallback, model);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var json = body.ToString(Formatting.None);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (headers != null) foreach (var header in headers) request.Headers.Add(header.Key, header.Value);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            trace?.Request(index, json, fallback, model);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            using var bytes = new MemoryStream();
            var complete = false;
            var tooLarge = false;
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var buffer = new byte[8192];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false);
                    if (count == 0) { complete = true; break; }
                    // Keep every byte actually read, including the final chunk that
                    // crosses the existing limit, and mark that body as incomplete.
                    bytes.Write(buffer, 0, count);
                    if (bytes.Length > MaxResponseBytes) { tooLarge = true; break; }
                }
            }
            finally
            {
                var received = Encoding.UTF8.GetString(bytes.ToArray());
                errors?.Response(index, status, received, complete);
                trace?.Response(index, status, received, complete, bytes.Length);
            }
            if (!response.IsSuccessStatusCode)
                throw new AdvisorConnectionException(ErrorCode(status), status);
            if (tooLarge) throw new AdvisorConnectionException("response_too_large");
            try
            {
                using var source = new StringReader(Encoding.UTF8.GetString(bytes.ToArray()));
                using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
                var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonReaderException();
                return result;
            }
            catch (JsonException) { throw new AdvisorConnectionException("invalid_response"); }
        }
        catch (OperationCanceledException)
        {
            var code = cancellation.IsCancellationRequested ? "cancelled" : "request_timeout";
            trace?.RequestFailed(index, code);
            if (cancellation.IsCancellationRequested) throw;
            var error = new AdvisorConnectionException(code); errors?.Failure(error, index); throw error;
        }
        catch (AdvisorConnectionException error) { errors?.Failure(error, index); trace?.RequestFailed(index, error.Code, error.HttpStatus); throw; }
        catch (HttpRequestException cause) { var error = new AdvisorConnectionException("network_error"); errors?.Failure(error, index, cause); trace?.RequestFailed(index, "network_error"); throw error; }
        catch (IOException cause) { var error = new AdvisorConnectionException("network_error"); errors?.Failure(error, index, cause); trace?.RequestFailed(index, "network_error"); throw error; }
    }

    public void Dispose() => client.Dispose();
}
