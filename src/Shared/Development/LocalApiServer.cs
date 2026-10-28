using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SeaPowerAIAdvisor.Shared;

// An exact IPv4 loopback prefix, no CORS, and a required non-simple header prevent browser form requests.
internal sealed class LocalApiServer : IDisposable
{
    private readonly string requestHeader;
    private readonly HttpListener listener = new();
    private readonly Func<string, string, JObject, Task<LocalApiResponse>> dispatch;
    private readonly Action<string> warn;
    private readonly CancellationTokenSource stopping = new();
    private int active;
    private string? failure;
    internal string? Failure => Volatile.Read(ref failure);
    internal LocalApiServer(Func<string, string, JObject, Task<LocalApiResponse>> dispatch, Action<string> warn, string endpoint, string requestHeader)
    {
        var uri = new Uri(endpoint);
        if (uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/") throw new ArgumentException("A loopback HTTP endpoint is required.");
        if (string.IsNullOrWhiteSpace(requestHeader)) throw new ArgumentException("A developer request header is required.");
        this.dispatch = dispatch; this.warn = warn; this.requestHeader = requestHeader;
        listener.Prefixes.Add(endpoint.TrimEnd('/') + "/");
    }
    internal void Start() { listener.Start(); _ = Accept(); }
    private async Task Accept()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                if (Interlocked.Increment(ref active) > 16)
                { Interlocked.Decrement(ref active); context.Response.StatusCode = 429; context.Response.Close(); }
                else _ = Serve(context);
            }
        }
        catch (Exception error) when (stopping.IsCancellationRequested && (error is HttpListenerException || error is ObjectDisposedException)) { }
        catch (Exception error) { Volatile.Write(ref failure, error.Message); warn("Developer local API listener failed: " + error); }
    }
    private async Task Serve(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            LocalApiResponse response;
            if (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                request.Url?.Host != "127.0.0.1" || request.Headers["Origin"] != null || request.Headers[requestHeader] != "1")
                response = LocalApiResponse.Error(403, "local_developer_request_required");
            else
            {
                JObject body = new();
                if (request.HttpMethod != "GET")
                {
                    if (!string.Equals((request.ContentType ?? "").Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                        throw new LocalApiException(415, "json_required");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using var data = new MemoryStream(); var buffer = new byte[4096];
                    int read;
                    while ((read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        if (data.Length + read > 262144) throw new LocalApiException(413, "body_too_large");
                        data.Write(buffer, 0, read);
                    }
                    if (data.Length > 0)
                    {
                        using var source = new StringReader(new UTF8Encoding(false, true).GetString(data.ToArray()));
                        using var reader = new JsonTextReader(source) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 };
                        body = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                        if (reader.Read()) throw new JsonReaderException("Unexpected trailing content.");
                    }
                }
                response = await dispatch(request.HttpMethod, request.Url!.AbsolutePath, body).ConfigureAwait(false);
            }
            await Write(context, response).ConfigureAwait(false);
        }
        catch (LocalApiException error) { await TryError(context, error.Status, error.Code).ConfigureAwait(false); }
        catch (Exception error) when (error is JsonException || error is DecoderFallbackException) { await TryError(context, 400, "invalid_json").ConfigureAwait(false); }
        catch (OperationCanceledException) { await TryError(context, 408, "request_timeout").ConfigureAwait(false); }
        catch (Exception error)
        {
            if (!stopping.IsCancellationRequested) warn("Developer local API request failed: " + error);
            await TryError(context, 500, "local_request_failed").ConfigureAwait(false);
        }
        finally
        {
            try { context.Response.Close(); }
            catch (Exception error) when (error is HttpListenerException || error is ObjectDisposedException) { }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    private static async Task TryError(HttpListenerContext context, int status, string code)
    { try { await Write(context, LocalApiResponse.Error(status, code)).ConfigureAwait(false); } catch (Exception error) when (error is IOException || error is HttpListenerException || error is ObjectDisposedException) { } }
    private static async Task Write(HttpListenerContext context, LocalApiResponse response)
    {
        var bytes = Encoding.UTF8.GetBytes(response.Data.ToString(Formatting.None));
        context.Response.StatusCode = response.Status; context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store"; context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }
    public void Dispose() { stopping.Cancel(); listener.Close(); }
}
