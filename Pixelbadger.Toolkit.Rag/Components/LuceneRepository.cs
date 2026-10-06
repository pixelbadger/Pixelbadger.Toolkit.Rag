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
    internal const string ContentField = "content";

    /// <summary>The indexed form of a document id ("D": 32 digits with hyphens, lower case).</summary>
    internal static string FormatDocumentId(Guid documentId) => documentId.ToString("D");

    /// <inheritdoc />
    public Task ReplaceDocumentAsync(
        string indexPath,
        Guid documentId,
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

        var documentKey = FormatDocumentId(documentId);
        writer.DeleteDocuments(new Term(DocumentIdField, documentKey));

        foreach (var chunk in chunks)
        {
            var doc = new Document
            {
                new Int32Field(ChunkIdField, chunk.ChunkId, Field.Store.YES),
                new StringField(DocumentIdField, documentKey, Field.Store.YES),
                new TextField(ContentField, chunk.Text, Field.Store.NO)
            };
            writer.AddDocument(doc);
        }

        writer.Commit();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteDocumentAsync(string indexPath, Guid documentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Nothing to delete (and no reason to create an index) when none exists yet, e.g. a media-only corpus.
        if (!System.IO.Directory.Exists(indexPath))
            return Task.CompletedTask;

        using var indexDirectory = FSDirectory.Open(indexPath);
        if (!DirectoryReader.IndexExists(indexDirectory))
            return Task.CompletedTask;

        using var analyzer = new StandardAnalyzer(LUCENE_VERSION);
        using var writer = new IndexWriter(indexDirectory, new IndexWriterConfig(LUCENE_VERSION, analyzer) { Similarity = new BM25Similarity() });
        writer.DeleteDocuments(new Term(DocumentIdField, FormatDocumentId(documentId)));
        writer.Commit();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KeywordHit>> SearchAsync(
        string indexPath,
        string queryText,
        int maxResults,
        IReadOnlyCollection<Guid>? documentIds,
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
        if (documentIds is { Count: > 0 })
        {
            var documentIdQuery = new BooleanQuery();
            foreach (var documentId in documentIds)
            {
                documentIdQuery.Add(new TermQuery(new Term(DocumentIdField, FormatDocumentId(documentId))), Occur.SHOULD);
            }

            var boolQuery = new BooleanQuery();
            boolQuery.Add(contentQuery, Occur.MUST);
            boolQuery.Add(documentIdQuery, Occur.MUST);
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
