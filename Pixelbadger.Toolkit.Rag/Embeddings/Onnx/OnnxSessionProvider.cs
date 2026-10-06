using Microsoft.ML.OnnxRuntime;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Onnx;

/// <summary>
/// Lazily creates one shared InferenceSession per graph (Run is thread-safe).
/// Text-only hosts never load the encoders. Register as a singleton.
/// </summary>
public sealed class OnnxSessionProvider : IDisposable
{
    private readonly EmbeddingModelOptions _options;
    private readonly Lazy<InferenceSession> _text;
    private readonly Lazy<InferenceSession> _vision;
    private readonly Lazy<InferenceSession> _audio;

    public OnnxSessionProvider(EmbeddingModelOptions options)
    {
        _options = options;
        _text = new Lazy<InferenceSession>(() => Create(options.TextModelFile), LazyThreadSafetyMode.ExecutionAndPublication);
        _vision = new Lazy<InferenceSession>(() => Create(options.VisionModelFile), LazyThreadSafetyMode.ExecutionAndPublication);
        _audio = new Lazy<InferenceSession>(() => Create(options.AudioModelFile), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public InferenceSession Text => _text.Value;
    public InferenceSession Vision => _vision.Value;
    public InferenceSession Audio => _audio.Value;

    private InferenceSession Create(string relativeFile)
    {
        if (string.IsNullOrWhiteSpace(_options.ModelPath))
            throw new InvalidOperationException($"Model path not configured. Pass --model-path or set {EmbeddingModelOptions.ModelPathEnvVar}.");

        var path = _options.Resolve(relativeFile);
        if (!File.Exists(path))
            throw new FileNotFoundException($"ONNX model file not found: {path}", path);

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = _options.IntraOpNumThreads > 0 ? _options.IntraOpNumThreads : Environment.ProcessorCount
        };
        return new InferenceSession(path, sessionOptions);
    }

    public void Dispose()
    {
        if (_text.IsValueCreated) _text.Value.Dispose();
        if (_vision.IsValueCreated) _vision.Value.Dispose();
        if (_audio.IsValueCreated) _audio.Value.Dispose();
    }
}
