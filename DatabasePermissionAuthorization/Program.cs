using System.Security.Claims;
using DatabasePermissionAuthorization.Data;
using DatabasePermissionAuthorization.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using SimpleAuthentication;
using SimpleAuthentication.JwtBearer;
using TinyHelpers.AspNetCore.Extensions;
using TinyHelpers.AspNetCore.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddHttpContextAccessor();

builder.Services.AddSqlServer<ApplicationDbContext>(builder.Configuration.GetConnectionString("SqlConnection"),
    options =>
    {
        options.UseCompatibilityLevel(170);
    });

builder.Services.AddSimpleAuthentication(builder.Configuration);

builder.Services.AddScoped<IClaimsTransformation, PermissionClaimsTransformation>();
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddDefaultProblemDetails();
builder.Services.AddDefaultExceptionHandler();

builder.Services.AddOpenApi(options =>
{
    options.AddSimpleAuthentication(builder.Configuration);

    options.RemoveServerList();
    options.AddDefaultProblemDetailsResponse();
});

var app = builder.Build();
await ConfigureDatabaseAsync(app.Services);

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi();
app.MapSwaggerUI(setupAction: options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", app.Environment.ApplicationName);
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/login", async Task<Results<BadRequest, Ok<LoginResponse>>> (LoginRequest request, ApplicationDbContext dbContext, IJwtBearerService jwtBearerService,
    CancellationToken cancellationToken) =>
{
    var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == request.Email && u.Password == request.Password, cancellationToken);

    if (user is null)
    {
        return TypedResults.BadRequest();
    }

    var token = await jwtBearerService.CreateTokenAsync(user.Email);

    return TypedResults.Ok(new LoginResponse(token));
});

app.MapGet("/api/people", () => TypedResults.Ok())
    .RequirePermission(featureName: "People_GetAll");

app.MapGet("/api/people/{id:guid}", (Guid id) => TypedResults.Ok())
    .RequirePermission(featureName: "People_Get");

app.MapPost("/api/people", () => TypedResults.Ok())
    .RequirePermission(featureName: "People_Insert");

app.MapPut("/api/people/{id:guid}", (Guid id) => TypedResults.Ok())
    .RequirePermission(featureName: "People_Update");

app.MapDelete("/api/people/{id:guid}", (Guid id) => TypedResults.Ok())
    .RequirePermission(featureName: "People_Delete");

app.MapGet("/api/routes", (IEnumerable<EndpointDataSource> endpointSources) =>
{
    var endpoints = endpointSources.SelectMany(source => source.Endpoints);
    var routes = endpoints.OfType<RouteEndpoint>()
        .Select(e => new
        {
            Name = e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
            e.Metadata.GetMetadata<PermissionMetadata>()?.FeatureName,
            Pattern = e.RoutePattern.RawText,
            Methods = e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods[0]
        }).Where(e => !string.IsNullOrEmpty(e.FeatureName))
        .GroupBy(g => g.FeatureName).Select(g => new
        {
            FeatureName = g.Key,
            Routes = g.ToList()
        });

    return routes;
});

app.Run();

static async Task ConfigureDatabaseAsync(IServiceProvider serviceProvider)
{
    await using var scope = serviceProvider.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    await EnsureDatabaseAsync();
    await RunMigrationsAsync();

    async Task EnsureDatabaseAsync()
    {
        var dbCreator = dbContext.GetService<IRelationalDatabaseCreator>();
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // Create the database if it does not exist.
            if (!await dbCreator.ExistsAsync())
            {
                await dbCreator.CreateAsync();
            }
        });
    }

    async Task RunMigrationsAsync()
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await dbContext.Database.MigrateAsync();
        });
    }
}
public class PermissionRequirement : IAuthorizationRequirement
{
    public static PermissionRequirement Instance { get; } = new();

    private PermissionRequirement()
    {
    }
}

public record class PermissionMetadata(string FeatureName);

public static class EndpointConventionBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        public TBuilder RequirePermission(string featureName)
        {
            builder.WithMetadata(new PermissionMetadata(featureName));

            var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(PermissionRequirement.Instance).Build();
            builder.RequireAuthorization(policy);

            return builder;
        }
    }
}

public class PermissionClaimsTransformation(ApplicationDbContext dbContext) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;

        if (identity?.IsAuthenticated == true)
        {
            var email = identity.Name;
            if (email is not null)
            {
                // Retrieve the user's permissions from the database.
                // In a real world application, you might want to cache this information to avoid hitting the database on every request.
                var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Email == email);

                if (user?.Permissions.Count > 0)
                {
                    // Add a single claim with all permissions as a space-separated string.
                    var permissionsClaim = new Claim(ClaimTypes.UserData, string.Join(' ', user.Permissions));
                    identity.AddClaim(permissionsClaim);
                }
            }
        }

        return principal;
    }
}

public class PermissionAuthorizationHandler(IHttpContextAccessor httpContextAccessor, ApplicationDbContext dbContext) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext?.GetEndpoint() is not RouteEndpoint endpoint)
        {
            return;
        }

        var featureName = endpoint.Metadata.GetMetadata<PermissionMetadata>()?.FeatureName
            ?? $"{httpContext.Request.Method} {endpoint.RoutePattern.RawText?.TrimStart('/')}";

        var requiredPermissions = await dbContext.FeaturePermissions
            .Where(fp => fp.Name == featureName).Select(fp => fp.Permissions)
            .FirstOrDefaultAsync(httpContext.RequestAborted) ?? [];

        if (requiredPermissions.Count == 0)
        {
            // No permissions defined, block access to the feature.
            return;
        }

        var grantedPermissions = context.User.FindAll(ClaimTypes.UserData)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet();

        if (requiredPermissions.Any(grantedPermissions.Contains))   //requiredPermissions.All(grantedPermissions.Contains)
        {
            // User has at least one of the required permissions, succeed the requirement.
            context.Succeed(requirement);
        }
    }
}