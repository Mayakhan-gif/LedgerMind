using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace LedgerMind.Tests.Infrastructure.Fakes;

public sealed record RecordedRequest(
    HttpMethod Method,
    Uri? Uri,
    string? Body,
    string? Authorization,
    IReadOnlyDictionary<string, string> Headers);

// Groq / Xero ki HTTP calls ke liye: jawab pehle se queue me daalein, request yaad rehti hai.
// Queue khali ho to 500 deta hai (taake bhooli hui setup test me saaf nazar aaye).
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public void EnqueueJson(HttpStatusCode status, string json) =>
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        _responses.Enqueue(responder);

    public void Reset()
    {
        while (_responses.TryDequeue(out _)) { }
        while (Requests.TryDequeue(out _)) { }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var headers = request.Headers.ToDictionary(
            h => h.Key,
            h => string.Join(",", h.Value),
            StringComparer.OrdinalIgnoreCase);

        Requests.Enqueue(new RecordedRequest(
            request.Method,
            request.RequestUri,
            body,
            request.Headers.Authorization?.ToString(),
            headers));

        if (!_responses.TryDequeue(out var responder))
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                RequestMessage = request,
                Content = new StringContent("{\"error\":\"no fake response queued\"}", Encoding.UTF8, "application/json")
            };
        }

        var response = responder(request);
        response.RequestMessage = request;
        return response;
    }
}
