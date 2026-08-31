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
