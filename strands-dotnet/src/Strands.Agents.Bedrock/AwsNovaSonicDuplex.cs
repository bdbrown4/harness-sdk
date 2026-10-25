#if NET8_0_OR_GREATER
using System.Runtime.CompilerServices;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Strands.Agents.Bidi;

namespace Strands.Agents.Bedrock;

internal sealed class AwsNovaSonicDuplex(IAmazonBedrockRuntime client, string modelId, bool ownsClient) : INovaSonicDuplex
{
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private CancellationToken _token;
    private Task? _opening, _stopping;
    private Task<IInvokeModelWithBidirectionalStreamInputEvent>? _publishing;
    private InvokeModelWithBidirectionalStreamResponse? _response;

    public Task OpenAsync(Func<CancellationToken, Task<byte[]?>> readInput, CancellationToken cancellationToken)
    {
        if (_opening is not null || _stopping is not null) throw new InvalidOperationException("The AWS duplex connection has already been used.");
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _token = _lifetime.Token;
        return _opening = OpenCoreAsync(readInput);
    }

    private async Task OpenCoreAsync(Func<CancellationToken, Task<byte[]?>> readInput)
    {
        _response = await client.InvokeModelWithBidirectionalStreamAsync(new()
        {
            ModelId = modelId,
            BodyPublisher = () =>
            {
                var publishing = PublishAsync(readInput);
                lock (_gate) _publishing = publishing;
                return publishing;
            }
        }, _token).ConfigureAwait(false);
        if (_response.Body is null) throw new ModelException("Nova Sonic returned no output stream.");
    }

    private async Task<IInvokeModelWithBidirectionalStreamInputEvent> PublishAsync(Func<CancellationToken, Task<byte[]?>> readInput)
    {
        try
        {
            var bytes = await readInput(_token).ConfigureAwait(false);
            return bytes is null ? null! : new BidirectionalInputPayloadPart { Bytes = new MemoryStream(bytes, writable: false) };
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { return null!; }
    }

    public async IAsyncEnumerable<byte[]> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = _response ?? throw new InvalidOperationException("Open the AWS duplex connection before reading.");
        var iterator = response.Body.GetAsyncEnumerator(cancellationToken);
        Exception? primary = null;
        try
        {
            while (true)
            {
                bool moved;
                try { moved = await iterator.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception error) { primary = Translate(error, cancellationToken); throw primary; }
                if (!moved) break;
                if (iterator.Current is not BidirectionalOutputPayloadPart chunk || chunk.Bytes is null || chunk.Bytes.Length > 1048576)
                {
                    primary = new ModelException("Nova Sonic returned an invalid output chunk or exceeded the 1 MiB event limit.");
                    throw primary;
                }
                yield return chunk.Bytes.ToArray();
            }
        }
        finally
        {
            try { await iterator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { if (primary is null) throw; AddCleanup(primary, error); }
        }
    }

    private static Exception Translate(Exception error, CancellationToken token)
    {
        if (error is ModelTimeoutException || error.InnerException is ModelTimeoutException)
        {
            var timeout = new BidiConnectionTimeoutException("Nova Sonic reached its connection deadline.");
            timeout.Data["Strands.ProviderError"] = error;
            return timeout;
        }
        if (token.IsCancellationRequested && error is BedrockRuntimeEventStreamException { InnerException: OperationCanceledException })
            return new OperationCanceledException("Nova Sonic output was canceled.", error, token);
        return error;
    }

    public Task StopAsync()
    {
        TaskCompletionSource<bool>? owner = null;
        lock (_gate)
        {
            if (_stopping is null) { owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _stopping = owner.Task; }
        }
        if (owner is not null) _ = StopCoreAsync(owner);
        return _stopping;
    }

    private async Task StopCoreAsync(TaskCompletionSource<bool> completion)
    {
        Exception? primary = null;
        try
        {
            try { _lifetime?.Cancel(); } catch (Exception error) { Add(error); }
            try { Interlocked.Exchange(ref _response, null)?.Dispose(); } catch (Exception error) { Add(error); }
            if (_opening is not null) try { await _opening.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
                catch (Exception error) { Add(error); }
            // An open operation may have accepted a response while cancellation was in flight.
            try { Interlocked.Exchange(ref _response, null)?.Dispose(); } catch (Exception error) { Add(error); }
            Task? publishing; lock (_gate) publishing = _publishing;
            if (publishing is not null) try { await publishing.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
                catch (Exception error) { Add(error); }
            if (ownsClient) try { client.Dispose(); } catch (Exception error) { Add(error); }
            _lifetime?.Dispose();
            if (primary is null) completion.TrySetResult(true); else completion.TrySetException(primary);
        }
        catch (Exception error) { completion.TrySetException(error); }
        void Add(Exception error) { if (primary is null) primary = error; else AddCleanup(primary, error); }
    }

    private static void AddCleanup(Exception primary, Exception cleanup)
    {
        if (ReferenceEquals(primary, cleanup)) return;
        if (primary.Data["Strands.CleanupErrors"] is not List<Exception> errors) primary.Data["Strands.CleanupErrors"] = errors = [];
        errors.Add(cleanup);
    }
}
#endif
