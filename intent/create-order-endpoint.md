# Intent: Create Order Minimal API Endpoint
**Author:** Your Name  
**Status:** Accepted

## Problem
Clients currently have no HTTP endpoint to submit a new customer order, requiring manual database inserts during onboarding.

## Proposed Outcome
Expose `POST /api/orders` on the Orders Minimal API. Valid payloads create a pending order and return `201 Created` with a `Location` header and order summary. Invalid payloads return `400 Bad Request` using RFC 9457 ProblemDetails.

## Affected Users & Systems
* **Users:** External API consumers and checkout frontend.
* **Systems:** `Orders.Api` Minimal API host and `Orders.Api.Tests` integration suite.

## Constraints & Guardrails
* **Must have:** Idempotency check via `Idempotency-Key` header; duplicate keys return `200 OK` with the original order without creating a second record.
* **Must have:** Native .NET 10 `TypedResults` return types for accurate OpenAPI schema generation.
* **Must not touch:** Existing `/api/products` endpoints, authentication middleware configuration in `Program.cs`, or EF Core migration history.

## Open Questions
* None — ready for Red-Green-Refactor slices.
