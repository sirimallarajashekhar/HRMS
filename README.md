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
dotnet run --project src/Host/Hrms.Api

# 3. Check it
curl http://localhost:5000/health/ready     # -> Healthy once MySQL, Redis and RabbitMQ answer
curl http://localhost:5000/api/v1/ping
```

(Use the URL printed by `dotnet run`; adjust the ports above if you changed them in `.env`. On Windows PowerShell use `$env:ConnectionStrings__Mysql = "..."`.)

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

## Build and test

```bash
dotnet build Hrms.sln
dotnet test Hrms.sln          # integration tests start a MySQL container via Testcontainers, so Docker must be running
```

## CI/CD

`.github/workflows/ci.yml` runs on every pull request: build (warnings are errors), unit + architecture + integration tests, NuGet vulnerability scan, secret scan (gitleaks) and Docker image builds. `.github/workflows/cd.yml` is a skeleton that publishes images to GHCR and promotes dev → staging → prod; the deploy steps are placeholders and `prod` must be protected with required reviewers in the GitHub environment settings.
