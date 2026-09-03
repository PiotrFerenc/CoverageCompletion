# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CoverageCompletion — a .NET 10 console tool that, given the path to a target `.sln`,
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

All projects (`src/` and `tests/`) target `net10.0`, matching this sandbox's only
natively-installed SDK, so `dotnet build`/`dotnet run`/`dotnet test` all work with no
extra env vars needed to launch the tool itself. Target solutions the tool analyzes
may still be on an older TFM (e.g. `net8.0`) that this sandbox lacks a matching runtime
for — `ProcessRunner` (in `CoverageCompletion.Infrastructure`) sets
`DOTNET_ROLL_FORWARD=LatestMajor` on every `git`/`dotnet` child process it spawns so
`dotnet build`/`dotnet test` against those target solutions still launch regardless of
the host's exact runtimes. No manual roll-forward setup is required anywhere anymore.

Assertions use [Shouldly](https://github.com/shouldly/shouldly) (`X.ShouldBe(Y)` style) — chosen
specifically to avoid FluentAssertions' 8.x paid-commercial-license requirement.

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
- `Git/BranchMerger` — after the session, merges the `coverage/session-*` branch into a fresh
  `coverage/merged-<timestamp>-<random>` branch cut from the branch the session started on (via
  its own temporary worktree, never touching the caller's actual working tree). On conflict, that
  worktree is left in place (not cleaned up) for the user to resolve by hand; the CLI logs which
  outcome happened either way.
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
hardcoded template. Generated tests use xUnit + Shouldly + NSubstitute, with
integration-style tests using in-memory/mocked dependencies rather than Testcontainers.
