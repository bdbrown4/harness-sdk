using System.Runtime.ExceptionServices;
using Strands.Agents;

namespace Strands.Harness;

public sealed partial class HarnessAssemblyLoader
{
    private sealed partial class DeclaredToolProvider : IToolProviderUpdates
    {
        private static readonly AsyncLocal<CallbackScope?> ActiveCallback = new();
        [ThreadStatic] private static SynchronousCallbackScope? ActiveSynchronousCallback;
        private readonly object _gate = new();
        private readonly HarnessAssemblyLoader _loader;
        private readonly IReadOnlyList<HarnessModuleReference> _references;
        private readonly Func<FileSystemWatcher> _watcherFactory;
        private readonly string[] _inputs;
        private readonly Dictionary<string, object> _consumers = new(StringComparer.Ordinal);
        private SourceGeneration? _current;
        private SourceGeneration? _closing;

        internal DeclaredToolProvider(HarnessAssemblyLoader loader, IReadOnlyList<HarnessModuleReference> references, Func<FileSystemWatcher> watcherFactory)
        {
            _loader = loader;
            _references = references;
            _watcherFactory = watcherFactory;
            _inputs = references.SelectMany(reference => new[] { reference.Module }.Concat(reference.Files))
                .Select(path => loader.ValidateProviderPath(path, true).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Distinct(OperatingPathComparer).ToArray();
        }

        public ValueTask AddConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default)
        {
            Guard.NotNull(context, nameof(context)); RefuseCallbackReentry(); cancellationToken.ThrowIfCancellationRequested();
            lock (_gate) if (!_consumers.ContainsKey(context.ConsumerId)) _consumers.Add(context.ConsumerId, new object());
            return default;
        }

        public ValueTask<IReadOnlyList<ITool>> LoadToolsAsync(ToolProviderContext context, CancellationToken cancellationToken = default)
        {
            Guard.NotNull(context, nameof(context)); RefuseCallbackReentry(); cancellationToken.ThrowIfCancellationRequested();
            return new(PreserveFailure(LoadForCallerAsync(context, cancellationToken)));
        }

        private async Task<IReadOnlyList<ITool>> LoadForCallerAsync(ToolProviderContext context, CancellationToken caller, object? admission = null)
        {
            Task? closing;
            SourceGeneration generation;
            LoadAttempt attempt;
            lock (_gate)
            {
                admission = RequireConsumer(context, admission);
                closing = _closing?.Closed.Task;
                if (closing is null)
                {
                    generation = EnsureGenerationUnderGate();
                    ThrowTerminal(generation);
                    if (generation.Tools is { } tools) return tools;
                    attempt = RequireAttemptUnderGate(generation, watch: false);
                }
                else { generation = null!; attempt = null!; }
            }
            if (closing is not null)
            {
                await TaskCancellation.WaitAsync(closing, caller).ConfigureAwait(false);
                return await LoadForCallerAsync(context, caller, admission).ConfigureAwait(false);
            }
            var loaded = await AwaitAttemptAsync(generation, attempt, caller).ConfigureAwait(false);
            lock (_gate) RequireConsumer(context, admission);
            return loaded;
        }

        public ValueTask RemoveConsumerAsync(ToolProviderContext context, CancellationToken cancellationToken = default)
        {
            Guard.NotNull(context, nameof(context)); RefuseCallbackReentry(); cancellationToken.ThrowIfCancellationRequested();
            SourceGeneration? closing;
            Task[] subscriptions;
            var start = false;
            lock (_gate)
            {
                _consumers.Remove(context.ConsumerId);
                var owned = (_current ?? _closing)?.Subscriptions.Where(value => value.ConsumerId == context.ConsumerId).ToArray()
                    ?? Array.Empty<Subscription>();
                subscriptions = owned.Select(value => value.CloseAsync()).ToArray();
                if (_consumers.Count != 0) return new(Task.WhenAll(subscriptions));
                closing = _closing;
                if (closing is null && _current is { } generation)
                {
                    closing = _closing = generation;
                    _current = null;
                    generation.Closing = true;
                    start = true;
                }
            }
            if (start) _ = CompleteGenerationCloseAsync(closing!);
            return closing is null ? default : new(closing.Closed.Task);
        }

        private SourceGeneration EnsureGenerationUnderGate()
        {
            if (_current is { } current) return current;
            var generation = _current = new();
            _ = Task.Run(() => RunSourceAsync(generation));
            return generation;
        }

        private LoadAttempt RequireAttemptUnderGate(SourceGeneration generation, bool watch)
        {
            var attempt = watch ? generation.WatchedReady : generation.Loading;
            if (attempt is null || attempt.Completion.Task.IsFaulted)
            {
                attempt = new();
                generation.Attempts.Add(attempt);
                if (watch) generation.WatchedReady = attempt;
                else generation.Loading = attempt;
                RequestCaptureUnderGate(generation);
            }
            return attempt;
        }

        private async Task<IReadOnlyList<ITool>> AwaitAttemptAsync(SourceGeneration generation, LoadAttempt attempt, CancellationToken caller)
        {
            try
            {
                await TaskCancellation.WaitAsync(attempt.Completion.Task, caller).ConfigureAwait(false);
                return await attempt.Completion.Task.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (_gate) if (ReferenceEquals(error, attempt.Failure)) attempt.FailureObserved = true;
                throw;
            }
        }

        private async Task RunSourceAsync(SourceGeneration generation)
        {
            Exception? cleanup = null;
            try
            {
                while (true)
                {
                    await generation.Signal.WaitAsync(generation.Lifetime.Token).ConfigureAwait(false);
                    long sequence;
                    lock (_gate)
                    {
                        if (generation.Closing || generation.Terminal is not null) return;
                        sequence = generation.Sequence;
                    }
                    try
                    {
                        using (var scope = new CallbackScope(this))
                        {
                            if (generation.WatchRequested && generation.Watcher is null) ArmWatcher(generation);
                            CaptureAndPublish(generation, sequence);
                        }
                    }
                    catch (Exception error)
                    {
                        if (generation.Lifetime.IsCancellationRequested && error is OperationCanceledException canceled && canceled.CancellationToken == generation.Lifetime.Token) return;
                        bool hasTools;
                        lock (_gate)
                        {
                            FailAttempt(generation.Loading, error);
                            FailAttempt(generation.WatchedReady, error);
                            hasTools = generation.Tools is not null;
                        }
                        if (hasTools) Warn(error, "A managed tool generation could not be loaded; retaining the accepted tools.");
                    }
                }
            }
            catch (OperationCanceledException error) when (generation.Lifetime.IsCancellationRequested && error.CancellationToken == generation.Lifetime.Token) { }
            catch (Exception error) { cleanup = error; }
            finally
            {
                try { using var scope = new CallbackScope(this); DetachWatcher(generation); }
                catch (Exception error) { AppendFailure(ref cleanup, error); }
                Exception? terminal;
                lock (_gate) terminal = generation.Terminal;
                if (terminal is not null && cleanup is not null) { Exception? primary = terminal; AppendFailure(ref primary, cleanup); }
                lock (_gate)
                {
                    if (terminal is not null)
                    {
                        FailAttempt(generation.Loading, terminal);
                        FailAttempt(generation.WatchedReady, terminal);
                        foreach (var subscription in generation.Subscriptions) subscription.TerminateUnderGate(terminal);
                    }
                    else if (generation.Closing)
                    {
                        var canceled = new OperationCanceledException(generation.Lifetime.Token);
                        FailAttempt(generation.Loading, canceled);
                        FailAttempt(generation.WatchedReady, canceled);
                    }
                }
                if (cleanup is null) generation.Worker.TrySetResult(true);
                else generation.Worker.TrySetException(cleanup);
            }
        }

        private void CaptureAndPublish(SourceGeneration generation, long sequence)
        {
            var capture = _loader.CaptureGeneration(_references);
            if (_loader.CaptureGeneration(_references).Fingerprint != capture.Fingerprint) { Recapture(generation); return; }
            IReadOnlyList<ITool>? tools;
            lock (_gate)
            {
                if (!CanPublish(generation, sequence)) return;
                tools = generation.Fingerprint == capture.Fingerprint ? generation.Tools : null;
            }
            tools ??= ReadTools(capture);
            if (_loader.CaptureGeneration(_references).Fingerprint != capture.Fingerprint) { Recapture(generation); return; }
            lock (_gate)
            {
                if (!CanPublish(generation, sequence)) return;
                if (generation.Fingerprint != capture.Fingerprint || generation.Tools is null)
                {
                    generation.Tools = tools;
                    generation.Fingerprint = capture.Fingerprint;
                    generation.Version++;
                    foreach (var subscription in generation.Subscriptions) subscription.EnqueueUnderGate(tools, generation.Version);
                }
                generation.Loading?.Completion.TrySetResult(tools);
                if (generation.Watcher is not null) generation.WatchedReady?.Completion.TrySetResult(tools);
            }
        }

        private IReadOnlyList<ITool> ReadTools(GenerationCapture capture)
        {
            using var synchronous = new SynchronousCallbackScope(this);
            var tools = new List<ITool>();
            foreach (var reference in _references)
            {
                var value = _loader.LoadGeneration(reference, capture);
                if (value is ITool tool) tools.Add(tool);
                else if (value is IReadOnlyList<ITool> list)
                {
                    var copy = list.ToArray();
                    if (copy.Any(item => item is null)) throw new InvalidOperationException("A managed tool export contained null.");
                    tools.AddRange(copy);
                }
                else throw new InvalidOperationException("A managed tool export must be ITool or IReadOnlyList<ITool>.");
            }
            return Array.AsReadOnly(tools.ToArray());
        }

        private void ArmWatcher(SourceGeneration generation)
        {
            using var synchronous = new SynchronousCallbackScope(this);
            var watcher = _watcherFactory() ?? throw new InvalidOperationException("The managed watcher factory returned null.");
            try
            {
                _loader.ResolveDeclaredPath(".");
                watcher.Path = _loader.Directory;
                watcher.Filter = "*";
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes;
                watcher.Changed += (_, value) => Changed(generation, value.FullPath);
                watcher.Created += (_, value) => Changed(generation, value.FullPath);
                watcher.Deleted += (_, value) => Changed(generation, value.FullPath);
                watcher.Renamed += (_, value) => { Changed(generation, value.OldFullPath); Changed(generation, value.FullPath); };
                watcher.Error += (_, value) => MonitoringFailed(generation, value.GetException());
                generation.Watcher = watcher;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception primary)
            {
                generation.Watcher = null;
                try { watcher.Dispose(); } catch (Exception error) { Exception? failure = primary; AppendFailure(ref failure, error); }
                throw;
            }
        }

        private void Changed(SourceGeneration generation, string path)
        {
            if (!Relevant(path)) return;
            lock (_gate) if (!generation.Closing && generation.Terminal is null) RequestCaptureUnderGate(generation);
        }

        private bool Relevant(string path)
        {
            path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var input in _inputs)
            {
                if (OperatingPathComparer.Equals(input, path) || input.StartsWith(path + Path.DirectorySeparatorChar, comparison)) return true;
                if (!path.StartsWith(input + Path.DirectorySeparatorChar, comparison)) continue;
                var parts = path.Substring(input.Length + 1).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                if (!parts.Any(part => part is ".git" or ".venv" or "node_modules")) return true;
            }
            return false;
        }

        private void MonitoringFailed(SourceGeneration generation, Exception error)
        {
            lock (_gate)
            {
                if (generation.Closing || generation.Terminal is not null) return;
                generation.Terminal = error;
                RequestCaptureUnderGate(generation);
            }
        }

        private void Recapture(SourceGeneration generation)
        {
            lock (_gate) if (!generation.Closing && generation.Terminal is null) RequestCaptureUnderGate(generation);
        }

        private static void RequestCaptureUnderGate(SourceGeneration generation)
        {
            generation.Sequence++;
            if (generation.Signal.CurrentCount == 0) generation.Signal.Release();
        }

        private static bool CanPublish(SourceGeneration generation, long sequence)
            => !generation.Closing && generation.Terminal is null && generation.Sequence == sequence;

        private static void FailAttempt(LoadAttempt? attempt, Exception error)
        {
            if (attempt is null || attempt.Completion.Task.IsCompleted) return;
            attempt.Failure = error;
            attempt.Completion.TrySetException(error);
        }

        private void DetachWatcher(SourceGeneration generation)
        {
            using var synchronous = new SynchronousCallbackScope(this);
            var watcher = generation.Watcher;
            generation.Watcher = null;
            watcher?.Dispose();
        }

        private async Task CompleteGenerationCloseAsync(SourceGeneration generation)
        {
            using var scope = new CallbackScope(this);
            Exception? failure = null;
            var cleanup = new List<Exception>();
            try
            {
                try { using var synchronous = new SynchronousCallbackScope(this); generation.Lifetime.Cancel(); } catch (Exception error) { cleanup.Add(error); }
                Task[] subscriptions;
                lock (_gate) subscriptions = generation.Subscriptions.ToArray().Select(subscription => subscription.CloseAsync()).ToArray();
                try { await generation.Worker.Task.ConfigureAwait(false); } catch (Exception error) { cleanup.Add(error); }
                foreach (var subscription in subscriptions)
                    try { await subscription.ConfigureAwait(false); } catch (Exception error) { cleanup.Add(error); }
                Exception[] unobserved;
                lock (_gate) unobserved = generation.Attempts.Where(attempt => !attempt.FailureObserved && attempt.Failure is not null)
                    .Select(attempt => attempt.Failure!).ToArray();
                foreach (var error in unobserved)
                    if (!(error is OperationCanceledException canceled && canceled.CancellationToken == generation.Lifetime.Token)) AppendFailure(ref failure, error);
            }
            catch (Exception error) { AppendFailure(ref failure, error); }
            finally
            {
                try { generation.Signal.Dispose(); } catch (Exception error) { AppendFailure(ref failure, error); }
                try { generation.Lifetime.Dispose(); } catch (Exception error) { AppendFailure(ref failure, error); }
                foreach (var error in cleanup) AppendFailure(ref failure, error);
                lock (_gate) if (ReferenceEquals(_closing, generation)) _closing = null;
                if (failure is null) generation.Closed.TrySetResult(true);
                else generation.Closed.TrySetException(failure);
            }
        }

        private object RequireConsumer(ToolProviderContext context, object? admission = null)
        {
            if (!_consumers.TryGetValue(context.ConsumerId, out var current) || admission is not null && !ReferenceEquals(current, admission))
                throw new InvalidOperationException("The managed tool provider consumer has not been admitted.");
            return current;
        }

        private static void ThrowTerminal(SourceGeneration generation)
        {
            if (generation.Terminal is { } error) ExceptionDispatchInfo.Capture(error).Throw();
        }

        private void RefuseCallbackReentry()
        {
            for (var scope = ActiveSynchronousCallback; scope is not null; scope = scope.Previous)
                if (ReferenceEquals(scope.Owner, this)) throw new InvalidOperationException("A managed tool provider callback cannot await its own lifecycle.");
            for (var scope = ActiveCallback.Value; scope is not null; scope = scope.Previous)
                if (scope.Active && ReferenceEquals(scope.Owner, this)) throw new InvalidOperationException("A managed tool provider callback cannot await its own lifecycle.");
        }

        private void Warn(Exception error, string message)
        {
            using var scope = new CallbackScope(this);
            using var synchronous = new SynchronousCallbackScope(this);
            HarnessLogging.Warning(error, message);
        }

        private static void AppendFailure(ref Exception? primary, Exception error)
        {
            if (primary is null) { primary = error; return; }
            if (ReferenceEquals(primary, error)) return;
            var errors = primary.Data["Strands.CleanupErrors"] is IEnumerable<Exception> existing ? existing.ToList() : [];
            if (!errors.Any(existing => ReferenceEquals(existing, error))) errors.Add(error);
            primary.Data["Strands.CleanupErrors"] = errors;
        }

        private static Task<T> PreserveFailure<T>(Task<T> operation)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Complete();
            return completion.Task;
            async Task Complete()
            {
                try { completion.TrySetResult(await operation.ConfigureAwait(false)); }
                catch (Exception error) { completion.TrySetException(error); }
            }
        }

        private sealed class CallbackScope : IDisposable
        {
            internal DeclaredToolProvider Owner { get; }
            internal CallbackScope? Previous { get; }
            internal bool Active = true;
            internal CallbackScope(DeclaredToolProvider owner) { Owner = owner; Previous = ActiveCallback.Value; ActiveCallback.Value = this; }
            public void Dispose() { Active = false; ActiveCallback.Value = Previous; }
        }

        // Synchronous user code can restore an expired ExecutionContext over the current AsyncLocal.
        // This owner chain exists only around synchronous segments and must never cross an await.
        private sealed class SynchronousCallbackScope : IDisposable
        {
            internal DeclaredToolProvider Owner { get; }
            internal SynchronousCallbackScope? Previous { get; }
            internal SynchronousCallbackScope(DeclaredToolProvider owner)
            { Owner = owner; Previous = ActiveSynchronousCallback; ActiveSynchronousCallback = this; }
            public void Dispose() => ActiveSynchronousCallback = Previous;
        }

        private sealed class LoadAttempt
        {
            internal TaskCompletionSource<IReadOnlyList<ITool>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Exception? Failure;
            internal bool FailureObserved;
        }

        private sealed class SourceGeneration
        {
            internal CancellationTokenSource Lifetime { get; } = new();
            internal SemaphoreSlim Signal { get; } = new(0, 1);
            internal TaskCompletionSource<bool> Worker { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal List<LoadAttempt> Attempts { get; } = [];
            internal List<Subscription> Subscriptions { get; } = [];
            internal FileSystemWatcher? Watcher;
            internal IReadOnlyList<ITool>? Tools;
            internal string? Fingerprint;
            internal long Sequence;
            internal long Version;
            internal bool WatchRequested;
            internal bool Closing;
            internal Exception? Terminal;
            internal LoadAttempt? Loading;
            internal LoadAttempt? WatchedReady;
        }
    }
}
