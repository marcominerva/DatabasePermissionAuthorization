# Database-Driven Permission Authorization in ASP.NET Core

This sample demonstrates database-driven, permission-based authorization with JWT bearer tokens in an ASP.NET Core Minimal API.

The sample stores both the permissions granted to each user and the permissions required by each feature in SQL Server. The JWT identifies the authenticated user but does not contain permissions. A claims transformation loads the user's current permissions from the database, and a custom `AuthorizationHandler` compares them with the permissions configured for the feature associated with the selected endpoint.

## How it works

1. `POST /api/login` validates the submitted credentials against the `Users` table.
2. A successful login returns a JWT that identifies the user by email; permissions are deliberately not embedded in the token.
3. After authentication, `PermissionClaimsTransformation` uses the authenticated identity name to load the user from the database.
4. The transformation adds the user's permissions to the current `ClaimsIdentity` as one space-separated `ClaimTypes.UserData` claim.
5. Protected endpoints call `RequirePermission("FeatureName")`, which attaches a `PermissionMetadata` with the feature name and adds a custom authorization requirement.
6. `PermissionAuthorizationHandler` reads the feature name from the endpoint metadata and loads its required permissions from `FeaturePermissions`.
7. Authorization succeeds when the transformed principal has at least one of the required permissions.

Because endpoints are bound to features rather than to individual routes, multiple endpoints can share the same feature name, and therefore the same set of required permissions.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- SQL Server LocalDB, or another SQL Server instance

## Configuration

The default settings in `DatabasePermissionAuthorization/appsettings.json` use SQL Server LocalDB:

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
dotnet run --project .\DatabasePermissionAuthorization\DatabasePermissionAuthorization.csproj --launch-profile https
```

The default endpoints are:

- Swagger UI: <https://localhost:7252/swagger>
- OpenAPI document: <https://localhost:7252/openapi/v1.json>
- API base URL: <https://localhost:7252/api>

## Add sample data

The project intentionally does not seed credentials or authorization rules. After the first startup creates the database, add a user and the feature permission mappings with SQL Server Management Studio, or `sqlcmd`:

```sql
USE MinimalDB;

INSERT INTO Users (Email, Password, [Permissions])
VALUES ('alice@example.com', 'sample-password', '["people:read","people:write"]');

INSERT INTO FeaturePermissions (Name, [Permissions])
VALUES
    ('People_GetAll', '["people:read"]'),
    ('People_Get', '["people:read"]'),
    ('People_Insert', '["people:write"]'),
    ('People_Update', '["people:write"]'),
    ('People_Delete', '["people:write"]');
```

`FeaturePermissions.Name` must match the feature name passed to `RequirePermission(...)`. A protected endpoint whose feature has no matching row, or has an empty permission list, is denied by the custom requirement.

## Loading permissions with `PermissionClaimsTransformation`

`PermissionClaimsTransformation` implements ASP.NET Core's `IClaimsTransformation` interface and is registered as a scoped service:

```csharp
builder.Services.AddScoped<IClaimsTransformation, PermissionClaimsTransformation>();
```

ASP.NET Core invokes the transformation after a request has been authenticated. If the resulting identity is authenticated and has a name, the transformation treats that name as the user's email, retrieves the corresponding `Users` row, and adds the values from `Users.Permissions` to the current identity as a single, space-separated `ClaimTypes.UserData` claim. `PermissionAuthorizationHandler` then reads that claim when evaluating the endpoint requirement.

This keeps authorization data out of the JWT and makes changes to `Users.Permissions` effective on the next authenticated request, without requiring a new token. Changes to `FeaturePermissions` are also evaluated on every protected request. The tradeoff is that the current implementation queries the database during claims transformation and again during permission authorization. A real application should cache these lookups, while choosing an expiration and invalidation strategy that meets its authorization revocation requirements.

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

| Method | Route | Feature name | Example permission |
| --- | --- | --- | --- |
| `GET` | `/api/people` | `People_GetAll` | `people:read` |
| `GET` | `/api/people/{id}` | `People_Get` | `people:read` |
| `POST` | `/api/people` | `People_Insert` | `people:write` |
| `PUT` | `/api/people/{id}` | `People_Update` | `people:write` |
| `DELETE` | `/api/people/{id}` | `People_Delete` | `people:write` |

The sample endpoints return empty `200 OK` responses; their purpose is to demonstrate authorization rather than CRUD behavior. `GET /api/routes` lists the protected routes grouped by feature name, with their endpoint names, patterns, and HTTP methods, and is intended for local diagnostics only.

## Using roles in a real-world scenario

To keep the sample simple, permissions are assigned directly to each user through the `Users.Permissions` column. This quickly becomes hard to manage as the number of users grows. For example, granting `people:write` to every editor requires updating every single user.

In a real application, use a classic Role-Based Access Control (RBAC) model. Roles group permissions, and users are assigned roles rather than individual permissions:

```text
Users --< UserRoles >-- Roles --< RolePermissions
FeaturePermissions (unchanged)
```

With this model:

- Endpoints still declare only a feature through `RequirePermission(...)`, and `FeaturePermissions` still maps each feature to its required permissions.
- At login, the token contains the user's **roles** instead of the full list of permissions, which keeps the token small.
- The authorization handler resolves the permissions granted to those roles on the server, preferably through a cache such as `HybridCache` or `IMemoryCache` to avoid a database query on every request.
- Changes to the permissions of a role take effect immediately, without issuing new tokens. Only adding or removing a role from a user still requires a new token.

Roles are used only to group permissions. Endpoints should not check roles directly (for example with `RequireRole("Admin")`), because that would hard-code authorization rules in the application and lose the ability to configure them in the database.

## Response behavior

- Missing or invalid bearer tokens produce `401 Unauthorized`.
- Authenticated users without a matching required permission produce `403 Forbidden`.
- Exception and status-code middleware use Problem Details responses where supported by the configured authentication handler and middleware pipeline.

## Key components

- `Program.cs`: application setup, endpoints, `RequirePermission` extension, `PermissionRequirement`, `PermissionMetadata`, `PermissionClaimsTransformation`, and `PermissionAuthorizationHandler`
- `Data/ApplicationDbContext.cs`: EF Core model configuration
- `Data/Entities/User.cs`: credentials and granted permissions
- `Data/Entities/FeaturePermission.cs`: feature names and required permissions
- `Data/Migrations`: SQL Server database schema

## License

This project is licensed under the terms in [LICENSE.txt](LICENSE.txt).
