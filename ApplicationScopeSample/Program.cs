using System.Security.Claims;
using ApplicationScopeSample.Data;
using ApplicationScopeSample.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SimpleAuthentication;
using SimpleAuthentication.JwtBearer;
using TinyHelpers.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddHttpContextAccessor();

builder.Services.AddSqlServer<ApplicationDbContext>(builder.Configuration.GetConnectionString("SqlConnection"), options =>
{
    options.UseCompatibilityLevel(170);
});

builder.Services.AddSimpleAuthentication(builder.Configuration);

builder.Services.AddScoped<IAuthorizationHandler, ScopeAuthorizationHandler>();

builder.Services.AddDefaultProblemDetails();
builder.Services.AddDefaultExceptionHandler();

builder.Services.AddOpenApi(options =>
{
    options.AddSimpleAuthentication(builder.Configuration);
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

    var token = await jwtBearerService.CreateTokenAsync(user.Email, [new Claim("scp", string.Join(' ', user.Scopes))]);
    return TypedResults.Ok(new LoginResponse(token));
});

app.MapGet("/api/people", () => TypedResults.Ok())
    .WithName("People_GetAll")
    .RequireScope();

app.MapGet("/api/people/{id:guid}", (Guid id) => TypedResults.Ok())
    .WithName("People_Get")
    .RequireScope();

app.MapPost("/api/people", () => TypedResults.Ok())
    .WithName("People_Insert")
    .RequireScope();

app.MapPut("/api/people/{id:guid}", (Guid id) => TypedResults.Ok())
    .WithName("People_Update")
    .RequireScope();

app.MapDelete("/api/people/{id:guid}", (Guid id) => TypedResults.Ok())
    .WithName("People_Delete")
    .RequireScope();

app.MapGet("/debug/routes", (IEnumerable<EndpointDataSource> endpointSources) =>
{
    var endpoints = endpointSources.SelectMany(source => source.Endpoints);
    var routes = endpoints.OfType<RouteEndpoint>()
        .Select(e => new
        {
            Name = e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
            Pattern = e.RoutePattern.RawText,
            Methods = e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods[0]
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

public class ScopeRequirement : IAuthorizationRequirement
{
    public static ScopeRequirement Instance { get; } = new();

    private ScopeRequirement()
    {
    }
}

public static class EndpointConventionBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        public TBuilder RequireScope()
        {
            var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(ScopeRequirement.Instance).Build();
            builder.RequireAuthorization(policy);

            return builder;
        }
    }
}

public class ScopeAuthorizationHandler(IHttpContextAccessor httpContextAccessor, ApplicationDbContext dbContext) : AuthorizationHandler<ScopeRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ScopeRequirement requirement)
    {
        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext?.GetEndpoint() is not RouteEndpoint endpoint)
        {
            return;
        }

        var endpointName = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
            ?? $"{httpContext.Request.Method} {endpoint.RoutePattern.RawText?.TrimStart('/')}";

        var requiredScopes = await dbContext.FeatureScopes.Where(x => x.Name == endpointName)
            .Select(x => x.Scopes).FirstOrDefaultAsync(httpContext.RequestAborted) ?? [];

        if (requiredScopes.Count == 0)
        {
            return;
        }

        var grantedScopes = context.User.FindAll(c => c.Type is "scp" or "http://schemas.microsoft.com/identity/claims/scope")
            .Select(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).SelectMany(s => s)
            .ToHashSet(StringComparer.Ordinal) ?? [];

        if (requiredScopes.Any(grantedScopes.Contains)) // requiredScopes.All(grantedScopes.Contains)
        {
            context.Succeed(requirement);
        }
    }
}