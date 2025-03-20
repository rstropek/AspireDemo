# Create empty project with web API

Let's create a new project with .NET Aspire and add a simple web API.

[.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/) is a framework designed to **simplify the development of distributed, cloud-native applications**. It offers a collection of tools, templates, and NuGet packages that streamline the configuration, orchestration, and integration of multiple services. By providing opinionated defaults for logging, telemetry, health checks, and service discovery, .NET Aspire minimizes setup complexities and accelerates the development cycle—allowing developers to focus on building robust, scalable features rather than managing infrastructure.

Additionally, .NET Aspire enhances developer productivity by delivering a consistent and repeatable environment for both local development and production deployment. Its integrated dashboard offers real-time observability into application performance, making it easier to monitor health, troubleshoot issues, and optimize resource usage.

## Create project

The following commands start by creating a new .NET Aspire solution. Inside the new folder _WebApi_, a new ASP.NET Core Web API project is generated, and a reference is added to the "AspireWalkthrough.ServiceDefaults" project, which provides common service configurations and defaults for the application. 

Finally, the process moves into the "AspireWalkthrough.AppHost" project folder and adds a reference to the Web API project, thereby linking the AppHost project with the Web API so that it can orchestrate or interact with it.

```sh
dotnet new aspire

mkdir WebApi
cd WebApi
dotnet new webapi
dotnet add reference ../AspireWalkthrough.ServiceDefaults
cd ..

dotnet sln add WebApi

cd AspireWalkthrough.AppHost
dotnet add reference ../WebApi
cd ..
```

## Web API

Let's start with a very simple web API. The code calls _AddServiceDefaults()_, which is a custom extension method imported from a shared project. This method automatically sets up a range of essential services—such as logging, telemetry, health checks, and service discovery—ensuring that the application has a consistent and production-ready configuration without the need for manual setup of each individual service.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(); // Note: Service defaults come from shared project
var app = builder.Build();

app.MapDefaultEndpoints();
app.UseHttpsRedirection();

app.MapGet("/ping", () => "pong");

app.Run();
```

## App Host

[The AppHost defines the _app model_](https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/app-host-overview). code begins by creating a builder tailored for distributed applications using _DistributedApplication.CreateBuilder()_. It adds the WebApi project to the application by invoking _builder.AddProject_ and configures it to run with two replicas.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var webapi = builder.AddProject<Projects.WebApi>("webapi")
    .WithReplicas(2);

builder.Build().Run();
```

Take a look at the _.csproj_ file of the App Host. Note the `<IsAspireHost>true</IsAspireHost>`. The app host orchestrates the execution of all projects that are part of the _app model_.

## Run

```sh
dotnet run --project AspireWalkthrough.AppHost/
```

# Add backend service

Let's add another web API that we can access from the first one. We will see how we can reference APIs from another. Additionally, we try debugging.

## Create project

```sh
mkdir Backend
cd Backend
dotnet new webapi
dotnet add reference ../AspireWalkthrough.ServiceDefaults

dotnet sln add Backend

cd AspireWalkthrough.AppHost
dotnet add reference ../Backend
cd ..
```

## Backend

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var app = builder.Build();

app.MapDefaultEndpoints();
app.UseHttpsRedirection();

app.MapGet("/data", () =>
{
    return Results.Ok(new {
        X = 10,
        Y = 20,
    });
});

app.Run();
```

## App Host

The following code adds a backend project, named _backend_, to the application and configures it to run with two replicas. Next, the web API project is set up to reference the backend project. This reference indicates that the web API will interact with the backend service.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddProject<Projects.Backend>("backend")
    .WithReplicas(2);

var webapi = builder.AddProject<Projects.WebApi>("webapi")
    .WithReference(backend)
    .WithReplicas(2);

builder.Build().Run();
```

## Web API

Let's try to access the backend service from the web API. We will use the _HttpClientFactory_ to create an HTTP client and call the backend service. The response is then returned to the client.

The endpoint _ip_ can be used to resolve the IP address of the backend service. The service reference is retrieved from the configuration and resolved to an IP address using the _Dns.GetHostEntryAsync_ method.

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddHttpClient("backend", client =>
{
    // Uses .NET service discovery (https://learn.microsoft.com/en-us/dotnet/core/extensions/service-discovery)
    client.BaseAddress = new("https://backend");
});
var app = builder.Build();

app.MapDefaultEndpoints();
app.UseHttpsRedirection();

app.MapGet("/ping", () => "pong");

// .NET Activity Source is called "Tracer" in OpenTelemetry.
// Consider using https://opentelemetry.io/docs/languages/net/shim/
// to harmonize the naming of the components.
var source = new ActivitySource("WebApi");
var totalSums = new Meter("TotalSum").CreateCounter<int>("sum.total");

app.MapGet("/add", async (IHttpClientFactory factory, ILogger<Program> logger) =>
{
    logger.LogInformation("Adding numbers via backend service");

    var backend = factory.CreateClient("backend");
    var dataResponse = await backend.GetFromJsonAsync<ResultDto>("/data");

    var sum = 0;

    // .NET Activities are called Spans in OpenTelemetry.
    // When using Aspire, telemetry data is sent to the dashboard using OTLP
    // (OpenTelemetry protocol)
    using (var activity = source.StartActivity("Adding"))
    {
        await Task.Delay(100);
        sum = dataResponse?.X ?? 0 + dataResponse?.Y ?? 0;
        activity?.SetTag("sum", sum);
        totalSums.Add(sum);
    }

    return Results.Ok(new
    {
        Sum = sum,
    });
});

app.MapGet("/ip", async () =>
{
    // See also https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/app-host-overview?tabs=docker#service-endpoint-environment-variable-format
    // We could also access the environment variable services__backend__https__0 directly.
    var serviceReference = config["Services:backend:https:0"];
    serviceReference = serviceReference?.Replace("https://", string.Empty);
    var port = serviceReference?[(serviceReference.IndexOf(':') + 1)..] ?? string.Empty;
    serviceReference = serviceReference![..^(port.Length + 1)];

    var ipAddress = string.Empty;
    try
    {
        if (!string.IsNullOrEmpty(serviceReference))
        {
            var hostEntry = await Dns.GetHostEntryAsync(serviceReference);
            ipAddress = hostEntry.AddressList.FirstOrDefault()?.ToString() ?? "Not found";
        }
        else
        {
            ipAddress = "Service reference not available";
        }
    }
    catch (Exception ex)
    {
        ipAddress = $"Error resolving IP: {ex.Message}";
    }

    return Results.Ok(new { 
        ServiceReference = serviceReference,
        Port = port,
        IpAddress = ipAddress
    });
});

app.Run();

record ResultDto(int X, int Y);
```

Add meter to OTLP configuration in _ServiceDefaults_:

```csharp
public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
{
    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;
    });

    builder.Services.AddOpenTelemetry()
        .WithMetrics(metrics =>
        {
            metrics.AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation();

            metrics.AddMeter("TotalSum"); // <<< Add this
        })
        .WithTracing(tracing =>
        {
            tracing.AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation()
                // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                //.AddGrpcClientInstrumentation()
                .AddHttpClientInstrumentation();
        });

    builder.AddOpenTelemetryExporters();

    return builder;
}
```

# Add Postgres

Let's add a Postgres database to our project. We will see how we can use the database in our services.

## Add NuGet for Postgres

```sh
cd AspireWalkthrough.AppHost
dotnet add package Aspire.Hosting.PostgreSQL
cd ..

cd WebApi
dotnet add package Aspire.Npgsql
cd ..
```

## App Host

The Postgres section is responsible for provisioning a PostgreSQL server container and configuring persistent storage. A writable data volume named _postgresdata_ is attached to ensure that database data remains persistent even when containers are restarted. Two distinct databases are defined on the server: one simply named "aspiredb" and another, "postgresdb," which uses the default database name "postgres." The orchestration ensures that any service relying on these databases, such as the web API, will not begin operation until both databases are fully initialized and ready, thanks to the use of dependency waiting methods.

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddProject<Projects.Backend>("backend")
    .WithReplicas(2);

var postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume("postgresdata", isReadOnly: false);
var aspiredb = postgres.AddDatabase("aspiredb");
var postgresdb = postgres.AddDatabase(name: "postgresdb", databaseName: "postgres");

var webapi = builder.AddProject<Projects.WebApi>("webapi")
    .WithReference(backend)
    .WithReplicas(2)
    .WithReference(postgresdb)
    .WaitFor(postgresdb)
    .WithReference(aspiredb)
    .WaitFor(aspiredb);

builder.Build().Run();
```

## Web API

```csharp
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddHttpClient("backend", client =>
{
    client.BaseAddress = new("https://backend");
});
builder.AddKeyedNpgsqlDataSource("aspiredb");
builder.AddKeyedNpgsqlDataSource("postgresdb");
var app = builder.Build();

app.MapDefaultEndpoints();
app.UseHttpsRedirection();

app.MapGet("/ping", () => "pong");

// .NET Activity Source is called "Tracer" in OpenTelemetry.
// Consider using https://opentelemetry.io/docs/languages/net/shim/
// to harmonize the naming of the components.
var source = new ActivitySource("WebApi");
var totalSums = new Meter("TotalSum").CreateCounter<int>("sum.total");

app.MapGet("/add", async (IHttpClientFactory factory, ILogger<Program> logger) =>
{
    logger.LogInformation("Adding numbers via backend service");

    var backend = factory.CreateClient("backend");
    var dataResponse = await backend.GetFromJsonAsync<ResultDto>("/data");

    var sum = 0;

    // .NET Activities are called Spans in OpenTelemetry.
    // When using Aspire, telemetry data is sent to the dashboard using OTLP
    // (OpenTelemetry protocol)
    using (var activity = source.StartActivity("Adding"))
    {
        await Task.Delay(100);
        sum = dataResponse?.X ?? 0 + dataResponse?.Y ?? 0;
        activity?.SetTag("sum", sum);
        totalSums.Add(sum);
    }

    return Results.Ok(new
    {
        Sum = sum,
    });
});

app.MapGet("/ip", async () =>
{
    // See also https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/app-host-overview?tabs=docker#service-endpoint-environment-variable-format
    // We could also access the environment variable services__backend__https__0 directly.
    var serviceReference = config["Services:backend:https:0"];
    serviceReference = serviceReference?.Replace("https://", string.Empty);
    var port = serviceReference?[(serviceReference.IndexOf(':') + 1)..] ?? string.Empty;
    serviceReference = serviceReference![..^(port.Length + 1)];

    var ipAddress = string.Empty;
    try
    {
        if (!string.IsNullOrEmpty(serviceReference))
        {
            var hostEntry = await Dns.GetHostEntryAsync(serviceReference);
            ipAddress = hostEntry.AddressList.FirstOrDefault()?.ToString() ?? "Not found";
        }
        else
        {
            ipAddress = "Service reference not available";
        }
    }
    catch (Exception ex)
    {
        ipAddress = $"Error resolving IP: {ex.Message}";
    }

    return Results.Ok(new { 
        ServiceReference = serviceReference,
        Port = port,
        IpAddress = ipAddress
    });
});

app.MapGet("/answer-from-db", async ([FromKeyedServices("postgresdb")] NpgsqlDataSource postgres, [FromKeyedServices("aspiredb")] NpgsqlDataSource aspire) =>
{
    // Ensure that database "aspiredb" exists
    var csb = new NpgsqlConnectionStringBuilder(aspire.ConnectionString);
    var databaseName = csb.Database ?? throw new InvalidOperationException("Connection string is null");
    if (!await CheckDatabaseExists(postgres, databaseName))
    {
        await using var createCommand = postgres.CreateCommand($"CREATE DATABASE {databaseName}");
        await createCommand.ExecuteNonQueryAsync();
    }

    // Simulate getting some data from the database
    await using var command = aspire.CreateCommand();
    command.CommandText = "SELECT 42";
    var result = await command.ExecuteScalarAsync();
    return Results.Ok(new { Answer = result, });
});

app.Run();

/// <summary>
/// Helper method to check if a database exists
/// </summary>
static async Task<bool> CheckDatabaseExists(NpgsqlDataSource postgres, string dbName)
{
    await using var cmd = postgres.CreateCommand("SELECT 1 FROM pg_database WHERE datname = @dbName");
    cmd.Parameters.AddWithValue("@dbName", dbName);
    var result = await cmd.ExecuteScalarAsync();
    return result != null;
}

record ResultDto(int X, int Y);
```

## Check Docker

```sh
docker ps
docker volume ls | grep postgresdata
docker run --rm -v postgresdata:/volume alpine ls /volume
```

# Add Redis

Let's add a Redis database to our project. We will see how we can use it for caching in ASP.NET Core.

## Add NuGet for Redis

```sh
cd AspireWalkthrough.AppHost
dotnet add package Aspire.Hosting.Redis
cd ..

cd WebApi
dotnet add package Aspire.StackExchange.Redis.DistributedCaching
cd ..
```

## App Host

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddProject<Projects.Backend>("backend")
    .WithReplicas(2);

var postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume("postgresdata", isReadOnly: false);
var aspiredb = postgres.AddDatabase("aspiredb");
var postgresdb = postgres.AddDatabase(name: "postgresdb", databaseName: "postgres");

var cache = builder.AddRedis("cache");

var webapi = builder.AddProject<Projects.WebApi>("webapi")
    .WithReference(backend)
    .WithReplicas(2)
    .WithReference(postgresdb)
    .WaitFor(postgresdb)
    .WithReference(aspiredb)
    .WaitFor(aspiredb)
    .WithReference(cache)
    .WaitFor(cache);

builder.Build().Run();
```

## Web API

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Protocol;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddHttpClient("backend", client =>
{
    client.BaseAddress = new("https://backend");
});
builder.AddKeyedNpgsqlDataSource("aspiredb");
builder.AddKeyedNpgsqlDataSource("postgresdb");
builder.AddRedisDistributedCache("cache");
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromSeconds(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});
var app = builder.Build();

app.MapDefaultEndpoints();
app.UseHttpsRedirection();
app.UseSession();

app.MapGet("/ping", () => "pong");

// .NET Activity Source is called "Tracer" in OpenTelemetry.
// Consider using https://opentelemetry.io/docs/languages/net/shim/
// to harmonize the naming of the components.
var source = new ActivitySource("WebApi");
var totalSums = new Meter("TotalSum").CreateCounter<int>("sum.total");

app.MapGet("/add", async (IHttpClientFactory factory, ILogger<Program> logger) =>
{
    logger.LogInformation("Adding numbers via backend service");

    var backend = factory.CreateClient("backend");
    var dataResponse = await backend.GetFromJsonAsync<ResultDto>("/data");

    var sum = 0;

    // .NET Activities are called Spans in OpenTelemetry.
    // When using Aspire, telemetry data is sent to the dashboard using OTLP
    // (OpenTelemetry protocol)
    using (var activity = source.StartActivity("Adding"))
    {
        await Task.Delay(100);
        sum = dataResponse?.X ?? 0 + dataResponse?.Y ?? 0;
        activity?.SetTag("sum", sum);
        totalSums.Add(sum);
    }

    return Results.Ok(new
    {
        Sum = sum,
    });
});

app.MapGet("/ip", async () =>
{
    // See also https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/app-host-overview?tabs=docker#service-endpoint-environment-variable-format
    // We could also access the environment variable services__backend__https__0 directly.
    var serviceReference = config["Services:backend:https:0"];
    serviceReference = serviceReference?.Replace("https://", string.Empty);
    var port = serviceReference?[(serviceReference.IndexOf(':') + 1)..] ?? string.Empty;
    serviceReference = serviceReference![..^(port.Length + 1)];

    var ipAddress = string.Empty;
    try
    {
        if (!string.IsNullOrEmpty(serviceReference))
        {
            var hostEntry = await Dns.GetHostEntryAsync(serviceReference);
            ipAddress = hostEntry.AddressList.FirstOrDefault()?.ToString() ?? "Not found";
        }
        else
        {
            ipAddress = "Service reference not available";
        }
    }
    catch (Exception ex)
    {
        ipAddress = $"Error resolving IP: {ex.Message}";
    }

    return Results.Ok(new { 
        ServiceReference = serviceReference,
        Port = port,
        IpAddress = ipAddress
    });
});

app.MapGet("/answer-from-db", async ([FromKeyedServices("postgresdb")] NpgsqlDataSource postgres, [FromKeyedServices("aspiredb")] NpgsqlDataSource aspire) =>
{
    // Ensure that database "aspiredb" exists
    var csb = new NpgsqlConnectionStringBuilder(aspire.ConnectionString);
    var databaseName = csb.Database ?? throw new InvalidOperationException("Connection string is null");
    if (!await CheckDatabaseExists(postgres, databaseName))
    {
        await using var createCommand = postgres.CreateCommand($"CREATE DATABASE {databaseName}");
        await createCommand.ExecuteNonQueryAsync();
    }

    // Simulate getting some data from the database
    await using var command = aspire.CreateCommand();
    command.CommandText = "SELECT 42";
    var result = await command.ExecuteScalarAsync();
    return Results.Ok(new { Answer = result, });
});

app.MapGet("/set-session", ([FromQuery(Name = "key")] string keyFromQueryString, HttpContext context) =>
{
    context.Session.SetString("key", keyFromQueryString);
    return Results.Ok();
});

app.MapGet("/get-session", (HttpContext context) =>
{
    var value = context.Session.GetString("key");
    return Results.Ok(new { Value = value, });
});

app.Run();

/// <summary>
/// Helper method to check if a database exists
/// </summary>
static async Task<bool> CheckDatabaseExists(NpgsqlDataSource postgres, string dbName)
{
    await using var cmd = postgres.CreateCommand("SELECT 1 FROM pg_database WHERE datname = @dbName");
    cmd.Parameters.AddWithValue("@dbName", dbName);
    var result = await cmd.ExecuteScalarAsync();
    return result != null;
}

record ResultDto(int X, int Y);
```

# Add Web Frontend

Let's add a web frontend to our project. We will learn more about networking.

## Create project

```sh
mkdir Frontend
cd Frontend
npm init -y
npm install -D vite
cd ..
```

Copy the content of the following files:

* [Frontend/package.json](Frontend/package.json)
* [Frontend/vite.config.js](Frontend/vite.config.js)
* [Frontend/index.html](Frontend/index.html)
* [Frontend/src/index.js](Frontend/src/index.js)

Pay special attention to _vite.config.js_ and _package.json_.

## Add NuGet for Node projects

```sh
cd AspireWalkthrough.AppHost
dotnet add package Aspire.Hosting.NodeJs
cd ..
```

## App Host

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddProject<Projects.Backend>("backend")
    .WithReplicas(2);

var postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume("postgresdata", isReadOnly: false);
var aspiredb = postgres.AddDatabase("aspiredb");
var postgresdb = postgres.AddDatabase(name: "postgresdb", databaseName: "postgres");

var cache = builder.AddRedis("cache");

var webapi = builder.AddProject<Projects.WebApi>("webapi")
    .WithReference(backend)
    .WithReplicas(2)
    .WithReference(postgresdb)
    .WaitFor(postgresdb)
    .WithReference(aspiredb)
    .WaitFor(aspiredb)
    .WithReference(cache)
    .WaitFor(cache);

var frontend = builder.AddNpmApp("frontend", "../Frontend")
    .WithReference(webapi)
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints();

builder.Build().Run();
```

## Web API

Add CORS to the web API:

```csharp
...
builder.Services.AddCors();
...
app.UseCors(builder => builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
...
```

## Deployment (App Host)

Not a focus of this walkthrough.

```sh
dotnet add package Aspire.Hosting.Azure.PostgreSQL
dotnet add package Aspire.Hosting.Azure.Redis
```

```cs
var builder = DistributedApplication.CreateBuilder(args);

var backend = builder.AddProject<Projects.Backend>("backend")
    .WithReplicas(2);

var postgres = builder
    .AddAzurePostgresFlexibleServer("postgres")
    .RunAsContainer(cfg => cfg.WithDataVolume("postgresdata", isReadOnly: false));
var aspiredb = postgres.AddDatabase("aspiredb");
var postgresdb = postgres.AddDatabase(name: "postgresdb", databaseName: "postgres");

var cache = builder
    .AddAzureRedis("cache")
    .RunAsContainer();

var webapi = builder.AddProject<Projects.WebApi>("webapi")
    .WithReference(backend)
    .WithReplicas(2)
    .WithReference(postgresdb)
    .WaitFor(postgresdb)
    .WithReference(aspiredb)
    .WaitFor(aspiredb)
    .WithReference(cache)
    .WaitFor(cache);

var frontend = builder.AddNpmApp("frontend", "../Frontend")
    .WithReference(webapi)
    .WithHttpEndpoint(env: "PORT")
    .WithExternalHttpEndpoints()
    .PublishAsDockerFile();

builder.Build().Run();
```

```sh
azd config set alpha.infraSynth on
azd infra synth
```
