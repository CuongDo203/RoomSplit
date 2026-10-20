# RoomSplit

RoomSplit is a backend API for sharing expenses between roommates.

One member pays a bill (electricity, water, internet, food, …). The cost is split among some or all members of the room, either equally or with custom amounts. RoomSplit keeps track of who owes whom and records repayments between members.

## Features

**Available now:**

- Rooms: create, list, update, delete.
- Members: add a member to a room, update a member, mark a member as having left the room (their history is kept).
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
| Naming | EFCore.NamingConventions: snake_case tables and columns |
| API docs | Microsoft.AspNetCore.OpenApi + Swagger UI |

> **Important:** EF Core must stay on **9.0.x**. Pomelo 9.0.0 has no build for EF Core 10.
> Don't upgrade `Microsoft.EntityFrameworkCore.*` packages to 10.x. Also avoid packages that pull in EF Core 10 as a dependency, such as Identity 10.x or the EF Core InMemory or Sqlite 10.x providers. If one of these is added, `dotnet restore` fails with NU1608 or NU1107.

## Project structure

```
RoomSplit/
├── RoomSplit.slnx
├── Directory.Build.props     Shared build settings (target framework, nullable, warnings)
├── Directory.Packages.props  All NuGet package versions (Central Package Management)
├── docker-compose.yml        Local MySQL for development
├── .github/workflows/ci.yml  CI: format check, Release build, tests
└── RoomSplit.Api/
    ├── Common/        Shared helpers (ApiRoutes, IAppClock)
    ├── Controllers/   Thin HTTP layer: calls services, returns results
    ├── Data/          AppDbContext (Fluent API configuration, seed data)
    ├── Dtos/          Request/response records (entities are never exposed)
    ├── Entities/      Domain entities and enums
    ├── Exceptions/    App exceptions and the handler that maps them to ProblemDetails
    ├── Migrations/    EF Core migrations
    └── Services/      Business logic and data access, one folder per feature
        ├── Categories/
        ├── Members/
        └── Rooms/
```

Requests flow from the controller to the service to `AppDbContext`. Services report expected errors by throwing:

- `NotFoundException` → 404
- `ConflictException` → 409
- `BusinessRuleException` → 400

`AppExceptionHandler` turns these into [ProblemDetails](https://www.rfc-editor.org/rfc/rfc9457) responses. It also maps MySQL foreign-key and duplicate-key violations (errors 1451, 1452, 1062) to 409, as a safety net for concurrent requests.

## Getting started

### 1. Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- MySQL 8.x running locally, or reachable over the network. The MySQL user needs permission to create the database. If you have Docker, you can start one with the bundled compose file:

  ```bash
  cp .env.example .env      # then set MYSQL_ROOT_PASSWORD
  docker compose up -d
  ```

  If a local MySQL already uses port 3306, set `MYSQL_PORT=3307` in `.env` and use `Port=3307` in the connection string.
- The EF Core CLI, **version 9**:

  ```bash
  dotnet tool install --global dotnet-ef --version "9.0.*"
  ```

  If you already have a different version, run `dotnet tool update --global dotnet-ef --version "9.0.*"`.

### 2. Clone and restore

```bash
git clone <repository-url>
cd RoomSplit
dotnet restore
```

### 3. Configure the database connection

`appsettings.Development.json` is git-ignored, so each developer has their own copy. Create yours from the template:

```bash
cd RoomSplit.Api
cp appsettings.Development.example.json appsettings.Development.json
# Windows PowerShell: Copy-Item appsettings.Development.example.json appsettings.Development.json
```

Then provide your MySQL password in one of two ways.

**Option A (recommended): User Secrets.** The password is stored outside the repository.

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Port=3306;Database=roomsplit;User=root;Password=<your-password>;"
```

**Option B: edit the file directly.** Put the password in `appsettings.Development.json`. The file is git-ignored, so the password won't be committed.

In other environments, set the environment variable `ConnectionStrings__DefaultConnection` instead.

### 4. Create the database

Run this from `RoomSplit.Api/`:

```bash
dotnet ef database update
```

This creates the `roomsplit` database, all tables, and the seed categories.

### 5. Run the API

```bash
dotnet run --launch-profile https
```

Then open Swagger UI at **https://localhost:7054/swagger**.

Health checks: `GET /health/live` (the process is up) and `GET /health/ready` (the database is reachable).

Swagger UI and the OpenAPI document (`/openapi/v1.json`) are only available in the `Development` environment. Sample requests are in [`RoomSplit.Api/RoomSplit.Api.http`](RoomSplit.Api/RoomSplit.Api.http), which you can run from Visual Studio, Rider or the VS Code REST Client.

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
| GET | `/api/v1/rooms/{roomId}/members?status=Active` | List a room's members. The `status` filter is optional |
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
