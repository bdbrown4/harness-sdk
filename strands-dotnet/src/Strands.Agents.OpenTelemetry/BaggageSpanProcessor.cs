using System.Diagnostics;
using OpenTelemetry;

namespace Strands.Agents.Telemetry;

internal sealed class BaggageSpanProcessor(Func<string, bool>? predicate) : BaseProcessor<Activity>
{
    public override void OnStart(Activity data)
    {
        foreach (var entry in Baggage.GetBaggage())
        {
            try
            {
                if (predicate is null || predicate(entry.Key)) data.SetTag(entry.Key, entry.Value);
            }
            catch (Exception) { /* A user baggage predicate cannot fail the application operation. */ }
        }
    }
}
