using FluentAssertions;
using Microsoft.Data.SqlClient;
using Pixelbadger.Toolkit.Rag.Domain;
using Pixelbadger.Toolkit.Rag.Ingestion;
using Pixelbadger.Toolkit.Rag.Persistence;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Persistence;

[Collection("SqlServer")]
public class SqlDocumentStoreTests(SqlServerFixture sql)
{
    private static SqlDocumentStore NewStore(string cs, VectorSearchMode mode = VectorSearchMode.Auto)
        => new(new SqlStoreOptions { ConnectionString = cs, SearchMode = mode });

    private async Task<(SqlDocumentStore Store, string ConnectionString)> CreateMigratedStoreAsync()
    {
        var cs = await sql.CreateDatabaseAsync();
        var store = NewStore(cs);
        await store.MigrateAsync();
        return (store, cs);
    }

    private static DocumentDraft Doc(string name, Modality modality = Modality.Text, string? hash = null)
        => new("data/" + name + ".txt", name + ".txt", modality, hash ?? new string('a', 64));

    /// <summary>What the upload endpoint does before the worker ingests: creates the (Queued) document row and its job.</summary>
    private static async Task<Guid> NewDocAsync(string cs, string name = "doc")
    {
        var queue = new SqlIngestQueue(new SqlStoreOptions { ConnectionString = cs }, new IngestSettings());
        var upload = new IngestUpload("data/" + name + ".txt", 1, () => new MemoryStream([1]));
        return (await queue.EnqueueNewDocumentsAsync([upload], 1000)).Single().DocumentId;
    }

    private static ChunkDraft Chunk(int ordinal, string seed, string? text = null)
        => new(ordinal, Modality.Text, ordinal * 100L, ordinal * 100L + 99, text ?? "text " + seed, MockEmbeddingService.Vector(seed));

    private static List<ChunkDraft> Chunks(string prefix, int count)
        => Enumerable.Range(1, count).Select(i => Chunk(i, $"{prefix}-{i}")).ToList();

    private static async Task<T> ScalarAsync<T>(string cs, string query)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(query, conn);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    private static Task<int> VectorIndexCountAsync(string cs)
        => ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.vector_indexes WHERE object_id = OBJECT_ID(N'dbo.Chunks_EG2_256')");

    /// <summary>Ingests <paramref name="documents"/> x <paramref name="chunksPerDoc"/> deterministic chunks; returns name to document id.</summary>
    private static async Task<Dictionary<string, Guid>> PopulateAsync(
        SqlDocumentStore store, string cs, int documents, int chunksPerDoc, string prefix = "p")
    {
        var ids = new Dictionary<string, Guid>();
        for (int d = 0; d < documents; d++)
        {
            var name = $"{prefix}{d}";
            ids[name] = await NewDocAsync(cs, name);
            await store.ReplaceDocumentAsync(ids[name], Doc(name), Chunks(name, chunksPerDoc));
        }

        return ids;
    }

    [Fact]
    public async Task Migrate_CreatesSchema_AndIsIdempotent()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        await store.MigrateAsync();

        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.tables WHERE name IN ('Documents','Chunks_EG2_256')")).Should().Be(2);
        (await ScalarAsync<string>(cs, "SELECT TYPE_NAME(user_type_id) + CAST(max_length AS varchar) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Documents') AND name = 'IndexStatus'"))
            .Should().Be("tinyint1");
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Chunks_EG2_256') AND name = 'Embedding' AND TYPE_NAME(user_type_id) = 'vector'"))
            .Should().Be(1);
        // Migrations must not create the vector index (100-row minimum).
        (await VectorIndexCountAsync(cs)).Should().Be(0);
    }

    [Fact]
    public async Task Migrate_CreatesMissingDatabase()
    {
        var seed = await sql.CreateDatabaseAsync();
        var name = "pbrag_test_mig_" + Guid.NewGuid().ToString("N")[..8];
        var builder = new SqlConnectionStringBuilder(seed) { InitialCatalog = name };
        try
        {
            await NewStore(builder.ConnectionString).MigrateAsync();
            var master = new SqlConnectionStringBuilder(seed) { InitialCatalog = "master" }.ConnectionString;
            (await ScalarAsync<int>(master, $"SELECT COUNT(*) FROM sys.databases WHERE name = '{name}'")).Should().Be(1);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            var master = new SqlConnectionStringBuilder(seed) { InitialCatalog = "master" }.ConnectionString;
            await using var conn = new SqlConnection(master);
            await conn.OpenAsync();
            await using var drop = new SqlCommand($"IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", conn);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Replace_InsertsChunksInOrdinalOrder_WithUniqueIds()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var id = await NewDocAsync(cs, "alpha");
        var draft = Doc("alpha");
        // Deliberately out of order.
        var chunks = new List<ChunkDraft> { Chunk(3, "c"), Chunk(1, "a"), Chunk(2, "b") };

        var records = await store.ReplaceDocumentAsync(id, draft, chunks);

        records.Select(r => r.Ordinal).Should().Equal(1, 2, 3);
        records.Select(r => r.ChunkId).Should().OnlyHaveUniqueItems().And.OnlyContain(i => i > 0);
        records.Select(r => r.ChunkGlobalId).Should().OnlyHaveUniqueItems().And.NotContain(Guid.Empty);
        records.Should().OnlyContain(r => r.DocumentGlobalId == id && r.SourcePath == draft.SourcePath);
        records[0].Text.Should().Be("text a");
        records[0].LocatorStart.Should().Be(100);
        records[0].LocatorEnd.Should().Be(199);
        records[0].Modality.Should().Be(Modality.Text);
    }

    [Fact]
    public async Task Replace_UpdatesTheExistingDocument_SetsIndexedAndTheHash()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var id = await NewDocAsync(cs, "alpha");
        (await ScalarAsync<int>(cs, "SELECT IndexStatus FROM dbo.Documents")).Should().Be((int)IndexStatus.Queued);

        await store.ReplaceDocumentAsync(id, Doc("alpha"), Chunks("a", 1));

        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents")).Should().Be(1);
        (await ScalarAsync<int>(cs, "SELECT IndexStatus FROM dbo.Documents")).Should().Be((int)IndexStatus.Indexed);
        (await ScalarAsync<string>(cs, "SELECT ContentHash FROM dbo.Documents")).Should().Be(new string('a', 64));
    }

    [Fact]
    public async Task Replace_IntoAMissingDocument_Throws_AndWritesNothing()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var unknown = Guid.NewGuid();

        var act = () => store.ReplaceDocumentAsync(unknown, Doc("ghost"), Chunks("g", 2));

        (await act.Should().ThrowAsync<DocumentNotFoundException>()).Which.DocumentId.Should().Be(unknown);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents")).Should().Be(0);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(0);
    }

    [Fact]
    public async Task Replace_AfterTheDocumentWasDeleted_DoesNotResurrectIt()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var id = await NewDocAsync(cs, "alpha");
        await store.ReplaceDocumentAsync(id, Doc("alpha"), Chunks("v1", 2));
        await store.DeleteDocumentAsync(id);

        var act = () => store.ReplaceDocumentAsync(id, Doc("alpha"), Chunks("v2", 2));

        await act.Should().ThrowAsync<DocumentNotFoundException>();
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents")).Should().Be(0);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(0);
    }

    [Fact]
    public async Task Replace_Reingest_ReplacesChunks_KeepsDocumentRow()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var id = await NewDocAsync(cs, "alpha");
        var draft = Doc("alpha");

        var first = await store.ReplaceDocumentAsync(id, draft, Chunks("v1", 3));
        var second = await store.ReplaceDocumentAsync(id, draft with { Title = "renamed", ContentHash = new string('b', 64) }, Chunks("v2", 2));

        second.Should().HaveCount(2);
        second.Select(r => r.DocumentId).Distinct().Should().Equal(first[0].DocumentId); // stable document row
        second.Select(r => r.ChunkId).Should().NotIntersectWith(first.Select(r => r.ChunkId));
        second.Select(r => r.ChunkGlobalId).Should().NotIntersectWith(first.Select(r => r.ChunkGlobalId));
        second.Select(r => r.Title).Should().OnlyContain(t => t == "renamed");

        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents")).Should().Be(1);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(2);
        (await ScalarAsync<string>(cs, "SELECT ContentHash FROM dbo.Documents")).Should().Be(new string('b', 64));
        (await store.GetChunksAsync(first.Select(r => r.ChunkId).ToList())).Should().BeEmpty();
    }

    [Fact]
    public async Task Replace_WithNoChunks_RemovesOldChunks()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var id = await NewDocAsync(cs, "alpha");
        await store.ReplaceDocumentAsync(id, Doc("alpha"), Chunks("a", 3));
        await store.ReplaceDocumentAsync(id, Doc("alpha"), []);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(0);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents")).Should().Be(1);
    }

    [Fact]
    public async Task Replace_RejectsWrongEmbeddingSize_AndLeavesStoreUntouched()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var id = await NewDocAsync(cs, "alpha");
        var bad = new List<ChunkDraft> { new(1, Modality.Text, null, null, "x", new float[10]) };

        var act = () => store.ReplaceDocumentAsync(id, Doc("alpha"), bad);

        await act.Should().ThrowAsync<ArgumentException>();
        (await ScalarAsync<int>(cs, "SELECT IndexStatus FROM dbo.Documents")).Should().Be((int)IndexStatus.Queued);
    }

    [Fact]
    public async Task Replace_IsAtomic_FailureRollsBackDeletedChunks()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var id = await NewDocAsync(cs, "alpha");
        var draft = Doc("alpha");
        await store.ReplaceDocumentAsync(id, draft, Chunks("v1", 3));

        // Title is nvarchar(1000): an oversized value fails the document update inside the transaction.
        var act = () => store.ReplaceDocumentAsync(id, draft with { Title = new string('x', 1200) }, Chunks("v2", 2));
        await act.Should().ThrowAsync<Exception>();

        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(3);
        (await ScalarAsync<string>(cs, "SELECT Title FROM dbo.Documents")).Should().Be("alpha.txt");
    }

    [Fact]
    public async Task DeleteDocument_RemovesChunksAndJobs_LeavesOthers_AndReportsUnknownIds()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var alpha = await NewDocAsync(cs, "alpha");
        var beta = await NewDocAsync(cs, "beta");
        await store.ReplaceDocumentAsync(alpha, Doc("alpha"), Chunks("a", 3));
        await store.ReplaceDocumentAsync(beta, Doc("beta"), Chunks("b", 2));

        (await store.DeleteDocumentAsync(alpha)).Should().BeTrue();

        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(2);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents")).Should().Be(1);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.IngestJobs")).Should().Be(1);
        (await store.DeleteDocumentAsync(alpha)).Should().BeFalse();
        (await store.DeleteDocumentAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task DeletingDocument_ByRawSql_CascadesToChunks()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var alpha = await NewDocAsync(cs, "alpha");
        var beta = await NewDocAsync(cs, "beta");
        await store.ReplaceDocumentAsync(alpha, Doc("alpha"), Chunks("a", 3));
        await store.ReplaceDocumentAsync(beta, Doc("beta"), Chunks("b", 2));

        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var del = new SqlCommand("DELETE FROM dbo.IngestJobs; DELETE FROM dbo.Documents WHERE GlobalId = @g", conn);
            del.Parameters.AddWithValue("@g", alpha);
            await del.ExecuteNonQueryAsync();
        }

        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(2);
    }

    [Fact]
    public async Task GetChunks_HydratesRecords_AndOmitsMissingIds()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var alphaId = await NewDocAsync(cs, "alpha");
        var picId = await NewDocAsync(cs, "pic");
        var written = await store.ReplaceDocumentAsync(alphaId, Doc("alpha", Modality.Text), Chunks("a", 3));
        var image = await store.ReplaceDocumentAsync(picId, Doc("pic", Modality.Image),
            [new ChunkDraft(1, Modality.Image, null, null, null, MockEmbeddingService.Vector("img"))]);

        var ids = written.Select(r => r.ChunkId).Append(image[0].ChunkId).Append(int.MaxValue).ToList();
        var hydrated = await store.GetChunksAsync(ids);

        hydrated.Should().HaveCount(4);
        hydrated.Where(h => h.DocumentGlobalId == alphaId).Should().BeEquivalentTo(written);
        var pic = hydrated.Single(h => h.DocumentGlobalId == picId);
        pic.Modality.Should().Be(Modality.Image);
        pic.Text.Should().BeNull();
        pic.LocatorStart.Should().BeNull();
        pic.Title.Should().Be("pic.txt");

        (await store.GetChunksAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task ExactSearch_ReturnsNearestFirst_WithCosineDistance()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var ids = await PopulateAsync(store, cs, documents: 5, chunksPerDoc: 4); // below 100 rows: exact path

        var target = Chunk(1, "p3-2");
        var hits = await store.SearchAsync(target.Embedding, 5, null);

        hits.Should().HaveCount(5);
        hits.Select(h => h.Distance).Should().BeInAscendingOrder();
        hits[0].Distance.Should().BeApproximately(0f, 1e-4f);
        store.LastSearchPath.Should().Be(SqlDocumentStore.SearchPath.Exact);

        var top = (await store.GetChunksAsync([hits[0].ChunkId])).Single();
        top.DocumentGlobalId.Should().Be(ids["p3"]);
        top.Ordinal.Should().Be(2);

        // Opposite vector -> cosine distance ~2 for the same chunk.
        var opposite = target.Embedding.Select(x => -x).ToArray();
        var all = await store.SearchAsync(opposite, 20, null);
        all.Should().HaveCount(20);
        all[^1].ChunkId.Should().Be(hits[0].ChunkId);
        all[^1].Distance.Should().BeApproximately(2f, 1e-3f);
    }

    [Fact]
    public async Task Search_FewerRowsThanMaxResults_ReturnsAll_AndEmptyStoreReturnsNothing()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        (await store.SearchAsync(MockEmbeddingService.Vector("q"), 5, null)).Should().BeEmpty();

        await store.ReplaceDocumentAsync(await NewDocAsync(cs, "alpha"), Doc("alpha"), Chunks("a", 2));
        (await store.SearchAsync(MockEmbeddingService.Vector("q"), 5, null)).Should().HaveCount(2);
        (await store.SearchAsync(MockEmbeddingService.Vector("q"), 0, null)).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_RejectsWrongDimensions()
    {
        var (store, _) = await CreateMigratedStoreAsync();
        var act = () => store.SearchAsync(new float[3], 5, null);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Search_DocumentIdFilter_RestrictsResults()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var ids = await PopulateAsync(store, cs, documents: 4, chunksPerDoc: 3);
        var query = MockEmbeddingService.Vector("p0-1"); // best match lives in p0

        var unfiltered = await store.SearchAsync(query, 3, null);
        (await store.GetChunksAsync([unfiltered[0].ChunkId])).Single().DocumentGlobalId.Should().Be(ids["p0"]);

        var filtered = await store.SearchAsync(query, 10, [ids["p1"], ids["p2"]]);
        filtered.Should().HaveCount(6);
        var documents = (await store.GetChunksAsync(filtered.Select(h => h.ChunkId).ToList())).Select(c => c.DocumentGlobalId).Distinct();
        documents.Should().BeEquivalentTo([ids["p1"], ids["p2"]]);

        (await store.SearchAsync(query, 10, [Guid.NewGuid()])).Should().BeEmpty();
        (await store.SearchAsync(query, 10, [])).Should().HaveCount(10); // empty filter = no filter
    }

    [Fact]
    public async Task EnsureVectorIndex_BelowThreshold_IsNoOp_AtThreshold_Creates_AndIsIdempotent()
    {
        var (store, cs) = await CreateMigratedStoreAsync();

        await store.EnsureVectorIndexAsync(); // empty table: must not throw
        (await VectorIndexCountAsync(cs)).Should().Be(0);

        await PopulateAsync(store, cs, documents: 33, chunksPerDoc: 3); // 99 rows
        await store.EnsureVectorIndexAsync();
        (await VectorIndexCountAsync(cs)).Should().Be(0);

        await store.ReplaceDocumentAsync(await NewDocAsync(cs, "extra"), Doc("extra"), Chunks("extra", 1)); // 100 rows
        await store.EnsureVectorIndexAsync();
        store.LastVectorIndexError.Should().BeNull();
        (await VectorIndexCountAsync(cs)).Should().Be(1);

        await store.EnsureVectorIndexAsync(); // idempotent
        await NewStore(cs).EnsureVectorIndexAsync();
        (await VectorIndexCountAsync(cs)).Should().Be(1);
    }

    [Fact]
    public async Task EnsureVectorIndex_EnablesPreviewFeatures_WhenOff()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        await PopulateAsync(store, cs, documents: 10, chunksPerDoc: 10);
        await using (var conn = new SqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var off = new SqlCommand("ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = OFF", conn);
            await off.ExecuteNonQueryAsync();
        }

        // With preview off the engine does not even parse VECTOR_SEARCH / CREATE VECTOR INDEX on SQL Server 2025.
        await store.EnsureVectorIndexAsync();

        store.LastVectorIndexError.Should().BeNull();
        (await VectorIndexCountAsync(cs)).Should().Be(1);
    }

    [Fact]
    public async Task ExactOnlyMode_NeverCreatesIndex_AndNeverUsesApproximateSearch()
    {
        var cs = await sql.CreateDatabaseAsync();
        var store = NewStore(cs, VectorSearchMode.ExactOnly);
        await store.MigrateAsync();
        await PopulateAsync(store, cs, documents: 12, chunksPerDoc: 10);

        await store.EnsureVectorIndexAsync();
        (await VectorIndexCountAsync(cs)).Should().Be(0);

        await store.SearchAsync(MockEmbeddingService.Vector("q"), 5, null);
        store.LastSearchPath.Should().Be(SqlDocumentStore.SearchPath.Exact);
    }

    [Fact]
    public async Task ApproximateSearch_AgreesWithExact_AtThreeHundredRows()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        await PopulateAsync(store, cs, documents: 30, chunksPerDoc: 10); // 300 rows
        await store.EnsureVectorIndexAsync();
        store.LastVectorIndexError.Should().BeNull();
        (await VectorIndexCountAsync(cs)).Should().Be(1);

        var exact = NewStore(cs, VectorSearchMode.ExactOnly);
        const int k = 10;
        const int queries = 25;
        int overlap = 0;
        foreach (var q in Enumerable.Range(0, queries))
        {
            var query = MockEmbeddingService.Vector("query-" + q);
            var approx = await store.SearchAsync(query, k, null);
            store.LastSearchPath.Should().NotBe(SqlDocumentStore.SearchPath.Exact, "the vector index exists, so the approximate path must be used");
            var truth = await exact.SearchAsync(query, k, null);

            approx.Should().HaveCount(k);
            approx.Select(h => h.Distance).Should().BeInAscendingOrder();
            overlap += approx.Select(h => h.ChunkId).Intersect(truth.Select(h => h.ChunkId)).Count();
        }

        var recall = overlap / (double)(k * queries);
        recall.Should().BeGreaterThanOrEqualTo(0.8, "DiskANN recall@10 over 300 random 256-d vectors should be high");

        // A stored vector queried verbatim is found at distance ~0.
        var self = MockEmbeddingService.Vector("p7-4");
        var hit = (await store.SearchAsync(self, 1, null)).Single();
        hit.Distance.Should().BeApproximately(0f, 1e-3f);
    }

    [Fact]
    public async Task ApproximateSearch_WithDocumentFilter_ReturnsOnlyMatchingDocuments_EvenWhenSelective()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var ids = await PopulateAsync(store, cs, documents: 30, chunksPerDoc: 10);
        await store.EnsureVectorIndexAsync();
        var query = MockEmbeddingService.Vector("filter-query");

        var filtered = await store.SearchAsync(query, 5, [ids["p3"], ids["p9"]]);
        filtered.Should().HaveCount(5);
        (await store.GetChunksAsync(filtered.Select(h => h.ChunkId).ToList())).Select(c => c.DocumentGlobalId)
            .Should().OnlyContain(d => d == ids["p3"] || d == ids["p9"]);

        // Fewer matching rows than requested: must fall back to exact and return all of them.
        var all = await store.SearchAsync(query, 50, [ids["p3"]]);
        all.Should().HaveCount(10);

        var truth = await NewStore(cs, VectorSearchMode.ExactOnly).SearchAsync(query, 10, [ids["p3"]]);
        all.Select(h => h.ChunkId).Should().BeEquivalentTo(truth.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task Replace_WithVectorIndexPresent_StillWorks_AndSearchRemainsCorrect()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var ids = await PopulateAsync(store, cs, documents: 12, chunksPerDoc: 10);
        await store.EnsureVectorIndexAsync();
        (await VectorIndexCountAsync(cs)).Should().Be(1);

        // On SQL Server 2025 RTM-CU9 the pre-v3 index makes the table read-only; the store must cope
        // (dropping the index first), and on v3-capable servers it must keep the index.
        var replacement = Chunk(1, "fresh-vector");
        await store.ReplaceDocumentAsync(ids["p4"], Doc("p4"), [replacement]);

        var hits = await store.SearchAsync(replacement.Embedding, 3, null);
        hits[0].Distance.Should().BeApproximately(0f, 1e-3f);
        (await store.GetChunksAsync([hits[0].ChunkId])).Single().DocumentGlobalId.Should().Be(ids["p4"]);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(111);

        // The ingester calls Ensure after each batch: the index comes back.
        await store.EnsureVectorIndexAsync();
        (await VectorIndexCountAsync(cs)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteDocument_WithVectorIndexPresent_StillWorks()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var ids = await PopulateAsync(store, cs, documents: 12, chunksPerDoc: 10);
        await store.EnsureVectorIndexAsync();
        (await VectorIndexCountAsync(cs)).Should().Be(1);

        // A pre-v3 index makes the chunk table read-only (deletes fail too); the store drops it first.
        (await store.DeleteDocumentAsync(ids["p2"])).Should().BeTrue();

        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(110);
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Documents")).Should().Be(11);
        await store.EnsureVectorIndexAsync();
        (await VectorIndexCountAsync(cs)).Should().Be(1);
    }

    [Fact]
    public async Task SearchPath_ChosenByAnotherInstance_SeesExistingIndex()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        await PopulateAsync(store, cs, documents: 12, chunksPerDoc: 10);
        await store.EnsureVectorIndexAsync();

        var fresh = NewStore(cs);
        await fresh.SearchAsync(MockEmbeddingService.Vector("x"), 3, null);
        fresh.LastSearchPath.Should().NotBe(SqlDocumentStore.SearchPath.Exact);
    }

    // ---- canonical source content ----

    [SkippableFact]
    public async Task Replace_WithSourceContent_PromotesItInTheSameWriteAsTheChunks()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var id = await NewDocAsync(cs);
        byte[] bytes = [0, 1, 2, 0xFF];

        await store.ReplaceDocumentAsync(id, Doc("doc") with { SourceContent = bytes, ContentType = "text/plain" }, Chunks("c", 2));

        var content = (await store.GetContentAsync(id))!;
        content.Bytes.Should().Equal(bytes);
        content.ContentType.Should().Be("text/plain");
        content.FileName.Should().Be("doc.txt");
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(2);
    }

    [SkippableFact]
    public async Task Replace_WithoutSourceContent_KeepsTheStoredSource()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var id = await NewDocAsync(cs);
        await store.ReplaceDocumentAsync(id, Doc("doc") with { SourceContent = [1, 2, 3], ContentType = "text/plain" }, Chunks("a", 1));

        await store.ReplaceDocumentAsync(id, Doc("doc"), Chunks("b", 1));

        (await store.GetContentAsync(id))!.Bytes.Should().Equal(1, 2, 3);
    }

    [SkippableFact]
    public async Task Replace_OfAMissingDocument_ThrowsAndStoresNothing()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var ghost = Guid.NewGuid();

        var act = async () => await store.ReplaceDocumentAsync(ghost, Doc("ghost") with { SourceContent = [9] }, Chunks("g", 1));

        await act.Should().ThrowAsync<DocumentNotFoundException>();
        (await store.GetContentAsync(ghost)).Should().BeNull();
        (await ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.Chunks_EG2_256")).Should().Be(0);
    }

    [SkippableFact]
    public async Task GetContent_FallsBackToTheExtensionWhenNoContentTypeIsStored()
    {
        var (store, cs) = await CreateMigratedStoreAsync();
        var id = await NewDocAsync(cs);
        await store.ReplaceDocumentAsync(id, Doc("doc") with { SourceContent = [1] }, Chunks("a", 1));

        (await store.GetContentAsync(id))!.ContentType.Should().Be("text/plain");
    }
}
