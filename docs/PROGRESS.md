# HRMS – Progress log

Append one entry per finished step: date, step id, what was built, follow-ups.

## 2026-11-25 – P0.1 Solution skeleton
**Built:** Hrms.sln (.NET 10, central package management, warnings as errors); hosts Hrms.Api (Serilog JSON, OpenTelemetry, /health/live + /health/ready, built-in OpenAPI per API version via Asp.Versioning.OpenApi, Scalar in Development, problem details with stable `code` + `traceId`, correlation id, /api/v1 versioning, sample `GET /api/v1/ping`) and Hrms.Worker (Serilog, OpenTelemetry, heartbeat placeholder); SharedKernel (`Result`/`Error`); Contracts; empty Platform projects (Workflow, Policy, Notifications); Core module template (Domain/Application/Infrastructure/Api). 31 tests: unit, integration (WebApplicationFactory), architecture (modules discovered by naming convention; boundary rule proven by a failing fixture).
**Decisions:** built-in OpenAPI (not Swashbuckle); FluentAssertions pinned to 7.2.2 (v8 needs a commercial license); Microsoft.* packages pinned to 10.0.x (NuGet "latest" is 11.0 and does not run on net10.0); OpenAPI/Scalar not served in Production until P0.4; `InvariantGlobalization` deliberately NOT set (multi-country formatting); no FSD existed, built from the playbook prompt.
**Convention learned:** NetArchTest matches namespaces by prefix, so layer sub-namespaces must be nested (`Hrms.X.Domain.Events`), never siblings like `Hrms.X.DomainEvents`.
**Follow-ups:**
- P0.2: real `ready` health checks (MySQL, Redis, RabbitMQ); restrict `AllowedHosts` per environment; Dockerfiles; CI supply-chain items (nuget.config source mapping, lock files, vulnerability scan); decide on an extra analyzer pack (Meziantou/Roslynator).
- P0.3: tenant id in the log scope; `ErrorType` on `Error` plus `Result` to problem-details mapping once real endpoints exist; move `Result` tests into a SharedKernel test project when the kernel grows.
- P0.4: deny-by-default fallback authorization policy; revisit OpenAPI exposure; `/ping` is anonymous on purpose until then.
- Low: log route template instead of raw path (PII risk once routes carry identifiers); quiet request logging for health probes; bootstrap logger around host startup; xunit 2.9.3 is marked Legacy in favour of xunit.v3.
