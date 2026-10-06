using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Text;

/// <summary>
/// EmbeddingGemma 2 embeddings via ONNX Runtime. Text goes through the prompt formats,
/// tokeniser and padded batches; images and audio windows go through their encoders and are merged by the
/// text graph itself. Every vector is truncated to <see cref="Dimensions"/> and re-normalised in
/// <see cref="EmbeddingMath"/>. Nothing touches the model files until the first embedding call.
/// </summary>
public sealed class GemmaEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingModelOptions _options;
    private readonly Func<IGemmaTokenizer> _tokenizerFactory;
    private readonly IGemmaTextModel _model;
    private readonly IVisionEncoder _visionEncoder;
    private readonly IAudioEncoder _audioEncoder;
    private readonly int _maxBatchSize;
    private readonly int _maxPaddedTokens;

    private readonly object _tokenizerLock = new();
    private IGemmaTokenizer? _tokenizer;

    /// <summary>DI constructor. The tokenizer and ONNX sessions are loaded lazily on first use.</summary>
    public GemmaEmbeddingService(EmbeddingModelOptions options, OnnxSessionProvider sessions, IVisionEncoder visionEncoder, IAudioEncoder audioEncoder)
        : this(options, () => LoadTokenizer(options), new OnnxGemmaTextModel(sessions), visionEncoder, audioEncoder,
            BatchPlanner.DefaultMaxBatchSize, BatchPlanner.DefaultMaxPaddedTokens)
    {
    }

    private GemmaEmbeddingService(EmbeddingModelOptions options, Func<IGemmaTokenizer> tokenizerFactory, IGemmaTextModel model,
        IVisionEncoder visionEncoder, IAudioEncoder audioEncoder, int maxBatchSize, int maxPaddedTokens)
    {
        _options = options;
        _tokenizerFactory = tokenizerFactory;
        _model = model;
        _visionEncoder = visionEncoder;
        _audioEncoder = audioEncoder;
        _maxBatchSize = maxBatchSize;
        _maxPaddedTokens = maxPaddedTokens;
    }

    /// <summary>Test seam: builds the service around a fake tokenizer / text model. Not used by DI.</summary>
    public static GemmaEmbeddingService Create(EmbeddingModelOptions options, Func<IGemmaTokenizer> tokenizerFactory, IGemmaTextModel model,
        IVisionEncoder visionEncoder, IAudioEncoder audioEncoder, int maxBatchSize = BatchPlanner.DefaultMaxBatchSize,
        int maxPaddedTokens = BatchPlanner.DefaultMaxPaddedTokens)
        => new(options, tokenizerFactory, model, visionEncoder, audioEncoder, maxBatchSize, maxPaddedTokens);

    public int Dimensions => _options.Dimensions;
    public string ModelId => _options.ModelId;

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var vectors = await EmbedSequencesAsync(new[] { TokenizePrompt(GemmaPrompts.Query(query)) }, cancellationToken);
        return vectors[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedDocumentTextAsync(string? title, IReadOnlyList<string> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();
        if (chunks.Count == 0) return Array.Empty<float[]>();

        var sequences = new long[chunks.Count][];
        for (int i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sequences[i] = TokenizePrompt(GemmaPrompts.Document(title, chunks[i]));
        }
        return await EmbedSequencesAsync(sequences, cancellationToken);
    }

    public async Task<float[]> EmbedImageAsync(PreprocessedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        var features = await Task.Run(() => _visionEncoder.Encode(image), cancellationToken);
        ValidateFeatures(features, "vision");
        var vectors = await EmbedSequencesAsync(new[] { GemmaInputBuilder.BuildImage(features.Rows) }, cancellationToken, image: features);
        return vectors[0];
    }

    public async Task<float[]> EmbedAudioAsync(PreprocessedAudio audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        cancellationToken.ThrowIfCancellationRequested();

        var features = await Task.Run(() => _audioEncoder.Encode(audio), cancellationToken);
        ValidateFeatures(features, "audio");
        var vectors = await EmbedSequencesAsync(new[] { GemmaInputBuilder.BuildAudio(features.Rows) }, cancellationToken, audio: features);
        return vectors[0];
    }

    /// <summary>
    /// Runs sequences in length-sorted, size-capped batches and returns the vectors in input order.
    /// Media features are only ever passed with a single sequence, so row order equals placeholder order.
    /// </summary>
    private async Task<float[][]> EmbedSequencesAsync(long[][] sequences, CancellationToken cancellationToken,
        EncodedFeatures? image = null, EncodedFeatures? audio = null)
    {
        var results = new float[sequences.Length][];
        var batches = BatchPlanner.Plan(sequences.Select(s => s.Length).ToArray(), _maxBatchSize, _maxPaddedTokens);

        foreach (var indices in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = TextBatch.Create(indices.Select(i => sequences[i]).ToArray());

            var raw = await Task.Run(() => _model.Run(batch, image, audio, cancellationToken), cancellationToken);
            var vectors = GemmaOutputProcessor.Process(raw, batch.BatchSize, Dimensions);
            for (int k = 0; k < indices.Length; k++) results[indices[k]] = vectors[k];
        }
        return results;
    }

    private long[] TokenizePrompt(string prompt) => GemmaInputBuilder.BuildText(GetTokenizer().Encode(prompt));

    private IGemmaTokenizer GetTokenizer()
    {
        var tokenizer = Volatile.Read(ref _tokenizer);
        if (tokenizer is not null) return tokenizer;

        lock (_tokenizerLock) // a failed load is not cached, so a fixed model path can recover
        {
            return _tokenizer ??= _tokenizerFactory();
        }
    }

    private static IGemmaTokenizer LoadTokenizer(EmbeddingModelOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ModelPath))
            throw new InvalidOperationException($"Model path not configured. Pass --model-path or set {EmbeddingModelOptions.ModelPathEnvVar}.");
        return GemmaTokenizer.Load(options.Resolve(options.TokenizerFile));
    }

    private static void ValidateFeatures(EncodedFeatures features, string source)
    {
        if (features is null || features.Rows <= 0)
            throw new InvalidOperationException($"The {source} encoder returned no soft tokens.");
        if (features.Data.Length != (long)features.Rows * EncodedFeatures.FeatureWidth)
            throw new InvalidOperationException(
                $"The {source} encoder returned {features.Data.Length} values for {features.Rows} rows; expected rows × {EncodedFeatures.FeatureWidth}.");
    }
}
