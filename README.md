# HRMS

Multi-tenant HRMS (ASP.NET Core / .NET 10, MySQL 8.4, Angular, .NET MAUI). Specs live in `docs/`, working agreements in `CLAUDE.md`, the step-by-step build in `hrms_playbook.md`.

## Run locally in 5 minutes

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download), [Docker Desktop](https://www.docker.com/products/docker-desktop/) (running), Git.

```bash
git clone https://github.com/sirimallarajashekhar/HRMS.git && cd HRMS

# 1. Local infrastructure: MySQL, Redis, RabbitMQ, S3-compatible object store, Seq (logs)
cp .env.example .env            # dev-only passwords; edit host ports here if something already uses them
docker compose up -d --wait     # returns when every service is healthy

# 2. Run the API on your machine (hot reload friendly)
export ConnectionStrings__Mysql="Server=localhost;Port=3306;Database=hrms;User ID=hrms;Password=dev-hrms-password"
export ConnectionStrings__Redis="localhost:6379,password=dev-redis-password"
export ConnectionStrings__RabbitMq="amqp://hrms:dev-rabbit-password@localhost:5672"
export Seq__ServerUrl="http://localhost:5341"

# A field-encryption key for local runs, kept in your user secrets and never in the repo (do this once):
dotnet user-secrets set --project src/Host/Hrms.Api "Encryption:CurrentKeyId" dev
dotnet user-secrets set --project src/Host/Hrms.Api "Encryption:Keys:0:Id" dev
dotnet user-secrets set --project src/Host/Hrms.Api "Encryption:Keys:0:Key" "$(openssl rand -base64 32)"

dotnet run --project src/Host/Hrms.Api

# 3. Check it
curl http://localhost:5000/health/ready     # -> Healthy once MySQL, Redis and RabbitMQ answer (port 5000, Development: see launchSettings.json)
curl http://localhost:5000/api/v1/ping
```

`dotnet run` uses the Development profile from `Properties/launchSettings.json` (`http://localhost:5000`). If you edited passwords or ports in `.env`, use the same values in the exports above. On Windows PowerShell use `$env:ConnectionStrings__Mysql = "..."`. All published ports bind to `127.0.0.1` only.

Or run the API and worker in containers too: `docker compose --profile app up -d --build` (API on `http://localhost:8080`).

| Service | URL / port (defaults) |
|---|---|
| API health | `/health/live` (process only), `/health/ready` (checks MySQL, Redis, RabbitMQ) |
| API docs (Development) | `/scalar`, OpenAPI JSON at `/openapi/v1.json` |
| Seq (logs) | http://localhost:8081 |
| RabbitMQ UI | http://localhost:15672 (`hrms` / password from `.env`) |
| Object store console | http://localhost:9001 (keys from `.env`) |
| MySQL | `localhost:3306`, database `hrms` |

Stop with `docker compose down` (add `-v` to also delete data).

## Database migrations and the first tenant

Run these against your local database before using anything that reads or writes data. **`Hrms.Audit` goes first:** every save of an audited record also writes to `audit_log`, so without that table those saves fail. In a deployment, apply it before the new code is released.

```bash
dotnet tool restore
export HRMS_MIGRATIONS_CONNECTION="$ConnectionStrings__Mysql"

dotnet ef database update --project src/Platform/Hrms.Audit --startup-project src/Host/Hrms.Api --context AuditDbContext
for module in Core Identity Employee Workflow; do
  dotnet ef database update --project src/Modules/$module/Infrastructure --startup-project src/Host/Hrms.Api --context ${module}DbContext
done

export HRMS_BOOTSTRAP_ADMIN_PASSWORD="<choose one>"
dotnet run --project src/Host/Hrms.Api -- bootstrap-tenant --code ACME --name "Acme Ltd" --admin-email admin@acme.example --admin-name "Acme Admin"
```

`bootstrap-tenant` is safe to repeat. **Run it again for each existing tenant after an upgrade:** it adds permissions that are new in that version (for example `audit.log.view`) to the default roles.

## Audit log

Changes to people, organisation, access and workflow records are written to `audit_log` in the same transaction as the change, with sign-ins, wrong credentials and lockouts. Read one record's history with `GET /api/v1/audit/{entityType}/{recordId}?from&to&page&pageSize` (for example `employee` or `user_account`). The caller needs `audit.log.view` plus the permission and data scope that viewing that data needs. A client can say why it changes something with the `X-Change-Reason` header. The reason is stored on every entry the request causes, so clients must not put personal data in it; in employee history it is returned only to callers who may also see that employee's personal data. Nothing is deleted automatically.

**Partitions.** The table has one partition per month. The worker keeps the next 12 months in place (`Audit:Partitions:MonthsAhead`); if it cannot, writes still succeed and, once the months run out, collect in the catch-all partition `p_future`. The worker logs an error each time it fails. Its `ALTER` is cancelled after `Audit:Partitions:AlterTimeoutSeconds` (default 120).

**If rows reach the catch-all, someone has to act.** Splitting it then copies those rows, and audited saves (sign-ins included) can be blocked until the copy finishes. The worker does not do this itself: it changes nothing and logs an error starting `ACTION NEEDED`, once an hour, **so alert on it.** The error contains the `ALTER TABLE` statement. Run it in a maintenance window from a client with no command timeout, after trying it on a copy to see how long it takes. If the error says no month is missing, look for a server with a wrong clock.

**Production database users (to be set up by whoever runs the database).** The application refuses to update or delete audit rows, but only the database can stop everything else from doing so:

- Give the API and worker user only `SELECT` and `INSERT` on `audit_log`. MySQL cannot take a table privilege away from a database-wide grant, so this means granting that user's privileges table by table.
- Give the worker a second user that may read and alter `audit_log`, as `ConnectionStrings__MysqlMaintenance`. MySQL's `ALTER TABLE` needs more than `ALTER` (see its documentation for partition changes), so confirm the grants in a staging environment. Without this setting the worker uses its ordinary connection.

The local Docker setup uses one user with full rights on the database, so none of this is needed there.

## Build and test

```bash
dotnet build Hrms.sln
dotnet test Hrms.sln          # integration tests start a MySQL container via Testcontainers, so Docker must be running
```

## CI/CD

`.github/workflows/ci.yml` runs on every pull request: build (warnings are errors), unit + architecture + integration tests, NuGet vulnerability scan, secret scan (gitleaks) and Docker image builds. `.github/workflows/cd.yml` is a skeleton that publishes images to GHCR and promotes dev → staging → prod; the deploy steps are placeholders and `prod` must be protected with required reviewers in the GitHub environment settings.

### One-time GitHub setup (repository owner)

1. **Branch protection on `main`:** require a pull request, 1 review and the `build` status check (CI job names are stable: `build`, `dependency-scan`, `secret-scan`, `docker-images`).
2. **Environments** (Settings → Environments): create `dev`, `staging` and `prod`. Add **required reviewers** to `prod` (and preferably `staging`); without them the CD workflow deploys unattended. Create `DEPLOY_TARGET` and `DEPLOY_TOKEN` as *environment* secrets, not repository secrets.
3. Allow GitHub Actions to create and publish packages (GHCR) for this repository.

### Deploying

Every deployed container must set **`AllowedHosts`** (for example `AllowedHosts=hrms.example.com`); the default is `localhost`, so without it the API answers 400 to every request, including health probes. In any environment other than Development, `/health/ready` reports unhealthy unless the MySQL, Redis and RabbitMQ connection strings are configured. `/openapi` and `/scalar` are not served in Production.
