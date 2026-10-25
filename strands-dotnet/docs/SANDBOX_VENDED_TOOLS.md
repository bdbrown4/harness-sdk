# Sandbox-vended tools

`Sandbox.GetTools()` is a virtual, default-empty capability. Agent initialization
loads provider tools first, prepares selected sandbox tools, then initializes
plugins. Existing registered names win. Each included tool's specification,
filter, schema and applicable policy are prepared before any sandbox tool is
published. Callbacks run outside the Agent initialization lock; caller
cancellation is checked after tool enumeration, each specification and filter.
Initialization failure removes the sandbox tools while preserving original user
tools and the primary failure.

Children resolve tools from their own sandbox. Inherited sandbox-origin tools
are omitted from application-tool copying, while an explicit successful user
mutation of the same object makes that tool application-owned and inheritable.
Explicitly bound standalone tools retain their selected sandbox. Sandboxes and
tools are borrowed; vending does not transfer disposal ownership.

Docker and SSH vend `sandbox_file_editor`, then `sandbox_shell`, bound to that
exact environment. The default local environment vends no tools. Construction
and metadata inspection execute no process or remote request. There is no new
Harness default-tool member or sandbox backend selector.

This increment follows the pinned TS/Python sandbox tool registration and
Docker/SSH vending contracts. Its component tests use deterministic fake
backends or native metadata inspection. Actual remote transport and executable
proof require a separately authorized runner and are not implied by these
tests, package assets, or tool names.
