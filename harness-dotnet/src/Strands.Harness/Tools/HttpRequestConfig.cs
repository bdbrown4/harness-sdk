using System.Net.Http;

namespace Strands.Harness.Tools;

/// <summary>The pinned SDK response and input conventions selected for an HTTP tool.</summary>
public enum HttpRequestBehavior
{
    /// <summary>Require a 2xx response after reading UTF8 text.</summary>
    TypeScript,
    /// <summary>Reject HTTP errors before reading; preserve declared text encoding and repeated headers.</summary>
    Python
}

/// <summary>Native bounded HTTP settings. Any supplied client remains caller-owned.</summary>
public sealed record HttpRequestConfig
{
    /// <summary>The registered tool name.</summary>
    public string Name { get; init; } = "http_request";
    /// <summary>The model-facing description.</summary>
    public string Description { get; init; } = "Makes HTTP requests to external APIs. Supports GET, POST, PUT, DELETE, PATCH, HEAD, and OPTIONS methods. Returns response with status, headers, and body.";
    /// <summary>The scoped input and response convention. Native timeouts are aggregate deadlines, not HTTPX phase timers.</summary>
    public HttpRequestBehavior Behavior { get; init; }
    /// <summary>An optional borrowed client; auth, redirect and transport settings are not changed.</summary>
    public HttpClient? HttpClient { get; init; }
    /// <summary>The maximum UTF8 request body size. Oversize input is rejected before sending.</summary>
    public int MaximumRequestBodyBytes { get; init; } = 5 * 1024 * 1024;
    /// <summary>The maximum returned body size. Oversize responses fail rather than truncate.</summary>
    public int MaximumResponseBodyBytes { get; init; } = 5 * 1024 * 1024;
    /// <summary>The maximum input or projected response header bytes.</summary>
    public int MaximumHeaderBytes { get; init; } = 64 * 1024;
}
