using Microsoft.ML.OnnxRuntime;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Vision;

/// <summary>Runs vision_encoder_quantized.onnx (session created lazily by <see cref="OnnxSessionProvider"/>).</summary>
public sealed class VisionEncoder : IVisionEncoder
{
    public const string PixelValuesInput = "pixel_values";
    public const string PositionIdsInput = "pixel_position_ids";
    public const string FeaturesOutput = "image_features";
    public const int PatchValues = 16 * 16 * 3;

    private readonly OnnxSessionProvider _sessions;

    public VisionEncoder(OnnxSessionProvider sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public EncodedFeatures Encode(PreprocessedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.NumPatches <= 0 || image.PixelValues.Length != image.NumPatches * PatchValues || image.PositionIds.Length != image.NumPatches * 2)
            throw new ArgumentException("PreprocessedImage tensors are inconsistent with NumPatches.", nameof(image));

        var session = _sessions.Vision;
        long patches = image.NumPatches;

        using var pixelValues = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, image.PixelValues.AsMemory(), [1L, patches, PatchValues]);
        using var positionIds = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, image.PositionIds.AsMemory(), [1L, patches, 2L]);
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, [PixelValuesInput, PositionIdsInput], [pixelValues, positionIds], [FeaturesOutput]);

        var output = outputs[0];
        var shape = output.GetTensorTypeAndShape().Shape;
        if (shape.Length != 2 || shape[1] != EncodedFeatures.FeatureWidth)
            throw new InvalidOperationException($"Unexpected {FeaturesOutput} shape [{string.Join(", ", shape)}]; expected [soft_tokens, {EncodedFeatures.FeatureWidth}].");

        int rows = checked((int)shape[0]);
        if (rows != image.ExpectedSoftTokens)
            throw new InvalidOperationException($"Vision encoder returned {rows} soft tokens but {image.ExpectedSoftTokens} were expected for a {image.Width}x{image.Height} image.");

        return new EncodedFeatures(output.GetTensorDataAsSpan<float>().ToArray(), rows);
    }
}
