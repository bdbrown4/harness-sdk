# A2A discovery and authenticated cards

`A2AServerOptions.Skills` accepts detached `A2AAgentSkill` declarations. A skill retains its protocol JSON, including examples, per-skill MIME modes, security requirements, and extensions. `A2AAgentSkill.FromTool(tool.Spec)` maps a native tool name to the skill's identifier and name, preserves its description, and uses empty tags, matching the pinned Python adapter.

```csharp
await using var server = new A2AServer(executor, new()
{
    Name = "Weather",
    Description = "Weather research",
    Url = new Uri("https://weather.example/rpc"),
    Skills = tools.Select(tool => A2AAgentSkill.FromTool(tool.Spec)).ToArray(),
    AdditionalCardFields = JsonSerializer.SerializeToElement(new
    {
        provider = new { organization = "Weather", url = "https://weather.example/" },
        documentationUrl = "https://weather.example/docs"
    })
});
JsonElement publicCard = server.AgentCard;
```

The supplied skills and additional JSON are copied on construction. Discovery and metadata reads do not create an agent. The embedding application can initialize an agent and explicitly derive skills from its resulting tool registry when it wants plugin-derived metadata.

The application version defaults to `0.0.1`, matching both pinned implementations. `CardFormat.Native` defaults to A2A `0.3.0`, JSONRPC, and the handler's configured MIME modes (`text/plain` and `application/json` by default). `CardFormat.TypeScript` uses the pinned adapter's protocol `0.2.0` and `text/plain` defaults. `CardFormat.Python` uses protocol `0.3.0` and its legacy `text` modes. Both explicit language formats require configured `text/plain` runtime support; they cannot advertise unsupported input or output. These profiles affect discovery defaults, while native execution and authorization settings remain explicit. Native skills default to the empty TypeScript list. Explicit tool-to-skill conversion provides Python's metadata mapping without invoking a hidden representative factory.

`AdditionalCardFields` supplies optional provider, documentation, security, signature, and application extension fields. It cannot replace runtime-bound identity, modes, skills, or capabilities. `UpdateAgentCard` atomically replaces a complete detached card, supporting explicit metadata, skills, and public URL changes. Earlier returned cards remain valid. Updating an advertised URL does not change the embedding host's routing; the host must expose that endpoint.

Updates validate required fields, supported protocol versions, JSONRPC interfaces, configured MIME support, unique skill identifiers, standard security-scheme member shapes, OAuth flow fields, protocol extensions, and signature member types. Unknown extension fields retain their wire casing. Security declarations describe the embedding host's authentication; this handler does not enforce the advertised schemes. Signature fields are retained metadata: the SDK does not generate or cryptographically verify card signatures in this increment. Extension declarations likewise describe host-supplied behavior.

The card cannot enable push delivery or state-transition history because those runtime features remain absent. `MaxCardBytes` defaults to 1 MiB of serialized UTF-8 card JSON, charging escaped Unicode as it appears on the wire. Invalid, oversized, or unsupported updates leave the prior card intact.

An authenticated extended card requires both `AuthenticatedExtendedCard` and an explicit `AuthorizeExtendedCard` callback. The callback receives the borrowed `HttpRequestMessage` and cancellation token, allowing the embedding host to apply its own identity and authorization services. The handler advertises `supportsAuthenticatedExtendedCard` only when that card and callback are configured. Returning `false` produces HTTP 403. Host authentication and credential validation remain application responsibilities.

`agent/getAuthenticatedExtendedCard` accepts the protocol's no-parameters request. When no card is configured, it returns the specific A2A error `-32007`. Authorized responses contain the detached private card; ordinary discovery continues returning public metadata. The native client caches an explicitly retrieved authenticated card for that client instance. Applications should scope clients and their configured credentials to the intended authority.

Authorization reads are tracked through server shutdown. Disposal cancels and drains accepted reads, and callbacks must cooperate with cancellation. A callback cannot await disposal of its own server or recursively read its own extended card. Caller cancellation preserves the caller token, including cancellation immediately after the authorization await. Backend errors produce a sanitized, correlated JSON-RPC internal error without publishing the private metadata.

Pinned implementation references at `3bd98e618320f0f286acbfb2252f72388030421b`:

- `strands-ts/src/a2a/server.ts`
- `strands-py/src/strands/multiagent/a2a/server.py`

Protocol member and method shapes follow the official [A2A JavaScript 0.3.10 types](https://raw.githubusercontent.com/a2aproject/a2a-js/v0.3.10/src/types.ts) and [A2A 0.3 specification](https://a2a-protocol.org/v0.3.0/specification/). A subsequent increment adds the [remote graph stable-pause bridge](MULTIAGENT_A2A_GRAPH.md). Hosting adapters, push notification delivery, active execution crash recovery, and full signed-card verification remain separate acceptance work. Exact source hashes and test evidence for the card checkpoint are recorded in `A2A_CARDS_SCOPE.json`.
