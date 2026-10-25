using System.Net;
using System.Text.Json;

namespace Strands.Agents.A2A;

/// <summary>An invalid or incomplete A2A wire exchange.</summary>
public sealed class A2AProtocolException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>A rejected HTTP exchange or transport failure.</summary>
public sealed class A2AHttpException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>The received HTTP status, when a response was available.</summary>
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>A protocol-defined JSON-RPC error returned by a remote agent.</summary>
public sealed class A2ARpcException : Exception
{
    /// <summary>Captures the remote code, message and detached extension data.</summary>
    public A2ARpcException(int code, string message, JsonElement? errorData = null) : base(message)
    { Code = code; ErrorData = errorData?.Clone(); }
    /// <summary>The exact JSON-RPC error code.</summary>
    public int Code { get; }
    /// <summary>The detached optional JSON-RPC error data.</summary>
    public JsonElement? ErrorData { get; }
}
