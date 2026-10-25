# Managed generation capture and concurrent writers

Native managed generations read trusted root images and declared inputs with
`FileShare.ReadWrite | FileShare.Delete`. A compatible external writer can keep
its handle open, overwrite content, or replace a file while capture is active.
The loader owns and closes each capture stream; the caller's writer stays borrowed.
Both `ResolveOptions` generations and explicit watched tool providers use this
same private capture path.

Each read consumes exactly the length observed when its stream opens. Later
growth is left for a subsequent fingerprint capture; premature end-of-stream
rejects that capture. Image bytes must fit in an `Int32` array, while a declared
file is hashed in bounded chunks without allocating its whole contents. These
bounds prevent capture from chasing an actively growing file until EOF. They
do not impose a new timeout or claim to interrupt an uncooperative filesystem.

This sharing policy does not turn a set of files into an atomic transaction.
The watched provider retains its three complete fingerprint captures and its
sequence check before publication. Observed changes request another capture;
invalid or inaccessible live images retain the last accepted tool batch. A
writer that denies read access can still prevent capture and cause a diagnostic.
Publish complete files atomically when the application's build workflow permits.

The deterministic regression holds a compatible write-capable handle during
real provider capture, separately for the root image and a declared side file.
It previously failed in the loader's read opens on Windows. That regression
demonstrates a sharing contract defect; it does not identify the conflicting
handle in the independently retained combined c387 run. The existing partial
image recovery test remains unchanged. In-memory `Assembly.Load(byte[])` does
not retain a source stream in this implementation; no persistent assembly lock
or universal multi-file consistency guarantee is claimed.
