# RoomSplit

RoomSplit is a backend API for sharing expenses between roommates.

One member pays a bill (electricity, water, internet, food, …). The cost is split among some or all members of the room, either equally or with custom amounts. RoomSplit keeps track of who owes whom and records repayments between members.

## Features

**Available now:**

- Rooms: create, list, update, delete.
- Members: add a member to a room, update a member, mark a member as having left the room (their history is kept). A room's member list is cached in Redis for 60 seconds and invalidated whenever a member of the room changes.
- Expense categories: Electricity, Water, Internet, Food, Household, Other.

**Planned (MVP):**

- Expenses with `Equal` or `Custom` splits among a subset of members.
- Balances: each member's net position in a room.
- Settlements: member A pays back member B.
- Suggestions for the fewest transfers needed to settle all debts.

**Out of scope for now:** authentication, a shared room fund, recurring expenses, monthly closing, and members belonging to multiple rooms.

## How balances work

For a member M:

```
balance(M) = Σ expenses paid by M
           − Σ M's shares of expenses
           + Σ settlements M paid
           − Σ settlements M received
```

A positive balance means others owe M. A negative balance means M owes others.

*Example:* An pays 300,000 VND for electricity, split equally between An, Binh and Chi.
→ An is +200,000, Binh is −100,000, Chi is −100,000.
Binh then pays An back 100,000 → Binh is 0, An is +100,000.

Business rules:

- The shares of an expense must add up to the expense amount.
- The payer and all members sharing an expense must belong to the expense's room.
- In an `Equal` split, any remainder goes to the payer's share. For example, 100,000 / 3 gives the payer 33,334 and the others 33,333.
- Members who have left can't be added to new expenses.
- A settlement needs two different members of the same room.

## Tech stack

| Area | Technology |
|---|---|
| Framework | ASP.NET Core Web API (.NET 10), controller-based |
| ORM | Entity Framework Core **9.0.x** |
| Database | MySQL 8.x via Pomelo.EntityFrameworkCore.MySql 9.0.0 |
| Cache | Redis 8.x via `IDistributedCache` (Microsoft.Extensions.Caching.StackExchangeRedis) |
| Naming | EFCore.NamingConventions: snake_case tables and columns |
| API docs | Microsoft.AspNetCore.OpenApi + Swagger UI |
| Containers | Docker, docker compose (MySQL, Redis, API) |

> **Important:** EF Core must stay on **9.0.x**. Pomelo 9.0.0 has no build for EF Core 10.
> Don't upgrade `Microsoft.EntityFrameworkCore.*` packages to 10.x. Also avoid packages that pull in EF Core 10 as a dependency, such as Identity 10.x or the EF Core InMemory or Sqlite 10.x providers. If one of these is added, `dotnet restore` fails with NU1608 or NU1107.

## Project structure

```
RoomSplit/
├── RoomSplit.slnx
├── Directory.Build.props     Shared build settings (target framework, nullable, warnings)
├── Directory.Packages.props  All NuGet package versions (Central Package Management)
├── docker-compose.yml        Shared stack: MySQL + Redis + API
├── docker-compose.override.yml  Dev settings (applied automatically)
├── docker-compose.prod.yml   Staging/production settings
├── .env.example              Template for .env (dev compose variables)
├── envs/                     *.env.example templates for staging and production
├── Dockerfile                Multi-stage image for the API (see docs/docker.md)
├── .dockerignore             Keeps build output, .git and secrets out of the build context
├── .github/workflows/ci.yml  CI: format check, Release build, tests
├── scripts/                  measure-members-cache.ps1 (response times with/without cache)
├── docs/                     Design notes and learning material (Vietnamese)
└── RoomSplit.Api/
    ├── Common/        Shared helpers (ApiRoutes, IAppClock, CacheOptions)
    ├── Controllers/   Thin HTTP layer: calls services, returns results
    ├── Data/          AppDbContext (Fluent API configuration, seed data), DevDataSeeder
    ├── Dtos/          Request/response records (entities are never exposed)
    ├── Entities/      Domain entities and enums
    ├── Exceptions/    App exceptions and the handler that maps them to ProblemDetails
    ├── Migrations/    EF Core migrations
    └── Services/      Business logic and data access, one folder per feature
        ├── Categories/
        ├── Members/   (also IMemberCache: cached member lists)
        └── Rooms/
```

Requests flow from the controller to the service to `AppDbContext`. Services report expected errors by throwing:

- `NotFoundException` → 404
- `ConflictException` → 409
- `BusinessRuleException` → 400

`AppExceptionHandler` turns these into [ProblemDetails](https://www.rfc-editor.org/rfc/rfc9457) responses. It also maps MySQL foreign-key and duplicate-key violations (errors 1451, 1452, 1062) to 409, as a safety net for concurrent requests.

## Containers

The dev stack (`docker compose up`) runs three containers on one Docker network. Containers reach each other by **service name** (`mysql`, `redis`) through Docker's internal DNS; the host reaches them through published ports.

```
                               Host machine
   Browser / curl / Swagger UI       MySQL client            redis-cli
              │                           │                      │
      localhost:API_PORT          localhost:MYSQL_PORT    localhost:REDIS_PORT
           (8080)                     (dev only)              (dev only)
              │                           │                      │
 ┌────────────┼───────── Docker network <project>_default ───────┼────────────┐
 │            ▼                           ▼                      ▼            │
 │     ┌──────────────┐  mysql:3306  ┌──────────────┐   ┌──────────────┐      │
 │     │ api          │─────────────►│ mysql        │   │ redis        │      │
 │     │ .NET 10      │              │ 8.0.46       │   │ 8.2.10       │      │
 │     │ port 8080    │─────┐        │ port 3306    │   │ port 6379    │      │
 │     └──────────────┘     │        └──────┬───────┘   └──────▲───────┘      │
 │                          └───────────────┼──────────────────┘              │
 │                             redis:6379   │                                 │
 └──────────────────────────────────────────┼─────────────────────────────────┘
                                            ▼
                              volume <project>_mysql-data
```

| Service | Dev container name | Image | Port in container | Host port | Starts after | Data |
|---|---|---|---|---|---|---|
| `mysql` | `roomsplit-mysql` | `mysql:8.0.46` | 3306 | `MYSQL_PORT` (dev only) | | Named volume `mysql-data` |
| `redis` | `roomsplit-redis` | `redis:8.2.10-alpine` | 6379 | `REDIS_PORT` (dev only) | | None: cache only, no persistence, 64 MB, LRU eviction |
| `api` | `roomsplit-api` | `roomsplit-api:<tag>`, built from `Dockerfile` | 8080 | `API_PORT` | `mysql` and `redis` healthy | None |

`<project>` is `roomsplit` in dev, `roomsplit-staging` / `roomsplit-prod` for staging and production (`COMPOSE_PROJECT_NAME`). In staging and production, MySQL and Redis have **no** host port and Redis requires a password.

## Getting started

### 1. Software to install

| Software | Version | Needed for | Check |
|---|---|---|---|
| [Git](https://git-scm.com/) | any recent | Cloning the repository | `git --version` |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Windows/macOS) or Docker Engine + Compose plugin (Linux) | Compose v2 | Running MySQL, Redis and the API (option A); MySQL and Redis only (option B) | `docker version`, `docker compose version` |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | 10.0.x | Option B (`dotnet run`), building, migrations | `dotnet --version` |
| EF Core CLI `dotnet-ef` | **9.0.x** | Creating migrations; migration scripts for staging/production | `dotnet ef --version` |
| PowerShell 7 + `curl` | `curl.exe` ships with Windows 10+ | `scripts/measure-members-cache.ps1` | `pwsh --version` |

Install the EF Core CLI (version 9, matching EF Core 9):

```bash
dotnet tool install --global dotnet-ef --version "9.0.*"
# already installed with another version:
dotnet tool update --global dotnet-ef --version "9.0.*"
```

Only Docker is needed to run the app with option A. A local MySQL or Redis installation is **not** needed.

### 2. Clone and create `.env`

```bash
git clone <repository-url>
cd RoomSplit
cp .env.example .env          # Windows PowerShell: Copy-Item .env.example .env
```

Edit `.env` and set at least `MYSQL_ROOT_PASSWORD`. If a port is already used on your machine (for example a local MySQL on 3306), change `MYSQL_PORT`, `REDIS_PORT` or `API_PORT`. See [Environment variables](#environment-variables).

### 3A. Run everything in Docker (recommended)

```bash
docker compose up -d --build
docker compose ps             # mysql and redis "healthy", api "Up"
```

- Swagger UI: **http://localhost:8080/swagger** (port = `API_PORT`).
- Health: `GET /health/live` (process up), `GET /health/ready` (database reachable).
- In `Development` the API applies pending migrations at startup and, on an empty database, adds a sample room (`Database:MigrateOnStartup`, see [`docs/dev-migrate-seed.md`](docs/dev-migrate-seed.md)). No manual `dotnet ef database update` is needed.
- See the member cache at work:

  ```bash
  curl http://localhost:8080/api/v1/rooms/1/members
  docker exec roomsplit-redis redis-cli KEYS 'roomsplit:*'      # roomsplit:members:room:1
  docker exec roomsplit-redis redis-cli TTL roomsplit:members:room:1
  ```

- Logs: `docker compose logs -f api`.
- Stop: `docker compose down` (keeps the database). `docker compose down -v` also **deletes the database volume**.

### 3B. Run the API with `dotnet run`, MySQL and Redis in Docker

```bash
docker compose up -d mysql redis

cd RoomSplit.Api
cp appsettings.Development.example.json appsettings.Development.json
# Windows PowerShell: Copy-Item appsettings.Development.example.json appsettings.Development.json
```

`appsettings.Development.json` is git-ignored, so each developer has their own copy. In it, set the MySQL password, and `Port=` to your `MYSQL_PORT`, in `ConnectionStrings:DefaultConnection`; set the port in `ConnectionStrings:Redis` to your `REDIS_PORT`. Alternatively, keep the password out of the file with User Secrets:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Port=3306;Database=roomsplit;User=root;Password=<your-password>;"
```

Then run:

```bash
dotnet run --launch-profile https
```

Swagger UI: **https://localhost:7054/swagger**. Migrations and sample data are applied at startup, as in option A.

If `ConnectionStrings:Redis` is empty, the API uses an in-process memory cache instead of Redis, so it also runs without Redis.

### 4. Measure the member cache (optional)

With the dev stack from step 3A running:

```powershell
./scripts/measure-members-cache.ps1 -RoomId 1 -Runs 10
```

The script times `GET /api/v1/rooms/{roomId}/members` in three scenarios: no cache (recreates the `api` container with `CACHE_ENABLED=false`), cache miss and cache hit. It prints every run plus min/average/median/max, and leaves the API with the cache on. Results and analysis: [`docs/redis-members-cache.md`](docs/redis-members-cache.md#9-kết-quả-đo).

### 5. Simulate staging and production locally (optional)

Staging and production run the **same image**, built once and tagged with the commit, with different settings from `envs/<environment>.env`:

```bash
cp envs/staging.env.example envs/staging.env          # set API_TAG and passwords
cp envs/production.env.example envs/production.env    # use different passwords

docker build -t roomsplit-api:<tag> .
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file envs/staging.env up -d     # http://localhost:8081
docker compose -f docker-compose.yml -f docker-compose.prod.yml --env-file envs/production.env up -d  # http://localhost:8082
```

Each env file sets `COMPOSE_PROJECT_NAME`, so every environment gets its own containers, network and database volume, and can run alongside dev. These environments do not migrate at startup and MySQL is not reachable from the host, so create the schema with an idempotent script through `docker compose exec`. The steps are in [`docs/docker-multi-env.md`](docs/docker-multi-env.md#4-quy-trình-chạy-staging-và-production).

The image can also be run on its own. It contains no connection string, so pass one as an environment variable:

```bash
docker run --rm -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Server=host.docker.internal;Port=3307;Database=roomsplit;User=root;Password=<password>;" \
  roomsplit-api:dev
```

The container listens on port **8080** (HTTP only), runs as the non-root user `app`, and defaults to the `Production` environment (no Swagger UI). Without `ConnectionStrings__Redis` it uses the in-process cache.

The compose setup is split into three files:

| File | Used by | Contents |
|---|---|---|
| `docker-compose.yml` | all environments | Services, pinned images, volume, healthchecks, startup order |
| `docker-compose.override.yml` | dev (read automatically) | Build from source, `Development`, MySQL and Redis ports on the host, `CACHE_ENABLED` |
| `docker-compose.prod.yml` | staging and production (`-f`) | Prebuilt image, dedicated DB user, Redis password, no MySQL/Redis ports, resource limits, log rotation |

Design notes: [`docs/docker.md`](docs/docker.md), [`docs/docker-compose.md`](docs/docker-compose.md), [`docs/docker-environments.md`](docs/docker-environments.md), [`docs/docker-multi-env.md`](docs/docker-multi-env.md), [`docs/redis-members-cache.md`](docs/redis-members-cache.md). Concepts: [`docs/docker-basics.md`](docs/docker-basics.md), [`docs/docker-volumes-networks.md`](docs/docker-volumes-networks.md), [`docs/redis-basics.md`](docs/redis-basics.md).

## Environment variables

### Compose variables (`.env` for dev, `envs/<environment>.env` for staging/production)

Docker compose reads these to fill in `docker-compose*.yml`. `.env` and `envs/*.env` are git-ignored; the committed templates are `.env.example` and `envs/*.env.example`.

| Variable | Dev (`.env`) | Staging / production | Default | Description |
|---|---|---|---|---|
| `MYSQL_ROOT_PASSWORD` | **required** | **required** | | MySQL root password. In dev the API connects as root with it |
| `MYSQL_PORT` | optional | not used | `3306` | Host port for MySQL |
| `REDIS_PORT` | optional | not used | `6379` | Host port for Redis |
| `API_PORT` | optional | **required** | `8080` (dev) | Host port for the API (staging example `8081`, production `8082`) |
| `CACHE_ENABLED` | optional | not used | `true` | Becomes `Cache__Enabled` in the dev `api` container; `false` disables caching |
| `COMPOSE_PROJECT_NAME` | not set (`roomsplit`) | **required** | | Separates containers, network and volume per environment |
| `ASPNETCORE_ENVIRONMENT` | fixed `Development` | **required** | | `Staging` or `Production` |
| `API_TAG` | optional | **required** | `dev` | Tag of the `roomsplit-api` image to run |
| `MYSQL_USER`, `MYSQL_PASSWORD` | not used | **required** | | Dedicated MySQL user for the API |
| `REDIS_PASSWORD` | not used | **required** | | Redis password (`--requirepass`); also used by the healthcheck |

### Application settings

ASP.NET Core reads these from `appsettings*.json`, User Secrets or environment variables. As an environment variable, `:` becomes `__` (for example `Cache:Enabled` → `Cache__Enabled`). Compose already sets the connection strings for each environment.

| Setting (environment variable) | Default | Description |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | none (**required**) | MySQL connection string, e.g. `Server=mysql;Port=3306;Database=roomsplit;User=root;Password=...;` |
| `ConnectionStrings__Redis` | empty → in-process memory cache | StackExchange.Redis connection string, e.g. `redis:6379,password=...,abortConnect=false,connectTimeout=2000,syncTimeout=1000,asyncTimeout=1000` |
| `Cache__Enabled` | `true` | `false` turns caching off (every request reads MySQL) |
| `Cache__MembersTtlSeconds` | `60` | How long a room's member list stays cached |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development`, `Staging` or `Production` |
| `Database__MigrateOnStartup` | `true` in Development, else `false` | Apply pending migrations at startup |
| `Swagger__Enabled` | `true` in Development and Staging, `false` in Production | Serve Swagger UI and `/openapi/v1.json` |
| `App__TimeZone` | `Asia/Ho_Chi_Minh` | Time zone of the business "today" |

## API overview

All endpoints are under `/api/v1`. The prefix is defined once in `Common/ApiRoutes.cs`.

| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/v1/categories` | List expense categories |
| GET | `/api/v1/rooms` | List rooms, with active member counts |
| GET | `/api/v1/rooms/{id}` | Get a room |
| POST | `/api/v1/rooms` | Create a room |
| PUT | `/api/v1/rooms/{id}` | Update a room |
| DELETE | `/api/v1/rooms/{id}` | Delete a room. Returns 409 if the room has members, expenses or settlements |
| GET | `/api/v1/rooms/{roomId}/members?status=Active` | List a room's members. The `status` filter is optional. Cached for 60 s per room |
| POST | `/api/v1/rooms/{roomId}/members` | Add a member to a room |
| GET | `/api/v1/members/{id}` | Get a member |
| PUT | `/api/v1/members/{id}` | Update a member |
| POST | `/api/v1/members/{id}/leave` | Mark a member as having left the room |
| DELETE | `/api/v1/members/{id}` | Delete a member. Only for members without history (otherwise 409) |

Enums are serialized as strings, for example `"status": "Active"`.

## Development guide

### Changing the database schema

Never edit the database by hand. Change the entities or `AppDbContext`, then run:

```bash
dotnet ef migrations add <MigrationName>
dotnet ef database update
```

Commit the generated files in `Migrations/`.

### Conventions

- **Naming:** C# uses PascalCase. Database tables and columns use snake_case, and table names are plural. This is applied automatically.
- **Entity configuration:** relationships and constraints are configured with the Fluent API in `AppDbContext`, not with data annotations on entities.
- **Money:** `decimal(12,0)`, in VND with no decimal places.
- **Dates:** `DateOnly` for business dates, `DateTime` for timestamps.
- **Delete behavior:** `Restrict` everywhere to preserve history. The one exception is `expense_shares → expenses`, which cascades.
- **Layering:** controllers never use `AppDbContext` directly. All queries and business rules go in `Services/<Feature>/` as an `IXxxService` interface plus an `XxxService` implementation, registered as scoped services in `Program.cs`.
- **Package versions:** set in `Directory.Packages.props` only. Csproj files reference packages without a version.
- **Cancellation:** every controller action takes a `CancellationToken ct` and passes it on. Every service method takes a required `CancellationToken ct` and passes it to every EF call.
- **"Today":** never use `DateTime.Today` or `DateTime.Now` for business dates. Inject `IAppClock` and use `clock.Today`. It uses the time zone in `App:TimeZone` (default `Asia/Ho_Chi_Minh`), whatever the server's own time zone is.
- **Warnings:** Release builds (and CI) treat warnings as errors. Run `dotnet format` before committing.
- **Routes:** never hard-code the `api/...` prefix. Use `ApiRoutes.Base`, for example `[Route($"{ApiRoutes.Base}/rooms")]`.
