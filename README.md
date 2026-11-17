# Intent-Driven TDD Workflow (.NET 10 + GitHub Copilot)

This repository uses an **Intent-Driven Test-Driven Development (TDD)** workflow powered by custom GitHub Copilot agents, a `/tdd` skill, and deterministic .NET 10 hooks. It targets the Copilot harness, so it works in VS Code (Copilot session target), GitHub Copilot CLI, and the GitHub Copilot app.

Instead of letting a single AI context write tests and implementation together (which often produces shallow tests that merely mirror the code), every feature is split into isolated phases anchored to a human-approved specification in `intent/`.

---

## How It Works

```text
  [intent-author] ──▶ intent/<feature>.md (Human Review & Approval)
                            │
                            ▼
                 ┌──▶ [tdd-tester]      (Red: writes 1 failing HTTP test)
                 │          │
   /tdd Loop     │          ▼
  (per slice)    │    [tdd-implementer] (Green: writes minimal API code)
                 │          │
                 │          ▼
                 └─── [tdd-refactorer]  (Refactor: cleans up C# idioms)
```

### The Four Agents
* **`intent-author`** — Interviews you (1–2 questions at a time) about desired API behaviour, edge cases, and boundaries, then writes `intent/<feature>.md` without generating premature code.
* **`tdd-tester` (Red)** — Reads the accepted `intent/*.md` file and writes **one** failing behavioural test using `WebApplicationFactory<Program>` and `HttpClient`. Blocked from touching production code.
* **`tdd-implementer` (Green)** — Writes the minimal .NET 10 Minimal API code (`TypedResults`, `ProblemDetails`, records) required to make the test pass. Blocked from touching test files.
* **`tdd-refactorer` (Refactor)** — Refactors the newly written code into clean route groups (`MapGroup`) and static handlers while keeping `dotnet test` green.

---

## Built-In Guardrails & Git Automation

* **File-Level Phase Locks (`.github/hooks/TddGuard.cs`):** Each agent declares a `preToolUse` hook in its own frontmatter. The hook runs the script with .NET 10 single-file execution (`dotnet run`) before every tool call and applies a path allowlist:
  * `tdd-tester` can write only test projects (plus `.sln`/`.slnx` and `Directory.Packages.props`).
  * `tdd-implementer` and `tdd-refactorer` cannot write test files.
  * `intent-author` can write only under `intent/`.
  * No agent can edit `.github/` or `.git/`, so the guards cannot be switched off.
* **Phase Boundary Check on Stop:** Each agent's `agentStop` hook inspects the dirty tree and its latest phase commit against the same allowlist. A violation blocks the agent from finishing and asks it to fix the change. Clean changes that were left uncommitted are committed automatically.
* **Verified Phases (`verify` mode):** After each phase the `/tdd` skill runs `dotnet run .github/hooks/TddGuard.cs -- verify <role> <baseCommit>`. It checks the diff against the allowlist, that Red added exactly one failing test, and that Green and Refactor end with `dotnet test` passing. This needs a solution or project at the repository root that `dotnet test` can find.
* **Mandatory Atomic Commits:** Every agent commits its own completed phase using Conventional Commits (`docs(intent):`, `test(red):`, `feat(green):`, `refactor:`).
* **Remote Push Protection:** `.github/hooks/git-safety.json` applies to every agent. It blocks `git push` and destructive Git commands (`git reset --hard`, `git clean -f`, `git branch -D`), keeping remote publishing under human control.

Hooks cannot parse every shell command, so a determined agent can write files through a shell trick the guard does not recognise. The stop check and `verify` step catch that case by checking the resulting diff.

---

## Quickstart Guide

### 1. Prerequisites
* **.NET 10 SDK** (`net10.0`)
* **VS Code** with GitHub Copilot Chat and the **Copilot** session target selected, or **GitHub Copilot CLI**
* Ensure `Program.cs` exposes `public partial class Program { }` so `WebApplicationFactory<Program>` can host the API in tests

### 2. Step-by-Step Feature Workflow

1. **Create a feature branch:**
   ```bash
   git checkout -b feat/your-feature-name
   ```

2. **Define the intent:**
   * Open Copilot Chat (`Ctrl+Alt+I` / `Cmd+Option+I`) and select **`intent-author`** from the agent dropdown.
   * Describe the endpoint or behaviour you want to build and answer the follow-up questions.
   * Review the generated `intent/<feature-name>.md` file and change `**Status:** Draft` to `**Status:** Accepted`.

3. **Run the automated TDD loop:**
   * In Copilot Chat, run:
     ```text
     /tdd intent/<feature-name>.md
     ```
   * The `tdd` skill cycles through **Red → Green → Refactor** one vertical slice at a time, pausing after each slice for your go-ahead.
   * *(Optional)* To step through manually instead, select `tdd-tester`, `tdd-implementer`, and `tdd-refactorer` from the agent dropdown in turn.

4. **Review & push:**
   ```bash
   git log --oneline main..HEAD
   git push -u origin feat/your-feature-name
   ```

---

## Repository Layout

```text
├── .github/
│   ├── copilot-instructions.md       # Global .NET 10 Minimal API & Git commit rules
│   ├── agents/
│   │   ├── intent-author.agent.md    # Discovery & intent specification agent
│   │   ├── tdd-tester.agent.md       # Red-phase test specialist
│   │   ├── tdd-implementer.agent.md  # Green-phase implementation specialist
│   │   └── tdd-refactorer.agent.md   # Refactor-phase design specialist
│   ├── skills/
│   │   └── tdd/
│   │       └── SKILL.md              # The /tdd orchestrator skill
│   └── hooks/
│       ├── git-safety.json           # Repo-wide preToolUse hook (push and destructive git block)
│       └── TddGuard.cs               # Single-file .NET 10 handler: git-safety, guard, autocommit, verify
├── intent/
│   └── create-order-endpoint.md      # Example accepted intent specification
└── README.md
```