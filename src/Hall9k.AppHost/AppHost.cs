IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres = builder.AddPostgres("postgres")
    .WithDataVolume("hall9k-pgdata")
    .WithLifetime(ContainerLifetime.Persistent);

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("hall9k");

builder.AddProject<Projects.Hall9k_Daemon>("h9kd")
    .WithReference(database)
    .WaitFor(database);

builder.Build().Run();
