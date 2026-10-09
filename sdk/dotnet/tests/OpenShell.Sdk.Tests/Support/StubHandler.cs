// SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OpenShell.Oidc;

namespace OpenShell.Tests.Support;

/// <summary>A request captured by <see cref="StubHandler"/>, with its body read eagerly.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Url, string Body, HttpRequestHeaders Headers)
{
    public Dictionary<string, string> Form => CallbackServer.ParseQuery(Body);
}

/// <summary>An <see cref="HttpMessageHandler"/> that records requests and answers them from a delegate.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public Func<RecordedRequest, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    public int CountTo(string pathSuffix) => Requests.Count(r => r.Url.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers);
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        return Respond(recorded);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A stub provider serving discovery plus caller-defined token and device endpoints.</summary>
    public static StubHandler Provider(
        string issuer,
        Func<RecordedRequest, HttpResponseMessage>? token = null,
        Func<RecordedRequest, HttpResponseMessage>? device = null,
        bool pkce = true,
        bool deviceEndpoint = false,
        Func<string, string>? discoveryTransform = null)
    {
        var handler = new StubHandler();
        handler.Respond = request =>
        {
            var path = request.Url.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                var trimmed = issuer.TrimEnd('/');
                var doc = $$"""
                    {"issuer":"{{issuer}}","authorization_endpoint":"{{trimmed}}/authorize","token_endpoint":"{{trimmed}}/token"
                    {{(deviceEndpoint ? $",\"device_authorization_endpoint\":\"{trimmed}/device\"" : "")}}
                    {{(pkce ? ",\"code_challenge_methods_supported\":[\"plain\",\"S256\"]" : "")}}}
                    """;
                return Json(discoveryTransform?.Invoke(doc) ?? doc);
            }

            if (path.EndsWith("/token", StringComparison.Ordinal) && token is not null)
            {
                return token(request);
            }

            if (path.EndsWith("/device", StringComparison.Ordinal) && device is not null)
            {
                return device(request);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };
        return handler;
    }
}
