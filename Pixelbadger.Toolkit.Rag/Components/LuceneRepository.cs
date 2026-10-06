using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace Pixelbadger.Toolkit.Rag.Components;

/// <summary>
/// Lucene.NET BM25 index keyed by SQL chunk id. Only text chunks are indexed; the indexed
/// <c>content</c> field is not stored (content is hydrated from SQL).
/// </summary>
public class LuceneRepository : ILuceneRepository
{
    private const LuceneVersion LUCENE_VERSION = LuceneVersion.LUCENE_48;

    internal const string ChunkIdField = "chunk_id";
    internal const string DocumentIdField = "document_id";
    internal const string SourceIdField = "source_id";
    internal const string ContentField = "content";

    /// <inheritdoc />
    public Task ReplaceDocumentAsync(
        string indexPath,
        string documentGlobalId,
        string sourceId,
        IReadOnlyList<LuceneChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        System.IO.Directory.CreateDirectory(indexPath);

        // The IndexWriter holds the index write lock: open, commit and close per call
        // (one call per document, never per chunk).
        using var indexDirectory = FSDirectory.Open(indexPath);
        using var analyzer = new StandardAnalyzer(LUCENE_VERSION);
        var config = new IndexWriterConfig(LUCENE_VERSION, analyzer)
        {
            // Consistent BM25 similarity for both indexing and searching
            Similarity = new BM25Similarity()
        };

        using var writer = new IndexWriter(indexDirectory, config);

        writer.DeleteDocuments(new Term(DocumentIdField, documentGlobalId));

        foreach (var chunk in chunks)
        {
            var doc = new Document
            {
                new Int32Field(ChunkIdField, chunk.ChunkId, Field.Store.YES),
                new StringField(DocumentIdField, documentGlobalId, Field.Store.YES),
                new StringField(SourceIdField, sourceId, Field.Store.YES),
                new TextField(ContentField, chunk.Text, Field.Store.NO)
            };
            writer.AddDocument(doc);
        }

        writer.Commit();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KeywordHit>> SearchAsync(
        string indexPath,
        string queryText,
        int maxResults,
        IReadOnlyCollection<string>? sourceIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<KeywordHit> empty = Array.Empty<KeywordHit>();

        // A corpus may be media-only, in which case no Lucene index has been created.
        if (maxResults < 1 || string.IsNullOrWhiteSpace(queryText) || !System.IO.Directory.Exists(indexPath))
        {
            return Task.FromResult(empty);
        }

        using var indexDirectory = FSDirectory.Open(indexPath);
        if (!DirectoryReader.IndexExists(indexDirectory))
        {
            return Task.FromResult(empty);
        }

        using var analyzer = new StandardAnalyzer(LUCENE_VERSION);
        using var reader = DirectoryReader.Open(indexDirectory);
        var searcher = new IndexSearcher(reader)
        {
            Similarity = new BM25Similarity()
        };

        var contentQuery = ParseContentQuery(analyzer, queryText);

        Query finalQuery = contentQuery;
        if (sourceIds is { Count: > 0 })
        {
            var sourceIdQuery = new BooleanQuery();
            foreach (var sourceId in sourceIds)
            {
                sourceIdQuery.Add(new TermQuery(new Term(SourceIdField, sourceId)), Occur.SHOULD);
            }

            var boolQuery = new BooleanQuery();
            boolQuery.Add(contentQuery, Occur.MUST);
            boolQuery.Add(sourceIdQuery, Occur.MUST);
            finalQuery = boolQuery;
        }

        var hits = searcher.Search(finalQuery, maxResults);
        var results = new List<KeywordHit>(hits.ScoreDocs.Length);
        foreach (var scoreDoc in hits.ScoreDocs)
        {
            var doc = searcher.Doc(scoreDoc.Doc);
            var chunkId = doc.GetField(ChunkIdField)?.GetInt32Value();
            if (chunkId.HasValue)
            {
                results.Add(new KeywordHit(chunkId.Value, scoreDoc.Score));
            }
        }

        return Task.FromResult<IReadOnlyList<KeywordHit>>(results);
    }

    private static Query ParseContentQuery(StandardAnalyzer analyzer, string queryText)
    {
        var parser = new QueryParser(LUCENE_VERSION, ContentField, analyzer);
        try
        {
            return parser.Parse(QueryParser.Escape(queryText));
        }
        catch (ParseException)
        {
            // Bare operator words such as a trailing "AND" can still fail after escaping;
            // lower-casing turns them into ordinary terms.
            return parser.Parse(QueryParser.Escape(queryText.ToLowerInvariant()));
        }
    }
}
