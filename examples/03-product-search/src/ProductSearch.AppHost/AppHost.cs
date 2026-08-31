// Visual Studio F5 injects DEBUG_SESSION_* so DCP waits on PUT /run_session.
// VS 18.6 often never answers (120s timeout), then falls back to `dotnet.exe` with
// empty args and kills the API that already bootstrapped. Strip the session so DCP
// starts API/UI as processes. F5 debugs AppHost only — attach to ProductSearch.Api to debug it.
Environment.SetEnvironmentVariable("DEBUG_SESSION_PORT", null);
Environment.SetEnvironmentVariable("DEBUG_SESSION_TOKEN", null);
Environment.SetEnvironmentVariable("DEBUG_SESSION_SERVER_CERTIFICATE", null);
Environment.SetEnvironmentVariable("DEBUG_SESSION_INFO", null);

var builder = DistributedApplication.CreateBuilder(args);

var postgresUser = builder.AddParameter("postgres-user");
var postgresPassword = builder.AddParameter("postgres-password", secret: true);

var postgres = builder.AddPostgres("postgres", postgresUser, postgresPassword)
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg16")
    .WithDataVolume("productsearch-pgdata")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithHostPort(5432);

var db = postgres.AddDatabase("productsearch");

var api = builder.AddProject<Projects.ProductSearch_Api>("productsearch-api")
    .WithReference(db)
    .WaitFor(db)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.ProductSearch_UI>("productsearch-ui")
    .WithExternalHttpEndpoints()
    .WithEnvironment("ProductSearchUi__ApiBaseUrl", api.GetEndpoint("http"))
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
