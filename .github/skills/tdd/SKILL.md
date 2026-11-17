---
name: tdd
description: Run an automated Red-Green-Refactor TDD loop against an intent/*.md file by delegating each phase to the tdd-tester, tdd-implementer and tdd-refactorer custom agents.
argument-hint: "intent/<feature-name>.md"
disable-model-invocation: true
---

# TDD Orchestrator

Drive a strict Red-Green-Refactor loop for the referenced `intent/*.md` file. Delegate each phase to its custom agent as a subagent, using your subagent delegation tool (`task`). After each phase, verify it with the guard script before moving on.

## Setup
1. Read the referenced `intent/*.md` file. If none was provided, ask which one to use.
2. Confirm its status is `Accepted` and `Open Questions` is empty. If not, stop and tell the user.
3. Run `git status --porcelain` and confirm the tree is clean. If not, stop and ask the user to commit or stash.
4. Break `Proposed Outcome` into small, vertical behavioural slices.

## Loop (repeat for each slice)
For each phase below, first record the current commit with `git rev-parse HEAD` (the base). Delegate to the agent. Then run:

```text
dotnet run .github/hooks/TddGuard.cs -- verify <role> <base>
```

The command prints JSON and exits non-zero when `ok` is false. On failure, delegate to the same agent again with the `violations` text and ask it to fix them. Allow two retries, then stop and ask the user. Report any `warnings` to the user.

1. **Red:** delegate to `tdd-tester` with the path to `intent/*.md` and the next slice. Verify with role `tdd-tester`. This checks that exactly one test was added, that it fails, and that only test files changed.
2. **Green:** delegate to `tdd-implementer` with the path to `intent/*.md` and the failing test details. Verify with role `tdd-implementer`. This checks that `dotnet test` passes and that no test file changed.
3. **Refactor:** delegate to `tdd-refactorer` with the path to `intent/*.md` and the changed files. Verify with role `tdd-refactorer`. This checks that `dotnet test` stays green and that no test file changed.

## Rules
* Never write or edit code yourself. Delegate every file edit to the specialist agents.
* Never push. All commits stay local to the current branch.
* Stop on ambiguity. If an agent reports that `intent/*.md` is unclear or has an unresolved open question, pause and ask the user.
* After each full cycle, summarise the commits created and ask whether to continue with the next slice.
