IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Fixed port + password so the h9k CLI's default connection string works against the
// dev-loop Postgres exactly as it does against the docker-compose one (installed mode).
IResourceBuilder<ParameterResource> postgresPassword =
    builder.AddParameter("postgres-password", value: "hall9k", secret: true);

// Named distinctly from PostgresRuntime.VolumeName ("hall9k-pgdata") — that literal string is
// pinned in the installed-mode compose file specifically so h9k uninstall --purge-data can name
// it without guessing, and Aspire's own volume naming carries no project prefix to keep the two
// apart on its own, so the two provisioning paths need different literal names instead.
IResourceBuilder<PostgresServerResource> postgres = builder.AddPostgres("postgres", password: postgresPassword)
    .WithDataVolume("hall9k-dev-pgdata")
    .WithHostPort(5432)
    .WithLifetime(ContainerLifetime.Persistent);

IResourceBuilder<PostgresDatabaseResource> database = postgres.AddDatabase("hall9k");

builder.AddProject<Projects.Hall9k_Daemon>("h9kd")
    .WithReference(database)
    .WaitFor(database);

builder.Build().Run();
