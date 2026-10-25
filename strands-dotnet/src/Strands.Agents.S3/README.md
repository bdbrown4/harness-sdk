# Amazon S3 byte storage

Optional native byte storage using the official `AWSSDK.S3`, targeting `netstandard2.0`, `net8.0`, and `net10.0`.

```csharp
using Strands.Agents.S3;

await using var storage = new S3Storage(existingAmazonS3Client, "my-bucket",
    new S3StorageOptions { Prefix = "application/data" });
await storage.WriteAsync("example", new byte[] { 1, 2, 3 });
var bytes = await storage.ReadAsync("example");
```

A supplied official `IAmazonS3` remains borrowed and unchanged. The bucket-only constructor creates an owned client lazily, retaining the AWS SDK's credential and region resolution. Disposal closes admission, requests the store's own cancellation, joins accepted work, and disposes only an owned client.

The default missing-key policy accepts exact `NoSuchKey` and `NotFound` codes. Select `S3MissingKeyPolicy.NoSuchKeyOnly` for the pinned Python policy. HTTP 404 alone does not establish a missing object.

See the [storage contract](../../docs/S3_STORAGE.md) for source differences and limits, and [validation results](../../docs/VALIDATION_RESULTS.md) for source-specific platform and fresh-package evidence. Deterministic verification uses official SDK virtual methods and a localhost TCP endpoint with dummy credentials; it does not establish live AWS access or IAM behavior.
