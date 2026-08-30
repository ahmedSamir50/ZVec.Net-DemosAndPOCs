var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg16");
var db = postgres.AddDatabase("productsearch");

var api = builder.AddProject<Projects.ProductSearch_Api>("productsearch-api")
    .WithReference(db)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.ProductSearch_UI>("productsearch-ui")
    .WithExternalHttpEndpoints()
    .WithEnvironment("ProductSearchUi__ApiBaseUrl", api.GetEndpoint("http"))
    .WithReference(api);

builder.Build().Run();
