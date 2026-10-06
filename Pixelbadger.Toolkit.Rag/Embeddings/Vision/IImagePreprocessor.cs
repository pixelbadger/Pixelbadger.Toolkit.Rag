namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

/// <summary>Decodes an image file and builds vision encoder inputs (processor_config.json semantics).</summary>
public interface IImagePreprocessor
{
    Task<PreprocessedImage> PreprocessAsync(string filePath, CancellationToken cancellationToken = default);
}
