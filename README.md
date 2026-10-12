# Database-Driven Scope Authorization in ASP.NET Core

This sample demonstrates database-driven, scope-based authorization with JWT bearer tokens in an ASP.NET Core Minimal API.

The sample stores both the scopes granted to each user and the scopes required by each endpoint in SQL Server. A custom `AuthorizationHandler` compares the scopes in the authenticated user's token with the scopes configured for the selected endpoint.

## How it works

1. `POST /api/login` validates the submitted credentials against the `Users` table.
2. A successful login returns a JWT containing the user's scopes in an `scp` claim.
3. Protected endpoints call `RequireScope()`, which adds a custom authorization requirement.
4. `ScopeAuthorizationHandler` identifies the endpoint by its name and loads its required scopes from `FeatureScopes`.
5. Authorization succeeds when the token contains at least one of the required scopes.

The handler also recognizes the Microsoft identity platform scope claim URI (`http://schemas.microsoft.com/identity/claims/scope`).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- SQL Server LocalDB, or another SQL Server instance

## Configuration

The default settings in `ApplicationScopeSample/appsettings.json` use SQL Server LocalDB:

```json
{
  "ConnectionStrings": {
    "SqlConnection": "Server=(localdb)\\MSSQLLocalDB;Database=MinimalDB;Trusted_Connection=True;"
  }
}
```

Update `ConnectionStrings:SqlConnection` when using another SQL Server instance. The application applies the included Entity Framework Core migrations automatically during startup.

JWT issuer, audience, signing key, and token lifetime are configured under `Authentication:JwtBearer`. The checked-in signing key is intended only for this sample. Use a secret store or environment-specific configuration for real applications.

## Run the application

From the repository root, run:

```powershell
dotnet run --project .\ApplicationScopeSample\ApplicationScopeSample.csproj --launch-profile https
```

The default endpoints are:

- Swagger UI: <https://localhost:7252/swagger>
- OpenAPI document: <https://localhost:7252/openapi/v1.json>
- API base URL: <https://localhost:7252/api>

## Add sample data

The project intentionally does not seed credentials or authorization rules. After the first startup creates the database, add a user and the endpoint scope mappings with SQL Server Management Studio, or `sqlcmd`:

```sql
USE MinimalDB;

INSERT INTO Users (Email, Password, Scopes)
VALUES ('alice@example.com', 'sample-password', '["people:read","people:write"]');

INSERT INTO FeatureScopes (Name, Scopes)
VALUES
    ('People_GetAll', '["people:read"]'),
    ('People_Get', '["people:read"]'),
    ('People_Insert', '["people:write"]'),
    ('People_Update', '["people:write"]'),
    ('People_Delete', '["people:write"]');
```

`FeatureScopes.Name` must match the endpoint name assigned by `WithName(...)`. A protected endpoint with no matching row, or with an empty scope list, is denied by the custom requirement.

Passwords are stored and compared as plain text to keep the authorization example focused. This is not suitable for production; passwords must be salted and hashed with a dedicated password hasher.

## Try the API

Request a token with the sample credentials:

```powershell
$login = Invoke-RestMethod `
    -Method Post `
    -Uri https://localhost:7252/api/login `
    -ContentType 'application/json' `
    -Body '{"email":"alice@example.com","password":"sample-password"}'

$token = $login.token
```

Call a protected endpoint:

```powershell
Invoke-RestMethod `
    -Method Get `
    -Uri https://localhost:7252/api/people `
    -Headers @{ Authorization = "Bearer $token" }
```

The protected API surface is:

| Method | Route | Endpoint name | Example scope |
| --- | --- | --- | --- |
| `GET` | `/api/people` | `People_GetAll` | `people.read` |
| `GET` | `/api/people/{id}` | `People_Get` | `people.read` |
| `POST` | `/api/people` | `People_Insert` | `people.write` |
| `PUT` | `/api/people/{id}` | `People_Update` | `people.write` |
| `DELETE` | `/api/people/{id}` | `People_Delete` | `people.write` |

The sample endpoints return empty `200 OK` responses; their purpose is to demonstrate authorization rather than CRUD behavior. `GET /debug/routes` lists the discovered route names, patterns, and HTTP methods and is intended for local diagnostics only.

## Response behavior

- Missing or invalid bearer tokens produce `401 Unauthorized`.
- Authenticated users without a matching required scope produce `403 Forbidden`.
- Exception and status-code middleware use Problem Details responses where supported by the configured authentication handler and middleware pipeline.

## Key components

- `Program.cs`: application setup, endpoints, custom requirement, and authorization handler
- `Data/ApplicationDbContext.cs`: EF Core model configuration
- `Data/Entities/User.cs`: credentials and granted scopes
- `Data/Entities/FeatureScope.cs`: endpoint names and required scopes
- `Data/Migrations`: SQL Server database schema

## License

This project is licensed under the terms in [LICENSE.txt](LICENSE.txt).
