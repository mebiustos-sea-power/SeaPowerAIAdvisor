using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor;

internal interface IGeminiTransport
{
    Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation);
}

internal sealed class GeminiTransport : IGeminiTransport, IDisposable
{
    internal const int MaxResponseBytes = 2_000_000;
    private readonly HttpClient client;
    private readonly AdvisorTrace? trace;
    private int requests;
    internal GeminiTransport(AdvisorTrace? trace = null) : this(new HttpClientHandler { AllowAutoRedirect = false }, trace) { }
    internal GeminiTransport(HttpMessageHandler handler, AdvisorTrace? trace = null)
    { client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }; this.trace = trace; }

    public async Task<JObject> GenerateAsync(AdvisorSettings settings, JObject body, CancellationToken cancellation)
    {
        var index = ++requests;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var json = body.ToString(Formatting.None);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://generativelanguage.googleapis.com/v1beta/models/" + settings.RequestModel + ":generateContent");
            request.Headers.Add("x-goog-api-key", settings.RequestApiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            trace?.Request(index, json, settings.IsFallbackKey, settings.RequestModel);
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
                trace?.Response(index, status, Encoding.UTF8.GetString(bytes.ToArray()), complete, bytes.Length);
            }
            if (!response.IsSuccessStatusCode)
                throw new AdvisorConnectionException(status == 401 || status == 403 ? "api_access_denied" :
                    status == 429 ? "rate_limited" : status == 400 ? "api_bad_request" : status == 404 ? "model_unavailable" : "api_error", status);
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
            throw new AdvisorConnectionException(code);
        }
        catch (AdvisorConnectionException error) { trace?.RequestFailed(index, error.Code, error.HttpStatus); throw; }
        catch (HttpRequestException) { trace?.RequestFailed(index, "network_error"); throw new AdvisorConnectionException("network_error"); }
        catch (IOException) { trace?.RequestFailed(index, "network_error"); throw new AdvisorConnectionException("network_error"); }
    }

    public void Dispose() => client.Dispose();
}
