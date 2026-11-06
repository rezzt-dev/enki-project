using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace enki.Tests.Services;

/// <summary>Fake Supabase transport that lets a test script exactly what each request gets back — the
/// seam <see cref="enki.Services.SupabaseRestClient"/> and <see cref="enki.Services.PolarAuthService"/>
/// gained specifically so their push/pull/auth contract can be exercised without a live network dependency
/// (10.2 §6/§7). Every request is recorded in <see cref="Requests"/> so a test can assert on the method,
/// URL, headers, and body actually sent.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Request bodies captured eagerly, in the same order as <see cref="Requests"/> — the caller
    /// (e.g. <c>SupabaseRestClient.UpsertAsync</c>) disposes its <see cref="HttpRequestMessage"/> in a
    /// <c>using</c> block right after <c>SendAsync</c> returns, so reading <c>Content</c> off the stored
    /// request afterwards throws <see cref="ObjectDisposedException"/>; this list is read here, before that
    /// disposal ever happens.</summary>
    public List<string?> RequestBodies { get; } = [];

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(_ => response);

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> respond) => _responses.Enqueue(respond);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count == 0)
            throw new InvalidOperationException($"No scripted response for {request.Method} {request.RequestUri}");

        return _responses.Dequeue()(request);
    }
}
