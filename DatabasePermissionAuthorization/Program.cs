using System.Security.Claims;
using ApplicationScopeSample.Data;
using ApplicationScopeSample.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SimpleAuthentication;
using SimpleAuthentication.JwtBearer;
using TinyHelpers.AspNetCore.Extensions;
using TinyHelpers.AspNetCore.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddHttpContextAccessor();

builder.Services.AddSqlServer<ApplicationDbContext>(builder.Configuration.GetConnectionString("SqlConnection"), options =>
{
    options.UseCompatibilityLevel(170);
});

builder.Services.AddSimpleAuthentication(builder.Configuration);

builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddDefaultProblemDetails();
builder.Services.AddDefaultExceptionHandler();

builder.Services.AddOpenApi(options =>
{
    options.AddSimpleAuthentication(builder.Configuration);

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

app.MapPost("/api/login", async Task<Results<BadRequest, Ok<LoginResponse>>> (LoginRequest loginRequest, ApplicationDbContext dbContext, IJwtBearerService jwtBearerService, CancellationToken cancellationToken) =>
{
    var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Email == loginRequest.Email && x.Password == loginRequest.Password, cancellationToken);
    if (user is null)
    {
        return TypedResults.BadRequest();
    }

    var token = await jwtBearerService.CreateTokenAsync(user.Email, [new Claim(ClaimTypes.UserData, string.Join(' ', user.Permissions))]);
    return TypedResults.Ok(new LoginResponse(token));
});

app.MapGet("/api/people", () => TypedResults.Ok())
    .RequirePermission("People_GetAll");

app.MapGet("/api/people/{id:guid}", (Guid id) => TypedResults.Ok())
    .RequirePermission("People_Get");

app.MapPost("/api/people", () => TypedResults.Ok())
    .RequirePermission("People_Insert");

app.MapPut("/api/people/{id:guid}", (Guid id) => TypedResults.Ok())
    .RequirePermission("People_Update");

app.MapDelete("/api/people/{id:guid}", (Guid id) => TypedResults.Ok())
    .RequirePermission("People_Delete");

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

    var strategy = dbContext.Database.CreateExecutionStrategy();

    await strategy.ExecuteAsync(async () =>
    {
        await dbContext.Database.MigrateAsync();
    });
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

        var requiredPermissions = await dbContext.FeaturePermissions.Where(x => x.Name == featureName)
            .Select(x => x.Permissions).FirstOrDefaultAsync(httpContext.RequestAborted) ?? [];

        if (requiredPermissions.Count == 0)
        {
            return;
        }

        var grantedPermissions = context.User.FindAll(c => c.Type == ClaimTypes.UserData)
            .Select(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).SelectMany(s => s)
            .ToHashSet(StringComparer.Ordinal) ?? [];

        if (requiredPermissions.Any(grantedPermissions.Contains)) // requiredPermissions.All(grantedPermissions.Contains)
        {
            context.Succeed(requirement);
        }
    }
}