using DyApi.Models;
using DyApi.Services;
using System.Security.Claims;
using System.Text.Json;

namespace DyApi.Middleware;

public class DynamicRoutingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<DynamicRoutingMiddleware> _logger;

    public DynamicRoutingMiddleware(RequestDelegate next, ILogger<DynamicRoutingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IEndpointConfigService configService, IDynamicQueryService queryService, IJwtService jwtService)
    {
        var request = context.Request;
        var path = request.Path.Value?.TrimStart('/') ?? "";
        var method = request.Method.ToUpperInvariant();

        _logger.LogDebug("Processing request: {Method} {Path}", method, path);

        var config = await configService.GetEndpointByRouteAndVerbAsync(path, method);

        if (config == null)
        {
            await _next(context);
            return;
        }

        _logger.LogInformation("Matched dynamic endpoint: {MethodName} ({HttpVerb} {Route})", 
            config.MethodName, config.HttpVerb, config.RouteTemplate);

        // Check authorization if RequiredRole is set
        if (!string.IsNullOrEmpty(config.RequiredRole))
        {
            var authResult = await AuthorizeRequestAsync(context, config.RequiredRole, jwtService);
            if (!authResult.IsAuthorized)
            {
                context.Response.StatusCode = authResult.StatusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.ErrorResponse(authResult.ErrorMessage!)));
                return;
            }
        }

        try
        {
            var parameters = await ExtractParametersAsync(context, config);
            var validationError = ValidateParameters(config, parameters);
            
            if (!string.IsNullOrEmpty(validationError))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.ErrorResponse(validationError)));
                return;
            }

            var maskedParams = parameters.ToDictionary(
                kvp => kvp.Key, 
                kvp => IsSensitive(kvp.Key) ? "***" : kvp.Value?.ToString() ?? "null");
            
            _logger.LogInformation("Calling {StoredProcedure} with parameters: {Parameters}", 
                config.StoredProcedureName, JsonSerializer.Serialize(maskedParams));

            var result = await queryService.ExecuteStoredProcedureAsync(config.StoredProcedureName, parameters);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.SuccessResponse(result)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing dynamic endpoint {MethodName}", config.MethodName);
            
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(ApiResponse.ErrorResponse(
                "An error occurred while processing your request.")));
        }
    }

    private static async Task<AuthResult> AuthorizeRequestAsync(HttpContext context, string requiredRole, IJwtService jwtService)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return new AuthResult { IsAuthorized = false, StatusCode = StatusCodes.Status401Unauthorized, ErrorMessage = "Authorization header missing or invalid." };
        }

        var token = authHeader.Substring("Bearer ".Length).Trim();
        var username = jwtService.ValidateAccessToken(token);

        if (string.IsNullOrEmpty(username))
        {
            return new AuthResult { IsAuthorized = false, StatusCode = StatusCodes.Status401Unauthorized, ErrorMessage = "Invalid or expired token." };
        }

        // Check role
        if (!string.IsNullOrEmpty(requiredRole))
        {
            // Get claims from the authenticated user
            var user = context.User;
            if (!user.Identity?.IsAuthenticated ?? true)
            {
                return new AuthResult { IsAuthorized = false, StatusCode = StatusCodes.Status401Unauthorized, ErrorMessage = "User not authenticated." };
            }

            var hasRole = user.IsInRole(requiredRole) || 
                          user.HasClaim(ClaimTypes.Role, requiredRole);

            if (!hasRole)
            {
                return new AuthResult { IsAuthorized = false, StatusCode = StatusCodes.Status403Forbidden, ErrorMessage = $"Required role '{requiredRole}' not found." };
            }
        }

        return new AuthResult { IsAuthorized = true };
    }

    private class AuthResult
    {
        public bool IsAuthorized { get; set; }
        public int StatusCode { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private static async Task<Dictionary<string, object?>> ExtractParametersAsync(HttpContext context, ApiEndpointConfig config)
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
                                JsonValueKind.Number => property.Value.TryGetInt64(out var longVal) 
                                    ? longVal 
                                    : property.Value.GetDouble(),
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

    private static string? ValidateParameters(ApiEndpointConfig config, Dictionary<string, object?> parameters)
    {
        var requiredParams = config.GetParameterNames();
        var missingParams = new List<string>();

        foreach (var param in requiredParams)
        {
            if (!parameters.ContainsKey(param) || parameters[param] == null)
            {
                missingParams.Add(param);
            }
        }

        return missingParams.Count > 0 
            ? $"Missing required parameters: {string.Join(", ", missingParams)}" 
            : null;
    }

    private static bool IsSensitive(string paramName)
    {
        var sensitiveKeywords = new[] { "password", "secret", "token", "key", "credential", "auth" };
        return sensitiveKeywords.Any(keyword => 
            paramName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
