using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Audio;

public class AudioEncoderTests
{
    [Fact]
    public void Encode_RejectsInconsistentShapes_BeforeTouchingTheModel()
    {
        using var sessions = new OnnxSessionProvider(new EmbeddingModelOptions());
        var encoder = new AudioEncoder(sessions);

        var act = () => encoder.Encode(new PreprocessedAudio(new float[10], new bool[2], Clips: 1, Frames: 2));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BoolMask_CanBeWrappedAsOrtTensor()
    {
        using var v = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(new[] { true, false, true, true }, [2L, 2L]);

        v.GetTensorTypeAndShape().ElementDataType.Should().Be(Microsoft.ML.OnnxRuntime.Tensors.TensorElementType.Bool);
        v.GetTensorTypeAndShape().Shape.Should().Equal(2L, 2L);
    }

    [SkippableTheory]
    [InlineData(5)]
    [InlineData(40)]
    public void Encode_RealModel_ReturnsFeatureRowsMatchingTokenArithmetic(int seconds)
    {
        Skip.If(TestModelPaths.ModelPath is null, TestModelPaths.SkipReason);
        using var sessions = new OnnxSessionProvider(new EmbeddingModelOptions { ModelPath = TestModelPaths.ModelPath! });
        var encoder = new AudioEncoder(sessions);
        var windows = new AudioPreprocessor().BuildWindows(AudioTestSignals.Sine(440, seconds * 16_000, 0.3));

        foreach (var w in windows)
        {
            var encoded = encoder.Encode(w.Features);

            encoded.Rows.Should().Be(AudioTokenMath.CountTokens(w.Features)); // [verify] vs the real graph
            encoded.Data.Length.Should().Be(encoded.Rows * EncodedFeatures.FeatureWidth);
            encoded.Data.Should().OnlyContain(v => float.IsFinite(v));
        }
    }
}
