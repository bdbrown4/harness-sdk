using System.Text;

namespace Strands.Agents.Bidi;

public static partial class BidiConsole
{
    private sealed class ConsoleOutput(TextWriter writer, BidiConsoleOutputOptions options) : IBidiOutputStream
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<(string Label, string Id), StringBuilder> _pending = [];
        // Final block events repeat recently rendered deltas; arbitrary old replays need no eternal tombstone.
        private readonly LinkedList<(string Label, string Id)> _completed = [];
        private bool _started;

        public async ValueTask StartAsync(BidiAgent agent, CancellationToken cancellationToken = default)
        {
            if (agent is null) throw new ArgumentNullException(nameof(agent));
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_started) throw new InvalidOperationException("Console output is already in use by a run.");
                _pending.Clear(); _completed.Clear(); _started = true;
            }
            finally { _gate.Release(); }
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var line in _pending) await WriteLineAsync(line.Key.Label, line.Value.ToString()).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            finally { _pending.Clear(); _completed.Clear(); _started = false; _gate.Release(); }
        }

        public async ValueTask WriteAsync(IAgentStreamEvent output, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_started) throw new InvalidOperationException("Start console output before writing events.");
                switch (output)
                {
                    case BidiTextStartEvent value when options.ShowText: Begin("assistant", value.ContentId); break;
                    case BidiTextDeltaEvent value when options.ShowText: Append("assistant", value.ContentId, value.Delta); break;
                    case BidiTextStopEvent value when options.ShowText: await EndAsync("assistant", value.ContentId).ConfigureAwait(false); break;
                    case BidiTextBlockEvent value when options.ShowText: await CompleteAsync("assistant", value.ContentId, value.Text).ConfigureAwait(false); break;
                    case BidiTranscriptStartEvent value when options.ShowTranscripts: Begin(TranscriptLabel(value.Role), value.ContentId); break;
                    case BidiTranscriptDeltaEvent value when options.ShowTranscripts: Append(TranscriptLabel(value.Role), value.ContentId, value.Delta); break;
                    case BidiTranscriptStopEvent value when options.ShowTranscripts: await EndAsync(TranscriptLabel(value.Role), value.ContentId).ConfigureAwait(false); break;
                    case BidiTranscriptBlockEvent value when options.ShowTranscripts: await CompleteAsync(TranscriptLabel(value.Role), value.ContentId, value.Transcript).ConfigureAwait(false); break;
                    case BidiReasoningStartEvent value when options.ShowReasoning: Begin("reasoning", value.ContentId); break;
                    case BidiReasoningDeltaEvent value when options.ShowReasoning: Append("reasoning", value.ContentId, value.Delta); break;
                    case BidiReasoningStopEvent value when options.ShowReasoning: await EndAsync("reasoning", value.ContentId).ConfigureAwait(false); break;
                    case BidiReasoningBlockEvent value when options.ShowReasoning: await CompleteAsync("reasoning", value.ContentId, value.Text).ConfigureAwait(false); break;
                    case BidiToolUseBlocksEvent value when options.ShowTools:
                        await WriteLineAsync("tools", string.Join(", ", value.ToolUses.Select(call => call.Name))).ConfigureAwait(false);
                        break;
                }
            }
            finally { _gate.Release(); }
        }

        private void Begin(string label, string id) { _completed.Remove((label, id)); _pending[(label, id)] = new(); }
        private void Append(string label, string id, string delta)
        {
            if (!_pending.TryGetValue((label, id), out var line)) _pending[(label, id)] = line = new();
            line.Append(delta);
        }
        private async Task EndAsync(string label, string id)
        {
            if (!_pending.TryGetValue((label, id), out var line)) return;
            await CompleteAsync(label, id, line.ToString()).ConfigureAwait(false);
        }
        private async Task CompleteAsync(string label, string id, string text)
        {
            if (_completed.Contains((label, id))) return;
            await WriteLineAsync(label, text).ConfigureAwait(false);
            _pending.Remove((label, id)); _completed.AddLast((label, id));
            if (_completed.Count > 128) _completed.RemoveFirst();
        }
        private async Task WriteLineAsync(string label, string text)
        {
            if (text.Length == 0) return;
            await writer.WriteLineAsync(label + ": " + text).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        private static string TranscriptLabel(Role role) => (role == Role.User ? "user" : "assistant") + " (transcript)";
    }
}
