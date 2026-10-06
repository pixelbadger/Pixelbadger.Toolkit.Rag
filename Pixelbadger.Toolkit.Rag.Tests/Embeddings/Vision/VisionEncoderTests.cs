using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using Pixelbadger.Toolkit.Rag.Tests.Support;
using Xunit;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Vision;

public class VisionEncoderTests
{
    /// <summary>Plumbing test against a tiny hand-built graph (see generate_fixtures.py::make_stub_model).</summary>
    [Fact]
    public void Stub_graph_round_trips_tensors_and_returns_features()
    {
        var options = new EmbeddingModelOptions { ModelPath = VisionTestSupport.AssetsDir, VisionModelFile = "vision_stub.onnx" };
        using var sessions = new OnnxSessionProvider(options);
        var encoder = new VisionEncoder(sessions);

        // constant image -> every patch value 1.0 -> each feature = 1.0 (mean over 9 patches of mean over 768 values)
        var rgb = Enumerable.Repeat((byte)255, 96 * 144 * 3).ToArray();
        var img = ImagePatchifier.Patchify(rgb, 96, 144, 16, VisionPatchLayout.Default);
        var features = encoder.Encode(img);

        features.Rows.Should().Be(img.ExpectedSoftTokens).And.Be(2 * 3);
        features.Data.Should().HaveCount(6 * EncodedFeatures.FeatureWidth);
        features.Data.Should().OnlyContain(v => Math.Abs(v - 1f) < 1e-4f);
    }

    [Fact]
    public void Stub_graph_features_follow_patch_order()
    {
        var options = new EmbeddingModelOptions { ModelPath = VisionTestSupport.AssetsDir, VisionModelFile = "vision_stub.onnx" };
        using var sessions = new OnnxSessionProvider(options);
        var encoder = new VisionEncoder(sessions);

        // 48x48 image = 9 patches; patch p filled with value p*10 -> single feature row = mean = 40.
        var values = new float[9 * 768];
        for (int p = 0; p < 9; p++) Array.Fill(values, p * 10f / 255f, p * 768, 768);
        var img = new PreprocessedImage(values, new long[18], 9, 48, 48);
        var features = encoder.Encode(img);

        features.Rows.Should().Be(1);
        features.Data[0].Should().BeApproximately(40f / 255f, 1e-4f);
    }

    [Fact]
    public void Throws_when_encoder_row_count_disagrees_with_expected_soft_tokens()
    {
        var options = new EmbeddingModelOptions { ModelPath = VisionTestSupport.AssetsDir, VisionModelFile = "vision_stub.onnx" };
        using var sessions = new OnnxSessionProvider(options);
        var encoder = new VisionEncoder(sessions);

        // Claim 96x96 (=4 tokens) but supply 9 patches (stub returns 1 row).
        var img = new PreprocessedImage(new float[9 * 768], new long[18], 9, 96, 96);
        var act = () => encoder.Encode(img);
        act.Should().Throw<InvalidOperationException>().WithMessage("*soft tokens*");
    }

    [Fact]
    public void Rejects_inconsistent_preprocessed_image()
    {
        using var sessions = new OnnxSessionProvider(new EmbeddingModelOptions());
        var act = () => new VisionEncoder(sessions).Encode(new PreprocessedImage(new float[10], new long[2], 1, 48, 48));
        act.Should().Throw<ArgumentException>();
    }

    [SkippableFact]
    public async Task Real_model_produces_one_row_per_soft_token()
    {
        Skip.If(TestModelPaths.ModelPath is null, TestModelPaths.SkipReason);
        using var sessions = new OnnxSessionProvider(new EmbeddingModelOptions { ModelPath = TestModelPaths.ModelPath! });
        var img = await new ImagePreprocessor().PreprocessAsync(VisionTestSupport.Asset("images", "rgb_100x70.png"));
        var features = new VisionEncoder(sessions).Encode(img);

        features.Rows.Should().Be(img.ExpectedSoftTokens);
        features.Data.Should().HaveCount(features.Rows * EncodedFeatures.FeatureWidth);
        features.Data.Should().OnlyContain(v => !float.IsNaN(v) && !float.IsInfinity(v));
        // [verify] patch layout conventions (VisionPatchLayout) against golden tensors/embeddings from the real graph.
    }
}
