var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.PDDM_Api>("pddm-api")
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.PDDM_UI>("pddm-ui")
    .WithExternalHttpEndpoints()
    .WithEnvironment("PddmUi__ApiBaseUrl", api.GetEndpoint("http"))
    .WithReference(api);

builder.Build().Run();
