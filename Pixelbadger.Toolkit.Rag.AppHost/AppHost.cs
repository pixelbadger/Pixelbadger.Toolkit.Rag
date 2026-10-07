using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Sql;
using Azure.Provisioning.Storage;

// Local run (`dotnet run --project Pixelbadger.Toolkit.Rag.AppHost` / `aspire run`):
//   SQL Server 2025 in a persistent container, the app as a .NET project (debuggable; ffmpeg on PATH for audio),
//   the model from a local directory (parameter "model-path") and the Lucene index in the project's ./index.
// Publish (`azd up` / `aspire deploy`):
//   Azure SQL Database (Entra ID auth through the app's managed identity) and the Dockerfile image on Azure Container
//   Apps (Consumption profile, 4 vCPU / 8 GiB, scale to zero, at most one replica), with one Azure Files share at
//   /data holding the Lucene index (/data/index) and the model (/data/models/embeddinggemma-2-onnx, uploaded once with
//   scripts/upload-model.sh). A request wakes the app; a storage-queue scale rule keeps it running while the ingest
//   worker has work (the worker keeps a marker message in the "ingest-active" queue until the job queue is empty).
// Must match QueueIngestKeepAlive.ConnectionName in the app (the connection name and the queue name).
const string IngestActiveQueue = "ingest-active";

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("env");

var sql = builder.AddAzureSqlServer("sql")
    .RunAsContainer(container => container
        .WithImageTag("2025-latest") // the vector type needs SQL Server 2025
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent));

// The Azure SQL free offer (Aspire's default: General Purpose serverless, auto-pause). The app scales to zero, so the
// worker only polls while it is awake and the database pauses in between. Past the monthly free allowance the
// database keeps running and bills the overage instead of pausing until the next month.
var db = sql.AddDatabase("ragdb");
sql.ConfigureInfrastructure(infra =>
{
    foreach (var database in infra.GetProvisionableResources().OfType<SqlDatabase>())
        database.FreeLimitExhaustionBehavior = FreeLimitExhaustionBehavior.BillOverUsage;
});

// Marker queue for the ingest keep-alive (Azurite locally; only the scale rule in Azure reads it).
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator()
    .ConfigureInfrastructure(infra =>
    {
        // A few marker messages: locally redundant is plenty.
        foreach (var account in infra.GetProvisionableResources().OfType<StorageAccount>())
            account.Sku = new StorageSku { Name = StorageSkuName.StandardLrs };
    });
var ingestActive = storage.AddQueue(IngestActiveQueue);

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
        .WithRagDefaults(db, storage, ingestActive)
        .PublishAsAzureContainerApp((infra, app) =>
        {
            // One instance owns the Lucene index and the ingest queue: never scale out. Scale to zero when idle:
            // an HTTP request wakes the app, and the queue rule keeps it up while the worker's marker is there.
            app.Template.Scale.MinReplicas = 0;
            app.Template.Scale.MaxReplicas = 1;
            var identityId = infra.GetProvisionableResources().OfType<ProvisioningParameter>()
                .Single(p => p.BicepIdentifier == "rag_identity_outputs_id");
            app.Template.Scale.Rules.Add(new ContainerAppScaleRule
            {
                Name = "http",
                Http = new ContainerAppHttpScaleRule { Metadata = { ["concurrentRequests"] = "10" } }
            });
            app.Template.Scale.Rules.Add(new ContainerAppScaleRule
            {
                Name = "ingest-active",
                AzureQueue = new ContainerAppQueueScaleRule
                {
                    AccountName = storage.Resource.NameOutputReference.AsProvisioningParameter(infra),
                    QueueName = IngestActiveQueue,
                    QueueLength = 1,
                    // The app's managed identity (WithReference grants it queue access).
                    Identity = identityId
                }
            });

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
        .WithRagDefaults(db, storage, ingestActive);
}

builder.Build().Run();

internal static class RagResourceExtensions
{
    /// <summary>Wiring shared by the local project and the published container.</summary>
    public static IResourceBuilder<T> WithRagDefaults<T>(
        this IResourceBuilder<T> app, IResourceBuilder<AzureSqlDatabaseResource> db,
        IResourceBuilder<AzureStorageResource> storage, IResourceBuilder<AzureQueueStorageQueueResource> ingestActive)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport, IResourceWithEndpoints =>
        app
            // WithReference also grants the app's managed identity access to the database / queue in Azure.
            .WithReference(db)
            .WithEnvironment("Rag__ConnectionString", db)
            .WaitFor(db)
            .WithReference(ingestActive)
            // Queue access only (WithReference alone would also grant blob and table data roles).
            .WithRoleAssignments(storage, StorageBuiltInRole.StorageQueueDataContributor)
            .WaitFor(ingestActive)
            .WithHttpHealthCheck("/health");
}
