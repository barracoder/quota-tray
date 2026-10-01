using System.Net;
using System.Text;

namespace QuotaTray.Providers.GitHub.Tests;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHttpMessageHandler Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue((status, body));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No canned response left for " + request.RequestUri);
        }

        var (status, body) = _responses.Dequeue();
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
