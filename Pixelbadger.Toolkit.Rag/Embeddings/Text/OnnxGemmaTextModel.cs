using Microsoft.ML.OnnxRuntime;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Text;

/// <summary>The text graph (<c>model.onnx</c>), which also performs the multimodal merge. Seam for tests.</summary>
public interface IGemmaTextModel
{
    /// <summary>
    /// Runs one batch and returns the flat row-major <c>sentence_embedding</c> [BatchSize, hidden]
    /// (unit-norm, full width). Feature rows are consumed in placeholder order.
    /// </summary>
    float[] Run(TextBatch batch, EncodedFeatures? imageFeatures, EncodedFeatures? audioFeatures, CancellationToken cancellationToken);
}

/// <summary>ONNX Runtime implementation of <see cref="IGemmaTextModel"/> (reference §3, §8).</summary>
public sealed class OnnxGemmaTextModel : IGemmaTextModel
{
    private static readonly long[] EmptyFeatureShape = { 0, EncodedFeatures.FeatureWidth };
    private static readonly string[] OutputNames = { "sentence_embedding" };

    private readonly OnnxSessionProvider _sessions;

    public OnnxGemmaTextModel(OnnxSessionProvider sessions)
    {
        _sessions = sessions;
    }

    public float[] Run(TextBatch batch, EncodedFeatures? imageFeatures, EncodedFeatures? audioFeatures, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = _sessions.Text;

        var empty = Array.Empty<float>();
        using var ids = OrtValue.CreateTensorValueFromMemory(batch.InputIds, new long[] { batch.BatchSize, batch.SequenceLength });
        using var mask = OrtValue.CreateTensorValueFromMemory(batch.AttentionMask, new long[] { batch.BatchSize, batch.SequenceLength });
        using var image = FeatureTensor(imageFeatures, empty);
        using var video = OrtValue.CreateTensorValueFromMemory(empty, EmptyFeatureShape);
        using var audio = FeatureTensor(audioFeatures, empty);

        // All five inputs are required; absent modalities are empty [0, 512] tensors (reference §3).
        var inputs = new Dictionary<string, OrtValue>
        {
            ["input_ids"] = ids,
            ["attention_mask"] = mask,
            ["image_features"] = image,
            ["video_features"] = video,
            ["audio_features"] = audio,
        };

        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);

        try
        {
            // Only sentence_embedding is requested; last_hidden_state [batch, seq, 768] would be wasted work.
            using var outputs = session.Run(runOptions, inputs, OutputNames);
            var result = outputs[0].GetTensorDataAsSpan<float>().ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static OrtValue FeatureTensor(EncodedFeatures? features, float[] empty)
    {
        if (features is null) return OrtValue.CreateTensorValueFromMemory(empty, EmptyFeatureShape);

        if (features.Rows <= 0 || features.Data.Length != (long)features.Rows * EncodedFeatures.FeatureWidth)
            throw new InvalidOperationException(
                $"Encoder output has {features.Data.Length} values for {features.Rows} rows; expected rows × {EncodedFeatures.FeatureWidth}.");

        return OrtValue.CreateTensorValueFromMemory(features.Data, new long[] { features.Rows, EncodedFeatures.FeatureWidth });
    }
}
