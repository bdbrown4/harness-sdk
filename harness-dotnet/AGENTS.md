# .NET harness development

Read the root `AGENTS.md` and `team/TENETS.md`. This package composes `strands-dotnet` and must return its ordinary `Agent`.

- Keep the published README explicit about implemented behavior and remaining Python/TypeScript parity. Never add an option or registered tool that only pretends to provide a feature.
- Preserve tool names and the shared prompt. Public .NET names use PascalCase; provider wire fields retain their required spelling.
- Keep file access behind `ISandbox`. The host implementation is not a security boundary. Do not describe lexical path checks as isolation.
- Add a behavior test before implementing a change, using deterministic models and fake sandboxes. Exercise persistence with temporary directories and clean them up in `finally`.
- Keep cancellation on every asynchronous external boundary. Do not run model-authored code through a process or general-purpose interpreter as a substitute for Monty.
- Core options take precedence over matching harness convenience values. Consumer tools are additive and normalized names must remain unique.
- Run the package test project, both target framework builds, and NuGet packing before reporting completion. Report live-provider validation separately from deterministic fixtures; package publication is outside local validation.

The Python and TypeScript harnesses remain the behavioral references. Their existing differences must be investigated and documented rather than silently copied as an assumed unified contract.
