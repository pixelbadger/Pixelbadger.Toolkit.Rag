using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Sql;

// Local run (`dotnet run --project Pixelbadger.Toolkit.Rag.AppHost` / `aspire run`):
//   SQL Server 2025 in a persistent container, the app as a .NET project (debuggable; ffmpeg on PATH for audio),
//   the model from a local directory (parameter "model-path") and the Lucene index in the project's ./index.
// Publish (`azd up` / `aspire deploy`):
//   Azure SQL Database (Entra ID auth through the app's managed identity) and the Dockerfile image on Azure Container
//   Apps (Consumption profile, 4 vCPU / 8 GiB, exactly one replica), with one Azure Files share at /data holding the
//   Lucene index (/data/index) and the model (/data/models/embeddinggemma-2-onnx, uploaded once with
//   scripts/upload-model.sh).
var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("env");

var sql = builder.AddAzureSqlServer("sql")
    .RunAsContainer(container => container
        .WithImageTag("2025-latest") // the vector type needs SQL Server 2025
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent));

// General Purpose serverless, up to 2 vCores. Not the free offer (Aspire's default): the ingest worker polls the queue
// every few seconds, which would use up the free monthly vCore allowance and pause the database. The same polling
// keeps a serverless database from auto-pausing, so expect to pay for at least the minimum capacity.
var db = sql.AddDatabase("ragdb").WithDefaultAzureSku();
sql.ConfigureInfrastructure(infra =>
{
    foreach (var database in infra.GetProvisionableResources().OfType<SqlDatabase>())
    {
        database.Sku = new SqlSku { Name = "GP_S_Gen5_2" };
        database.MinCapacity = 0.5;
    }
});

if (builder.ExecutionContext.IsPublishMode)
{
    // The Dockerfile image (includes ffmpeg; Rag__IndexPath defaults to /data/index). The /data volume becomes an
    // Azure Files share. One share holds both the index and the model: with two volumes on one container, Aspire
    // 13.6 gives both environment storage definitions the same (truncated) name, so they would collide anyway.
    builder.AddDockerfile("rag", "..")
        .WithHttpEndpoint(targetPort: 8080)
        .WithExternalHttpEndpoints()
        .WithVolume("pbrag-data", "/data")
        .WithEnvironment("Rag__ModelPath", "/data/models/embeddinggemma-2-onnx")
        .WithRagDefaults(db)
        .PublishAsAzureContainerApp((_, app) =>
        {
            // One instance owns the Lucene index and the ingest queue: never scale out, never scale to zero
            // (the ingest worker runs in the background).
            app.Template.Scale.MinReplicas = 1;
            app.Template.Scale.MaxReplicas = 1;

            // The largest size of the Consumption workload profile.
            var container = app.Template.Containers.Single().Value!;
            container.Resources.Cpu = 4.0;
            container.Resources.Memory = "8Gi";

            // The image runs as the non-root "app" user (uid/gid 1654): own the SMB mount as that user. nobrl keeps
            // Lucene's lock file on client-side locks (one instance, so nothing else contends for it).
            foreach (var volume in app.Template.Volumes)
                volume.Value!.MountOptions = "uid=1654,gid=1654,dir_mode=0770,file_mode=0660,nobrl";
        });
}
else
{
    // Local copy of onnx-community/embeddinggemma-2-ONNX (set Parameters:model-path in user secrets, or enter it
    // in the dashboard when prompted).
    var modelPath = builder.AddParameter("model-path");

    builder.AddProject<Projects.Pixelbadger_Toolkit_Rag>("rag")
        .WithEnvironment("Rag__ModelPath", modelPath)
        .WithRagDefaults(db);
}

builder.Build().Run();

internal static class RagResourceExtensions
{
    /// <summary>Wiring shared by the local project and the published container.</summary>
    public static IResourceBuilder<T> WithRagDefaults<T>(this IResourceBuilder<T> app, IResourceBuilder<AzureSqlDatabaseResource> db)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport, IResourceWithEndpoints =>
        app
            // WithReference also grants the app's managed identity access to the database in Azure.
            .WithReference(db)
            .WithEnvironment("Rag__ConnectionString", db)
            .WaitFor(db)
            .WithHttpHealthCheck("/health");
}
