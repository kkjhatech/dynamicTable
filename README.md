# Dynamic API

A fully dynamic ASP.NET Core Web API where endpoints are configured in a database table, not hardcoded in C# controllers.

## Features

- **Dynamic Endpoint Registration**: Endpoints are read from `ApiEndpointConfig` table at startup
- **Flexible Parameter Binding**: Supports route, query, and body parameters
- **Stored Procedure Execution**: All database operations via Dapper
- **Caching**: In-memory caching of endpoint configuration
- **Admin Endpoints**: Reload configuration without restarting
- **Swagger/OpenAPI**: Auto-generated documentation from configuration

## Project Structure

```
DynamicApi/
├── Controllers/           # (Empty - using minimal APIs)
├── Services/
│   ├── IEndpointConfigService.cs
│   ├── EndpointConfigService.cs    # Configuration cache management
│   └── DynamicQueryService.cs      # Dapper SP execution
├── Models/
│   ├── ApiEndpointConfig.cs        # Entity model
│   └── ApiResponse.cs              # Response wrapper
├── Middleware/
│   └── DynamicRoutingMiddleware.cs # Fallback request handler
├── Database/
│   ├── CreateDatabase.sql          # Database creation
│   └── Setup.sql                   # Full schema + seed data
├── Program.cs                      # App startup + route registration
├── appsettings.json                # Connection strings
└── README.md
```

## Quick Start

### 1. Setup Database

```bash
# Option 1: Run scripts in SSMS or Azure Data Studio
1. Run Database/CreateDatabase.sql
2. Run Database/Setup.sql

# Option 2: Use sqlcmd
sqlcmd -S localhost -U sa -P "YourStrong@Passw0rd" -i Database/CreateDatabase.sql
sqlcmd -S localhost -U sa -P "YourStrong@Passw0rd" -d DynamicApi -i Database/Setup.sql
```

### 2. Update Connection String

Edit `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=DynamicApi;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;"
  }
}
```

### 3. Run the API

```bash
cd DynamicApi
dotnet restore
dotnet run
```

The API will start at:
- HTTP: http://localhost:5000
- HTTPS: https://localhost:7000
- Swagger UI: https://localhost:7000/swagger

## Pre-Configured Endpoints

After running the setup script, these endpoints are available:

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/customers` | Get all customers |
| GET | `/api/customers/{customerId}` | Get customer by ID |
| POST | `/api/orders` | Create a new order |
| PUT | `/api/customers/{customerId}` | Update a customer |
| DELETE | `/api/products/{productId}` | Delete (soft) a product |
| GET | `/api/health` | Health check |
| POST | `/api/reload-config` | Reload endpoint config cache |

## Testing Examples

### Get Customer by ID
```bash
curl http://localhost:5000/api/customers/1
```

### Get All Customers
```bash
curl http://localhost:5000/api/customers
```

### Create Order
```bash
curl -X POST http://localhost:5000/api/orders \
  -H "Content-Type: application/json" \
  -d '{"customerId": 1, "totalAmount": 150.00, "status": "Pending"}'
```

### Update Customer
```bash
curl -X PUT http://localhost:5000/api/customers/1 \
  -H "Content-Type: application/json" \
  -d '{"firstName": "Johnny", "email": "new.email@example.com"}'
```

### Delete Product
```bash
curl -X DELETE http://localhost:5000/api/products/1
```

### Reload Configuration
```bash
curl -X POST http://localhost:5000/api/reload-config \
  -H "X-Admin-Api-Key: admin-secret-key-12345"
```

## Adding New Endpoints

Insert a new row into `ApiEndpointConfig`:

```sql
INSERT INTO ApiEndpointConfig (MethodName, HttpVerb, RouteTemplate, StoredProcedureName, ParameterNames, IsActive, Description)
VALUES (
    'GetProductsByCategory',           -- MethodName
    'GET',                             -- HttpVerb
    'api/products/category/{category}', -- RouteTemplate
    'usp_GetProductsByCategory',       -- StoredProcedureName
    'category',                        -- ParameterNames (comma-separated)
    1,                                 -- IsActive
    'Get products by category'         -- Description
);
```

Then call `/api/reload-config` or restart the app.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Client Request                        │
└─────────────────────┬───────────────────────────────────┘
                      ▼
┌─────────────────────────────────────────────────────────┐
│              ASP.NET Core Routing                        │
│    (Matches against dynamically registered routes)       │
└─────────────────────┬───────────────────────────────────┘
                      ▼
┌─────────────────────────────────────────────────────────┐
│           DynamicRoutingMiddleware                     │
│    (Fallback for routes not explicitly registered)       │
└─────────────────────┬───────────────────────────────────┘
                      ▼
┌─────────────────────────────────────────────────────────┐
│              EndpointConfigService                       │
│    (In-memory cache of ApiEndpointConfig rows)         │
└─────────────────────┬───────────────────────────────────┘
                      ▼
┌─────────────────────────────────────────────────────────┐
│              DynamicQueryService                       │
│    (Dapper + SqlConnection to execute SPs)             │
└─────────────────────┬───────────────────────────────────┘
                      ▼
┌─────────────────────────────────────────────────────────┐
│                 SQL Server                             │
│              (Stored Procedures)                        │
└─────────────────────────────────────────────────────────┘
```

## Configuration

### appsettings.json

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=DynamicApi;User Id=sa;Password=YourPassword;TrustServerCertificate=True;"
  },
  "AdminApiKey": "your-secret-api-key",
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

## Response Format

All responses follow this structure:

**Success:**
```json
{
  "success": true,
  "data": [...]
}
```

**Error:**
```json
{
  "success": false,
  "error": "Error message here"
}
```

## Error Codes

| Status | Meaning |
|--------|---------|
| 200 | Success |
| 400 | Bad Request (missing parameters) |
| 401 | Unauthorized (invalid API key) |
| 404 | Not Found |
| 500 | Internal Server Error |

## Technologies

- **.NET 8** - Framework
- **Dapper** - Micro-ORM
- **Microsoft.Data.SqlClient** - SQL Server driver
- **Swashbuckle** - OpenAPI/Swagger

## License

MIT
