# Bedrock Knowledge Base memory

This optional package retrieves memory from an existing Amazon Bedrock Knowledge Base through the official AWS SDK. It implements the pinned TypeScript/Python retrieval contracts. Read-only behavior remains the default. Optional writes ingest CUSTOM inline text or upload S3 text and metadata sidecars before direct ingestion. Provisioning and status polling are not included.

Supply borrowed official clients when the host manages credentials, endpoints, retries, or client lifetime:

~~~csharp
var store = new BedrockKnowledgeBaseStore(new()
{
    Name = "knowledge",
    KnowledgeBaseId = "KB12345678",
    RuntimeClient = runtimeClient,
    AgentClient = managementClient,
    Profile = BedrockKnowledgeBaseProfile.TypeScript
});
var entries = await store.SearchAsync("deployment preferences");
await store.DisposeAsync();
~~~

The clients remain usable after the store closes. When a client is omitted, the store creates one lazily using normal official SDK credential/region policy. Region configures only owned clients; borrowed client configuration is never rewritten. Disposing an unused store creates no client. There is no public HTTP-client factory or custom transport protocol.

KnownKnowledgeBaseType avoids type detection. Otherwise successful initialization caches one type; concurrent initialization shares that valid result, while failed or canceled detection can be retried. The explicit known type's Value is compared ordinally with MANAGED; every other value selects vector search. Official SDK unmarshalling canonicalizes recognized lower-case service constants before this comparison. That SDK behavior differs from comparing the raw foreign response string and is explicitly characterized.

Direct search requests ten results unless MaxSearchResults is configured. The explicit positive limit overload wins. A MemoryManager without a store limit requests three; a read-only manager can share the store and its type cache with another conversation. The manager does not own or dispose this store. Search returns the service's order without a local Take, fabricated document ID, or automatic nextToken traversal. Inclusive minimum/maximum score bounds are mutually exclusive; unscored rows survive. NaN bounds follow source comparisons, so they discard scored rows.

Options and supported official filter graphs are detached before asynchronous work. Cyclic filter/Document graphs are rejected. A supplied empty filter overrides scope in the default TypeScript profile; Python treats it as absent and falls back to scope. Empty scope supplies no scope filter; whitespace scope is retained. A null scope key defaults to namespace, as does an empty key in Python. The source profiles also choose reserved metadata names:

- TypeScript: _sourceLocation, _relevanceScore.
- Python: _source_location, _relevance_score.

The reserved fields replace colliding user metadata. Entries always carry detached object metadata, even when empty. Missing result text becomes an empty string. The official SDK determines numeric precision and which typed location fields are available; this adapter does not invent an opaque raw response API.

TypeScript unwraps only top-level metadata objects with type: "bigDecimal" and a nonblank string field named string. Conversion follows finite ECMAScript Number forms, including exponent and unsigned hexadecimal/binary/octal literals. Invalid or nonfinite wrappers remain objects; nested wrappers stay untouched. Python keeps wrappers opaque. The native parser uses the explicit ECMAScript whitespace set rather than assuming CLR Trim is equivalent.

Terminal disposal closes admission, requests cancellation of operation tokens, physically joins accepted SDK tasks, and then attempts every created owned-client cleanup. There is no timeout that abandons accepted I/O. Late caller cancellation after successful I/O can still produce an OCE with that caller's token. Only mismatched cancellation exceptions are normalized when the actual caller is canceled; an independent SDK failure remains primary even if cancellation races. Matching caller OCE identity/stack is retained. Cleanup failures preserve their first cause and reference-unique secondary errors.

Live SDK/logging/cleanup callbacks cannot synchronously wait for this store's own disposal. Receipts expire when their operation/cleanup returns; unrelated stores and later escaped work remain independent. This is a bounded cooperative callback contract, not universal self-await detection. Application code must not detach uncooperative callback work and then make the callback wait for its own store shutdown.

Phase 0 verified ordinary NuGet consumers on .NET 8/10 and installed Framework 4.8.1 x64/x86 with official per-target clients. The official Core net472 asset lacks Amazon.Runtime.HttpClientFactory; the retained failed modern-factory experiment is not a supported Framework transport path. The public boundary is the typed AWS clients. No forced modern SDK assets or reflection shim is used.

No live AWS/IAM/knowledge-base provisioning, clean Framework 4.8, or ingestion consistency is established by localhost tests. Phase 0's S3 path-style versus default virtual-host key observations constrain the supported S3 write routes described below.


## Optional CUSTOM writes

Set Writable=true, DataSourceType=ContentDataSourceType.CUSTOM, and a nonempty DataSourceId to enable AddAsync. S3 requires the separate configuration below; all other source types are rejected before owned clients are created. The class implements the existing IWritableMemoryStore capability; its Writable flag still controls manager routing. The result is an immutable BedrockKnowledgeBaseAddResult whose DocumentId is the exact identifier submitted to the official IngestKnowledgeBaseDocuments request. Direct writes do not add a Get/type-detection request. Existing manager programmatic writes, add-memory tools, and optional Extraction configuration use the same explicit write capability.

The default TypeScript profile generates RFC UUIDv7 identifiers, with a 48-bit Unix-millisecond timestamp, random remaining bits, and the prescribed version/variant. Python generates UUIDv4. Identifiers are unique handles; there is no monotonic ordering guarantee. Original nonempty content is submitted without trimming. Emptiness follows the explicit ECMAScript trim or Python strip character set, including their different BOM/NEL/control-separator treatment.

Metadata must be an object-valued JsonElement, detached before asynchronous work. Supported attributes are strings (including empty), numbers represented by the official SDK double field, booleans, and nonempty lists containing only strings. Unsupported objects/null/mixed or empty lists are dropped with observational diagnostics. A nonempty scope supplies the first attribute and overrides colliding caller metadata. Explicit retrieval filters affect only search, not write scope. Native JSON/official SDK number precision and valid wire-number limits still apply.

AccessControlList uses official typed entries and is detached at construction, preserving future principal/access constant strings. Null differs from an empty list: TypeScript treats a present empty list as configured, requires at least one supported attribute, and emits an empty ACL when attributes exist. Python treats an empty list as false for inline projection and omits it. A nonempty ACL in either profile requires an inline metadata attribute. No AWS global collection policy is changed.

Only a null ACL plus the official ValidationException with ErrorCode exactly ValidationException and a message containing contiguous accessControlList (case-insensitively) is remapped to an actionable InvalidOperationException. Its InnerException is the original service exception. Configured empty ACLs suppress this remap in both profiles. Other failures retain their identity/stack, and writes reuse the accepted caller-cancellation and physical-disposal contract. Borrowed clients remain borrowed; no portable HttpClientFactory is introduced.

A successful ingestion may already have persisted the generated document when late caller cancellation is observed before AddAsync returns. The adapter does not compensate, delete, or retry that accepted write. The native identifier result is returned only on successful completion of the call.

## S3 document writes

Use `Writable = true`, `DataSourceType = ContentDataSourceType.S3`, the existing `DataSourceId`, and `S3 = new BedrockKnowledgeBaseS3Options { Bucket = "existing-bucket", Prefix = "memory/", Client = borrowedS3 }`. The optional client is borrowed; without it the store lazily creates an owned official S3 client using `Region`. The package pins `AWSSDK.S3` 4.0.104.5 and retains the standard vendor HTTP path on Framework; no custom HttpClientFactory is required.

Each write preserves the original nonempty text as UTF-8 without a BOM. The prefix is opaque: the store appends `/` only when absent and appends a private profile UUID plus `.txt`. TypeScript uses UUIDv7; Python uses UUIDv4. The returned `DocumentId` is the literal `s3://bucket/key` handle accepted by the subsequent ingestion call. Configuration, ACL entries, and metadata are detached before the first await. Borrowed client routing must remain stable during a write; the store does not change or police arbitrary concurrent client reconfiguration.

The store uploads content first, builds supported typed metadata and scope attributes, optionally uploads a compact `.metadata.json` sidecar, then calls direct ingestion with S3 content/metadata locations. Sidecar values retain typed `type`/value members and `includeForEmbedding: false`; nonempty ACL entries use the S3 sidecar's `Name`, `Type`, and `Access` fields. An absent or empty ACL alone does not create a sidecar under either profile. Unlike CUSTOM inline ACLs, a nonempty S3 ACL may create a sidecar with an empty metadataAttributes object. JSON values and wire field names are preserved; serializer escape spelling is not a cross-language byte-identity guarantee.

The official vendor transport drops an initial key slash for the known forced-path-style/custom-ServiceURL routes characterized in Phase0. The store therefore rejects a generated leading-slash key **before the first PUT** for those routes, including an empty prefix. It does not normalize keys, substitute percent aliases, or ingest a handle for a different uploaded object. This is a native vendor-transport restriction and an open opaque-route parity qualification. Default regional virtual-host leading-slash key behavior is supported by retained pre-transport vendor evidence; no live AWS upload proof is claimed.

Cancellation closes normal admission and physically joins every accepted SDK task. Cancellation observed after a remote success can leave a committed object or ingestion while returning the original caller-token OCE. The store does not delete uploaded objects after sidecar/ingestion failure, compensate, add retry loops, start ingestion jobs, or poll. Vendor client policies remain authoritative. Store disposal joins all accepted operations, attempts each created owned runtime/agent/S3 client cleanup, and preserves borrowed clients. Source read-only/CUSTOM defaults and accepted retrieval behavior remain unchanged.
