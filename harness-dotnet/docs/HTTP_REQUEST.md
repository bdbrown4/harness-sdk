# Optional raw HTTP requests

`Strands.Harness.Tools.HttpRequest.Create(config)` returns an ordinary `ITool`.
It accepts `method`, `url`, optional string `headers` and `body`, and optional
positive `timeout` seconds. It returns JSON containing status, response headers,
and the raw response text. It does not summarize, cache, or call a model.

```csharp
using Strands.Harness.Tools;

var tool = HttpRequest.Create(new() { HttpClient = applicationClient });
// Add tool to AgentOptions.Tools, or explicitly select the Harness built-in:
var options = new HarnessOptions
{
    BuiltinTools = ["http_request"],
    HttpRequest = new() { HttpClient = applicationClient }
};
```

The eight default Harness tools remain unchanged. This optional factory belongs
to the Harness package; a Core agent can add it by referencing that package.
Portable JSON configuration has no new HTTP settings in this increment.

Explicit selection grants direct host networking. Requests do not use the
selected file or command sandbox and never fall back to curl. Supply a configured
client when transport policy, authentication, proxies, or redirect restrictions
are required. The borrowed client is used directly and never disposed or mutated;
default headers and handler authentication are evaluated when the request sends.
The tool snapshots its own configuration when created and detaches request input
before awaiting. Changing the caller's borrowed client remains caller behavior.

## Scoped source conventions

The default `TypeScript` behavior follows the pinned SDK's seven uppercase methods
and disallows bodies for GET and HEAD. It reads the body before failing a non-2xx
response, returns `statusText`, and decodes UTF8 with replacement and BOM removal.
Ordinary repeated response headers use comma-space. For `Set-Cookie`, the last
value is retained, matching the pinned assignment loop on the characterized
Node24.19 `Headers.forEach`; this is not a claim about every browser/fetch runtime.

Explicit `Python` behavior allows GET/HEAD bodies, rejects status>=400 before
opening the body, returns unfollowed3xx, uses `status_text`, joins repeated headers
with newline, and decodes a supported declared charset with UTF8 fallback.
The optional body/header/timeout values may be JSON null in this profile.
Native timeout input follows the advertised number-or-null schema. The pinned
Python decorator's additional lax Pydantic coercions (for example true to1.0 or
a numeric string to a float) are not native input guarantees. Direct Python
function calls also differ from that decorator's registered validation path.

These profiles do not execute fetch or HTTPX. An owned per-request BCL client
follows at most20 redirects for TypeScript behavior and does not follow redirects
for Python behavior. It enables GZip/Deflate decompression and has no cookie jar.
Other redirect, compression, header, HTTP version and connection behavior follows
the installed BCL. A supplied client retains all of its own settings.

Native deadlines cover the complete operation, including response-body reads.
TypeScript defaults to30s. Python defaults to5s with an owned client; a finite
borrowed client timeout caps a model-supplied timeout, and an infinite client with
no model timeout adds no deadline. HTTPX's separate connect/read/write/pool timers
are not reproduced by this aggregate budget. The borrowed `HttpClient.Timeout`
also independently governs its own send operation. Timeout input must fit the
native timer range. This is bounded HTTP capability parity, not full fetch/HTTPX
transport or error-class parity.

## Native bounds and ownership

Default request and response body bounds are5MiB each; input and projected response
headers are limited to64KiB. These configurable native bounds reject rather than
truncate. Borrowed handlers may buffer headers before projection and retain their
own limits. URLs must be absolute HTTP(S) without embedded user information.
Header names must be tokens and values portable ASCII without control characters
except tab. Transport/framing headers are rejected: Content-Length,
Transfer-Encoding, Connection, Host, Upgrade, Trailer, TE, Expect, Proxy-Connection.
Content headers such as Content-Type are supported. TypeScript string bodies
default to `text/plain;charset=UTF-8`; Python bodies add no default Content-Type.

Both the explicit caller token and the captured per-execution tool signal govern
cancellation. A requested original token is preserved and causal failures remain
available. Independent failures propagate; cleanup failures attach to the primary
through `Strands.CleanupErrors` without self or duplicate references. Responses,
requests and internally created clients are cleaned up; supplied clients remain
usable. No application callback runs under a tool lock.

Public BCL characterization on net8/net10 and installed Framework4.8.1 found that
disposing content while an asynchronous stream-open callback is pending can lose
the late stream. The tool therefore physically joins stream opening, then lets
response disposal own the stream. Cancellation-close registration begins only
after opening completes, where it can unblock active body reads. An application
handler or content implementation that ignores cancellation while opening may
delay completion; the tool does not abandon that work or promise arbitrary Open
preemption. Response and content cleanup must cooperate with the BCL ownership
contract. There is no claim of live-service, clean Framework4.8, Linux, macOS, or
full native Harness release-feed validation from the initial focused tests.
