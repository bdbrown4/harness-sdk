# Classic tool-pair fallback boundaries

`SlidingWindowConversationManagerOptions.ToolPairBoundaryPolicy` selects only the fallback used when there is no plain user trim point at or after the window target. `Python` remains the default and retains the first complete assistant tool-use/user tool-result pair. `TypeScript` also retains the most recent plain user message before that pair.

```csharp
var manager = new Strands.Agents.Conversation.SlidingWindowConversationManager(new()
{
    WindowSize = 2,
    ToolPairBoundaryPolicy = Strands.Agents.Conversation.ClassicToolPairBoundaryPolicy.TypeScript
});
```

For `[user0, assistant1, user2, assistantTool3, userResult4]`, the Python policy retains `[assistantTool3, userResult4]`. The TypeScript policy retains `[user2, assistantTool3, userResult4]`, even though this exceeds the window target. The entire tail from the selected pair remains ordered.

When protected messages precede the pair, they supply the anchor instead. That protected prefix must begin with a plain user message, alternate roles, and end with a user message without tool use. Existing adjacent tool-pair pin protection applies. The reducer declines an invalid prefix, a missing anchor, or a fallback that would delete no messages. History, metadata, tracking identifiers, removal counters, and first-reduction pin state are unchanged on a declined fallback.

Native explicit reduction reports a declined TypeScript fallback by rethrowing the supplied overflow exception, or by throwing `InvalidOperationException` when there is no supplied error. The pinned TypeScript private helper returns `false` instead. If no complete fallback pair exists at all, native retains its prior ordinary no-progress behavior and rethrows a supplied overflow. This option does not change that existing lifecycle behavior.

The option is independent of `ToolResultTruncationPolicy`: selecting a TypeScript anchor does not select TypeScript rich-content truncation. The default Python lifecycle, per-turn cadence, proactive threshold, snapshot/state schema, zero-window behavior, ordinary user trim points, and child manager isolation remain unchanged. Default children inherit the selected boundary policy through a fresh manager instance.

Source: pinned `3bd98e618320f0f286acbfb2252f72388030421b`, TypeScript `src/conversation-manager/sliding-window-conversation-manager.ts:200–280,465–540`; Python `src/strands/agent/conversation_manager/sliding_window_conversation_manager.py` fallback remains assistant based. This is a fallback policy slice, not full classic conversation-manager union parity.
