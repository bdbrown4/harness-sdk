# Portable activity identities

The native tracer requests W3C IDs when it creates each operation. It uses `ActivitySource.CreateActivity` followed by `Start`, retaining the operation handle before a listener runs. It does not change `Activity.DefaultIdFormat` or `Activity.ForceDefaultIdFormat`.

This is required for portable `ActivityContext` correlation. Framework defaults can produce hierarchical activities whose W3C trace/span IDs are empty. A hierarchical application caller also reproduced the failure on a modern runtime. Explicit native parent/child operations and links need usable W3C IDs even in these environments. Existing W3C callers retain their trace and parent span; the original ambient caller and its ID format remain unchanged. A hierarchical caller has no transferable W3C parent context, so the native operation starts its own W3C trace.

Sampling remains authoritative: no-sampling listeners create no operation, and propagation-only operations retain IDs without recording activity data. Listener failures remain observational; a start callback that throws still has its created operation stopped, with the original ambient activity restored.

Two of six new contracts were observed failing on .NET 10 before the change. A separate actual Framework memory run also exposed three missing-link/empty-context failures before this prerequisite. The remaining four identity contracts passed their first run. The tests do not change global activity ID settings. This prerequisite is captured separately from automatic memory lifecycle tracing and graph/swarm instrumentation.

The implementation uses the official `System.Diagnostics.DiagnosticSource` 10.0.10 API available to the `netstandard2.0` asset: `ActivitySource.CreateActivity(string, ActivityKind, ActivityContext, IEnumerable<KeyValuePair<string, object>>, IEnumerable<ActivityLink>, ActivityIdFormat)`.
