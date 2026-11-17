# .NET 10 Minimal API Engineering Standards

## Technology & Language
* Target **.NET 10** (`net10.0`) with C# 14 features, nullable reference types enabled, and file-scoped namespaces.
* Prefer primary constructors for dependency injection in services and endpoint handlers.

## Minimal API Conventions
* **Typed Results Only:** Always return `TypedResults.*` (e.g., `Results<Ok<OrderResponse>, Created<OrderResponse>, ValidationProblem, NotFound>`) rather than untyped `Results.*` so OpenAPI metadata stays accurate without extra attributes.
* **Route Organisation:** Group related endpoints using `app.MapGroup("/api/...")` and extract complex lambdas into static handler methods.
* **Error Handling:** Return RFC 9457 `ProblemDetails` or `HttpValidationProblemDetails` for all `4xx`/`5xx` error responses.
* **Validation:** Use .NET 10's built-in Minimal API validation (`AddValidation()`) or explicit guard clauses returning `TypedResults.ValidationProblem(...)`.
* **Request/Response Contracts:** Use immutable `record` types for DTOs. Never expose EF Core entities directly over HTTP.

## Testing Standards
* Write behavioural endpoint tests using **xUnit** (or **TUnit**) with `WebApplicationFactory<Program>` and `HttpClient`.
* Make `Program` accessible to test projects via `public partial class Program { }` at the bottom of `Program.cs`.
* Assert on HTTP status codes, headers, and deserialised response bodies rather than mocking internal repositories where an in-memory or Testcontainers database can be used.

## Feature Workflow (Mandatory)
* **Features with an `intent/*.md` file go through `/tdd`.** Do not implement them directly. If the user asks you to implement one, ask them to run `/tdd intent/<feature-name>.md`, or delegate Red, Green and Refactor to the `tdd-tester`, `tdd-implementer` and `tdd-refactorer` agents in that order.
* Do not start work on an intent whose status is not `Accepted`.

## Git Atomic Commit Rule (Mandatory)
* **Every agent must commit its work locally before finishing its turn.** Never leave uncommitted changes in the working tree when handing off to the next phase.
* **Never push remotely or run destructive git commands:** `git push`, `git reset --hard`, `git clean -fd`, `git checkout -- .`, and `git branch -D` are strictly forbidden and blocked by hooks. Humans control all pushes and merges.
* Use clear Conventional Commits matching your role:
  * `intent-author`: `docs(intent): add <feature-name> specification`
  * `tdd-tester`: `test(red): add failing test for <slice>`
  * `tdd-implementer`: `feat(green): implement <slice>`
  * `tdd-refactorer`: `refactor: <summary of improvement>`
