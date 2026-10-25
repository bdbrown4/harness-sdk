using System.Text.Json;

namespace Strands.Agents.MultiAgent;

internal interface INodeCheckpointValidator
{
    IReadOnlyList<Interrupt> ReadPendingInterrupts(JsonElement snapshot);
    void ValidateResponses(JsonElement snapshot, IReadOnlyList<InterruptResponse> responses);
}
