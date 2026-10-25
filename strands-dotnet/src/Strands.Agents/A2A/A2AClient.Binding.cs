using System.Security.Cryptography;
using System.Text;

namespace Strands.Agents.A2A;

internal sealed record A2AEndpointBinding(string Discovery, string Configuration, string Endpoint);

public sealed partial class A2AClient
{
    internal bool MatchesConfiguration(A2AEndpointBinding binding) => binding.Discovery == HashBinding(_cardUri.AbsoluteUri) && binding.Configuration == HashBinding(_options.Endpoint?.AbsoluteUri ?? "");

    internal async Task<(A2AClient Client, A2AEndpointBinding Binding)> BindEndpointAsync(CancellationToken cancellationToken)
    {
        _ = await GetAgentCardAsync(cancellationToken).ConfigureAwait(false);
        await _cardLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = _endpoint ?? throw new A2AProtocolException("A remote node requires a discovered endpoint.");
            var client = new A2AClient(_client, _options with { Endpoint = endpoint.Url }) { _card = _card, _endpoint = endpoint };
            var identity = endpoint.Rest ? "HTTP+JSON\n" + endpoint.Url.AbsoluteUri : endpoint.Url.AbsoluteUri;
            return (client, new(HashBinding(_cardUri.AbsoluteUri), HashBinding(_options.Endpoint?.AbsoluteUri ?? ""), HashBinding(identity)));
        }
        finally { _cardLock.Release(); }
    }
    private static string HashBinding(string value)
    {
        using var hash = SHA256.Create();
        return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(part => part.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}
