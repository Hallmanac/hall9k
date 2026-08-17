IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Fixed port + password so the h9k CLI's default connection string works against the
// dev-loop Postgres exactly as it does against the docker-compose one (installed mode).
IResourceBuilder<ParameterResource> postgresPassword =
    builder.AddParameter("postgres-password", value: "hall9k", secret: true);

IResourceBuilder<PostgresServerResource> postgres = builder.AddPostgres("postgres", password: postgresPassword)
    .WithDataVolume("hall9k-pgdata")
    .WithHostPort(5432)
    .WithLifetime(ContainerLifetime.Persistent);

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("hall9k");

builder.AddProject<Projects.Hall9k_Daemon>("h9kd")
    .WithReference(database)
    .WaitFor(database);

builder.Build().Run();
