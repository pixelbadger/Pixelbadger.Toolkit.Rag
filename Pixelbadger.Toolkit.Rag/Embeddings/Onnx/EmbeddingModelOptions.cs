namespace Pixelbadger.Toolkit.Rag.Embeddings.Onnx;

/// <summary>
/// Location and runtime settings of the local onnx-community/embeddinggemma-2-ONNX snapshot.
/// The tool never downloads models: ModelPath must point at a local copy containing
/// tokenizer.json, config.json, processor_config.json and an onnx/ folder.
/// </summary>
public sealed class EmbeddingModelOptions
{
    public const string ModelPathEnvVar = "PBRAG_MODEL_PATH";
    public const int DefaultDimensions = 256;

    public string ModelPath { get; set; } = string.Empty;

    /// <summary>Matryoshka output dimension. Fixed at 256 (matches the vector(256) column).</summary>
    public int Dimensions { get; set; } = DefaultDimensions;

    public string TextModelFile { get; set; } = "onnx/model.onnx";
    public string VisionModelFile { get; set; } = "onnx/vision_encoder.onnx";
    public string AudioModelFile { get; set; } = "onnx/audio_encoder.onnx";
    public string TokenizerFile { get; set; } = "tokenizer.json";

    /// <summary>0 = Environment.ProcessorCount.</summary>
    public int IntraOpNumThreads { get; set; }

    public string ModelId => $"embeddinggemma-2@{Dimensions}";

    public string Resolve(string relative) => Path.Combine(ModelPath, relative);
}
