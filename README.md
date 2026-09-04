# CoverageCompletion

.NET 10 console tool that auto-completes unit test coverage for a target
`.sln` using an LLM.

Point it at a solution and it will:

1. Measure code coverage and find every gap (uncovered method).
2. Split the gaps across several parallel worktrees (bounded, default 4) so
   independent gaps build and test concurrently instead of one at a time.
3. For each gap, generate a matching unit test with an LLM (OpenAI),
   copying the assertion style from an existing test in the same solution.
4. Build and run the generated test.
5. On failure, feed the build/test error back to the LLM and retry (up to
   5 attempts), then skip and record why if it still doesn't pass.
6. Commit each successfully-passing test file.
7. Merge every worktree's commits, in order, into one new branch cut from
   the branch the run started on, and write a Markdown summary of what was
   completed/skipped.

All of this happens inside dedicated `git worktree`s that the tool creates
and removes itself — it never touches the caller's working tree directly.

## Requirements

- .NET 10 SDK
- `git`
- An `OPENAI_API_KEY` (target solutions using an SDK the sandbox lacks a
  matching runtime for, e.g. `net8.0`, are still handled automatically via
  a forced `DOTNET_ROLL_FORWARD`)

## Usage

```bash
OPENAI_API_KEY=sk-... dotnet run --project src/CoverageCompletion.Cli -- /path/to/target/solution.sln
```

Optionally set `OPENAI_MODEL` (default `gpt-4.1`) to pick a different model.

## Target solution conventions

Generated tests assume the target solution uses:

- [Mediator](https://github.com/martinothamar/Mediator) (source-generated
  `IRequestHandler<TRequest, TResponse>`)
- [FluentResults](https://github.com/altmann/FluentResults)
  (`Result`/`Result<T>`)
- xUnit + Shouldly + NSubstitute for tests, with integration-style
  tests using in-memory/mocked dependencies rather than Testcontainers

`TestPatternFinder` looks for a real, existing test in the target solution
to copy the assertion style from, rather than relying on a hardcoded
template.

## Architecture

```
src/
  CoverageCompletion.Contracts/       # shared DTOs + interfaces
  CoverageCompletion.Infrastructure/  # git, coverage analysis, build/test, reporting
  CoverageCompletion.Generation/      # LLM-driven test generation
  CoverageCompletion.Cli/             # DI composition root + orchestration loop
tests/
  CoverageCompletion.Infrastructure.Tests/
  CoverageCompletion.Generation.Tests/
  CoverageCompletion.Cli.Tests/
  CoverageCompletion.EndToEnd.Tests/
```

- **Contracts** defines every cross-module type and interface
  (`CoverageGap`, `WorktreeSession`, `IWorktreeManager`, `ICoverageAnalyzer`,
  `IBuildTestRunner`, `IGitCommitter`, `IBranchMerger`, `ITestGenerator`,
  `ISummaryReporter`) — the only coordination point between Infrastructure
  and Generation.
- **Infrastructure** shells out to `git`/`dotnet`: creates/removes worktrees,
  parses Cobertura coverage XML into gaps, builds/tests, commits, merges
  every lane's branch into one new branch, and writes the summary report.
- **Generation** is everything LLM-facing: finding a style pattern in the
  target solution, building the prompt, calling OpenAI's official `ChatClient`
  SDK, and retrying with the previous error on failure.
- **Cli** is the only project that depends on both Infrastructure and
  Generation. It wires up DI and runs the main loop: create a worktree →
  analyze coverage → create more worktrees (one per parallel lane) → split
  gaps across lanes → per gap: generate → write → build → test → retry or
  skip → commit → merge every lane's branch → write summary → remove every
  worktree.

## Development

```bash
dotnet build
dotnet test tests/CoverageCompletion.Infrastructure.Tests
dotnet test tests/CoverageCompletion.Generation.Tests
dotnet test tests/CoverageCompletion.Cli.Tests
```

Assertions use [Shouldly](https://github.com/shouldly/shouldly) — chosen specifically to avoid
FluentAssertions' 8.x paid-commercial-license requirement.
