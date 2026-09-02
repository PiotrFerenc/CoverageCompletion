# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CoverageCompletion — a .NET 8 console tool that, given the path to a target `.sln`,
measures code coverage, generates missing unit tests via an LLM (OpenAI), builds and
runs them, retries with the build/test error fed back to the LLM on failure, and
commits each successfully-passing test file. It operates on the target solution
inside a dedicated `git worktree` that it creates and removes itself — it never
touches the caller's working tree directly.

The full requirements/design conversation and the two-track implementation plan live
in `/home/piotr/.claude/plans/mighty-orbiting-parasol.md`.

## Commands

```bash
# Build everything
dotnet build

# Run all tests for one project
dotnet test tests/CoverageCompletion.Infrastructure.Tests
dotnet test tests/CoverageCompletion.Generation.Tests

# Run a single test
dotnet test tests/CoverageCompletion.Infrastructure.Tests --filter "FullyQualifiedName~CoberturaCoverageParserTests"

# Run the tool itself
OPENAI_API_KEY=sk-... dotnet run --project src/CoverageCompletion.Cli -- /path/to/target/solution.sln
```

`OPENAI_API_KEY` (and optionally `OPENAI_MODEL`, default `gpt-4.1`) must be set in the
environment before running the Cli — never hardcode it or pass it via a config file.

**Runtime note for this sandbox specifically**: only .NET runtimes 6/7/10 are installed
here, not 8.0.x, even though all projects target `net8.0`. `dotnet build` works fine
(SDK 10 builds net8.0 targets natively), but `dotnet run`/`dotnet test` need a roll-forward
to launch:
```bash
DOTNET_ROLL_FORWARD=LatestMajor dotnet test tests/CoverageCompletion.Infrastructure.Tests
```
`tests/CoverageCompletion.Infrastructure.Tests.csproj` already sets `<RollForward>Major</RollForward>`
in its own `.csproj` for this reason; other projects don't have it, so pass the env var
when running them directly in this environment. This is a quirk of this particular
sandbox's dotnet package setup, not something the app depends on.

FluentAssertions is pinned to `7.x` in both test projects — 8.x requires a paid commercial
license.

## Architecture

The solution is deliberately split into a shared contract package plus two independent
tracks, so two agents/developers can implement them in parallel without touching the same
files:

```
src/
  CoverageCompletion.Contracts/       # shared DTOs + interfaces — the ONLY coordination
                                       # point between the two tracks below
  CoverageCompletion.Infrastructure/  # Track A: git, coverage, build/test, reporting
  CoverageCompletion.Generation/      # Track B: LLM-driven test generation
  CoverageCompletion.Cli/             # orchestrator: DI composition root + main loop,
                                       # depends on both tracks only through Contracts
tests/
  CoverageCompletion.Infrastructure.Tests/
  CoverageCompletion.Generation.Tests/
```

**`CoverageCompletion.Contracts`** (`CoverageContracts.cs`) defines every cross-track type:
`CoverageGap`, `WorktreeSession`, `BuildTestResult`, `GeneratedTest`, and the interfaces
`IWorktreeManager`, `ICoverageAnalyzer`, `IBuildTestRunner`, `IGitCommitter`, `ITestGenerator`,
`ISummaryReporter`. Changing this file affects both tracks — treat it as a stable contract,
not an implementation detail.

**`CoverageCompletion.Infrastructure`** (Track A) — everything that shells out to `git`/`dotnet`
via `Process`:
- `Git/WorktreeManager` — creates `coverage/session-<timestamp>-<random>` worktrees, removes them.
- `Git/GitCommitter` — stages + commits a single file, returns the commit SHA.
- `Coverage/CoberturaCoverageParser` — pure XML→`CoverageGap` parsing (no I/O, easy to unit test
  in isolation), `Coverage/CoverageAnalyzer` drives `dotnet test --collect:"XPlat Code Coverage"`
  and feeds its output through the parser.
- `Build/BuildTestRunner` — `dotnet build` / `dotnet test --filter`, captures combined output.
- `Reporting/SummaryReporter` — accumulates completed/skipped gaps, writes a Markdown summary.

**`CoverageCompletion.Generation`** (Track B) — everything LLM-facing:
- `TestPatternFinder` — finds an existing test in the target solution to copy the assertion
  style from (naming convention first, then a loose content heuristic for Mediator handler /
  FluentResults-style tests as a fallback).
- `PromptBuilder` — pure string-building for the initial prompt and the retry-with-error prompt.
- `OpenAiClient` — thin `HttpClient` wrapper around the OpenAI Chat Completions API (no SDK
  dependency, so tests substitute `HttpMessageHandler` instead of hitting the network).
- `TestGenerator : ITestGenerator` — wires the three together.

**`CoverageCompletion.Cli`** (`Program.cs`) is the only place that knows about both tracks. It
builds a `ServiceCollection`, registers every implementation under its `Contracts` interface,
then runs the orchestration loop: create worktree → analyze coverage → for each gap, generate →
write file → build → test → on failure regenerate with the error fed back (up to 5 attempts,
then skip and record why) → on success commit → write the summary file → remove the worktree.

The target solution being tested is expected to use Mediator (source-generated
`IRequestHandler<TRequest, TResponse>`) and FluentResults (`Result`/`Result<T>`) — the
generated tests should match that solution's existing assertion style, which is why
`TestPatternFinder` looks for a real example in the target repo rather than using a
hardcoded template. Generated tests use xUnit + FluentAssertions + NSubstitute, with
integration-style tests using in-memory/mocked dependencies rather than Testcontainers.
