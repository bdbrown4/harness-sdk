# MCP tool discovery and output schemas

Native discovery validates every bounded page before accepting the discovery. A malformed later page, throwing filter, invalid header annotation, or cancellation observed before acceptance leaves previously accepted native output schemas and official known-tool definitions unchanged.

Successful discovery merges selected server names into the native output-schema map. Unrelated names remain accepted. A newly accepted definition with no output schema explicitly removes output validation for that name. Independent discovery scopes can overlap: the latest native acceptance for a server name supplies the schema for future by-name calls. Prefixes affect adapted names, not the server-name key. The official known-tool cache remains additive, including unselected definitions; discovery does not call its RemoveKnownTools or ClearKnownTools methods.

A by-name CallToolAsync captures its output schema before sending the request. Each returned McpTool retains the output schema accepted when that wrapper was created, including an explicit absence. Later discovery cannot change validation for an already admitted call or an older wrapper. Ordinary results, finalized task results, and legacy task results pass through the same native output-validation core. Error results retain their existing behavior.

Output validation is distinct from the official client's latest-definition HTTP parameter-header cache. Applications can also use the public Protocol client directly; these operations are separate authority and are not a transaction with native discovery. This increment does not publish replacement tools into an Agent, register notification subscriptions, or promise rollback of server effects.

The official 2.2.0 client validates the complete AddKnownTools batch before any known-tool cache writes. Its rejection path can log. Both native connection factories currently omit the official loggerFactory argument, which fixes that path to NullLogger. The private protocol client cannot be replaced through the native API. The native publication gate therefore runs no application logger or discovery delegate. Adding protocol-logger configuration requires reviewing this boundary again.

The exact upstream implementation audited is csharp-sdk commit 6fa3825973949a9c4f0cd8af344e15a8db09dc35, Client/McpClientImpl.cs AddKnownTools and its constructor, Client/McpHeaderExtractor.cs, and Protocol/Tool.cs. No private reflection, copied header validator, new disposal coordinator, or public API is introduced by this prerequisite.
