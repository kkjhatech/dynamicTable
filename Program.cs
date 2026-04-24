using DyApi.Middleware;
using DyApi.Models;
using DyApi.Services;
using System.Data.SqlClient;
using Microsoft.OpenApi.Models;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IEndpointConfigService, EndpointConfigService>();
builder.Services.AddScoped<IDynamicQueryService, DynamicQueryService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Configure Swagger
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Dynamic API",
        Version = "v1",
        Description = "A fully dynamic Web API with configurable endpoints"
    });

    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Admin-Api-Key",
        Description = "Admin API Key for protected endpoints"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            Array.Empty<string>()
        }
    });

    // Custom document filter to add dynamic endpoints
    options.DocumentFilter<DynamicEndpointDocumentFilter>();
});

var app = builder.Build();

// Ensure configuration is loaded at startup
using (var scope = app.Services.CreateScope())
{
    var configService = scope.ServiceProvider.GetRequiredService<IEndpointConfigService>();
    await configService.ReloadCacheAsync();
}

// Configure middleware pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Map admin routes before dynamic middleware
app.MapPost("/api/reload-config", async (HttpContext context, IEndpointConfigService configService, IConfiguration configuration) =>
{
    var apiKey = context.Request.Headers["X-Admin-Api-Key"].FirstOrDefault();
    var expectedApiKey = configuration["AdminApiKey"];

    if (string.IsNullOrEmpty(apiKey) || apiKey != expectedApiKey)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.ErrorResponse("Invalid or missing API key.")));
        return;
    }

    try
    {
        await configService.ReloadCacheAsync();
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.SuccessResponse(new { message = "Configuration reloaded successfully." })));
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.ErrorResponse($"Failed to reload configuration: {ex.Message}")));
    }
})
.WithName("ReloadConfig")
.WithOpenApi(operation =>
{
    operation.Summary = "Reload API endpoint configuration";
    operation.Description = "Clears and reloads the endpoint configuration cache from the database.";
    return operation;
});

// Health check endpoint
app.MapGet("/api/health", (IEndpointConfigService configService) =>
{
    var configs = configService.GetCachedEndpoints();
    return Results.Json(ApiResponse.SuccessResponse(new 
    { 
        status = "Healthy", 
        endpointsLoaded = configs.Count,
        timestamp = DateTime.UtcNow
    }));
})
.WithName("HealthCheck")
.WithOpenApi(operation =>
{
    operation.Summary = "Health check";
    operation.Description = "Returns the health status of the API and the number of loaded endpoints.";
    return operation;
});

// Register dynamic endpoints using minimal APIs
var endpointConfigService = app.Services.GetRequiredService<IEndpointConfigService>();
var endpoints = await endpointConfigService.GetAllEndpointsAsync();

foreach (var endpoint in endpoints)
{
    RegisterDynamicEndpoint(app, endpoint);
}

// Add fallback middleware for routes not registered above
app.UseMiddleware<DynamicRoutingMiddleware>();

app.Run();

// Helper method to register dynamic endpoints
void RegisterDynamicEndpoint(WebApplication app, ApiEndpointConfig config)
{
    var routePattern = config.RouteTemplate.TrimStart('/');
    var logger = app.Services.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Registering dynamic endpoint: {HttpVerb} {Route} -> {StoredProcedure}",
        config.HttpVerb, config.RouteTemplate, config.StoredProcedureName);

    try
    {
        switch (config.HttpVerb.ToUpperInvariant())
        {
            case "GET":
                app.MapGet(routePattern, async (HttpContext context, IDynamicQueryService queryService) =>
                {
                    return await HandleDynamicRequest(context, config, queryService);
                })
                .WithName(config.MethodName)
                .WithOpenApi(operation => BuildOpenApiOperation(operation, config));
                break;

            case "POST":
                app.MapPost(routePattern, async (HttpContext context, IDynamicQueryService queryService) =>
                {
                    return await HandleDynamicRequest(context, config, queryService);
                })
                .WithName(config.MethodName)
                .WithOpenApi(operation => BuildOpenApiOperation(operation, config));
                break;

            case "PUT":
                app.MapPut(routePattern, async (HttpContext context, IDynamicQueryService queryService) =>
                {
                    return await HandleDynamicRequest(context, config, queryService);
                })
                .WithName(config.MethodName)
                .WithOpenApi(operation => BuildOpenApiOperation(operation, config));
                break;

            case "DELETE":
                app.MapDelete(routePattern, async (HttpContext context, IDynamicQueryService queryService) =>
                {
                    return await HandleDynamicRequest(context, config, queryService);
                })
                .WithName(config.MethodName)
                .WithOpenApi(operation => BuildOpenApiOperation(operation, config));
                break;

            case "PATCH":
                app.MapPatch(routePattern, async (HttpContext context, IDynamicQueryService queryService) =>
                {
                    return await HandleDynamicRequest(context, config, queryService);
                })
                .WithName(config.MethodName)
                .WithOpenApi(operation => BuildOpenApiOperation(operation, config));
                break;

            default:
                logger.LogWarning("Unsupported HTTP verb: {HttpVerb} for endpoint {MethodName}", 
                    config.HttpVerb, config.MethodName);
                break;
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to register endpoint {MethodName}", config.MethodName);
    }
}

async Task<IResult> HandleDynamicRequest(HttpContext context, ApiEndpointConfig config, IDynamicQueryService queryService)
{
    try
    {
        var parameters = await ExtractParametersFromRequest(context, config);
        var validationError = ValidateRequiredParameters(config, parameters);

        if (!string.IsNullOrEmpty(validationError))
        {
            return Results.BadRequest(ApiResponse.ErrorResponse(validationError));
        }

        var result = await queryService.ExecuteStoredProcedureAsync(config.StoredProcedureName, parameters);
        return Results.Ok(ApiResponse.SuccessResponse(result));
    }
    catch (SqlException ex)
    {
        return Results.BadRequest(ApiResponse.ErrorResponse($"Database error: {ex.Message}"));
    }
    catch (Exception ex)
    {
        return Results.StatusCode(500);
    }
}

async Task<Dictionary<string, object?>> ExtractParametersFromRequest(HttpContext context, ApiEndpointConfig config)
{
    var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    var request = context.Request;
    var paramNames = config.GetParameterNames();

    foreach (var paramName in paramNames)
    {
        if (request.RouteValues.TryGetValue(paramName, out var routeValue) && routeValue != null)
        {
            parameters[paramName] = routeValue.ToString();
            continue;
        }

        if (request.Query.TryGetValue(paramName, out var queryValue) && queryValue.Count > 0)
        {
            parameters[paramName] = queryValue.FirstOrDefault();
            continue;
        }
    }

    if (config.HttpVerb is "POST" or "PUT" or "PATCH")
    {
        try
        {
            request.EnableBuffering();
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            request.Body.Position = 0;

            if (!string.IsNullOrWhiteSpace(body))
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in root.EnumerateObject())
                    {
                        parameters[property.Name] = property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString(),
                            JsonValueKind.Number => property.Value.TryGetInt64(out var longVal) ? longVal : property.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => null,
                            _ => property.Value.ToString()
                        };
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    return parameters;
}

string? ValidateRequiredParameters(ApiEndpointConfig config, Dictionary<string, object?> parameters)
{
    var requiredParams = config.GetParameterNames();
    var missingParams = requiredParams.Where(p => !parameters.ContainsKey(p) || parameters[p] == null).ToList();
    
    return missingParams.Count > 0 
        ? $"Missing required parameters: {string.Join(", ", missingParams)}" 
        : null;
}

OpenApiOperation BuildOpenApiOperation(OpenApiOperation operation, ApiEndpointConfig config)
{
    operation.Summary = config.MethodName;
    operation.Description = string.IsNullOrEmpty(config.Description) 
        ? $"Dynamic endpoint: {config.MethodName}" 
        : config.Description;

    var paramNames = config.GetParameterNames();
    foreach (var paramName in paramNames)
    {
        if (!operation.Parameters.Any(p => p.Name == paramName))
        {
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = paramName,
                In = config.RouteTemplate.Contains($"{{{paramName}}}") ? ParameterLocation.Path : ParameterLocation.Query,
                Required = config.RouteTemplate.Contains($"{{{paramName}}}")
            });
        }
    }

    return operation;
}

// Custom Swagger document filter for dynamic endpoints
public class DynamicEndpointDocumentFilter : Swashbuckle.AspNetCore.SwaggerGen.IDocumentFilter
{
    public void Apply(Microsoft.OpenApi.Models.OpenApiDocument swaggerDoc, Swashbuckle.AspNetCore.SwaggerGen.DocumentFilterContext context)
    {
    }
}
