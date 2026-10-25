namespace Strands.Agents.ConsumerFixture;

public sealed record ConsumerHookEvent(Agent Agent, string Value) : HookEvent(Agent);

public static class ConsumerEventCopies
{
    public static HookEvent Copy(HookEvent value) => value with { };
    public static ConsumerHookEvent CopyTyped(ConsumerHookEvent value) => value with { Value = "copied" };
}
