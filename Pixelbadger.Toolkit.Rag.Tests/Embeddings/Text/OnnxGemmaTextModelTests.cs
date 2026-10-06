using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Embeddings.Text;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;
using Pixelbadger.Toolkit.Rag.Tests.Support;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Text;

/// <summary>
/// Exercises the real ONNX Runtime plumbing (OrtValue creation, empty [0,512] tensors, output extraction, cancellation)
/// against a tiny synthetic graph with the same I/O contract as model.onnx (tools/golden/make_tiny_text_graph.py).
/// The row value is sum(ids*mask) + 1000*sum(image) + 1e6*sum(audio) + 1e-3*sum(video); output[b, j] = value[b] + j.
/// </summary>
public sealed class OnnxGemmaTextModelTests : IDisposable
{
    private readonly string _modelDir;
    private readonly OnnxSessionProvider _sessions;
    private readonly OnnxGemmaTextModel _model;
    private readonly EmbeddingModelOptions _options;

    public OnnxGemmaTextModelTests()
    {
        _modelDir = Path.Combine(Path.GetTempPath(), "pbrag-tiny-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_modelDir, "onnx"));
        File.Copy(Path.Combine(TestModelPaths.GoldenDir, "synthetic", "tiny_text_graph.onnx"), Path.Combine(_modelDir, "onnx", "model.onnx"));

        _options = new EmbeddingModelOptions { ModelPath = _modelDir };
        _sessions = new OnnxSessionProvider(_options);
        _model = new OnnxGemmaTextModel(_sessions);
    }

    public void Dispose()
    {
        _sessions.Dispose();
        Directory.Delete(_modelDir, true);
    }

    private static float RowValue(float[] output, int row) => output[row * 768];

    [Fact]
    public void Text_only_call_passes_empty_media_tensors_and_masks_padding()
    {
        var batch = TextBatch.Create(new[] { new long[] { 2, 10, 20, 1 }, new long[] { 2, 5, 1 } });

        var output = _model.Run(batch, null, null, CancellationToken.None);

        output.Should().HaveCount(2 * 768);
        RowValue(output, 0).Should().Be(2 + 10 + 20 + 1);
        RowValue(output, 1).Should().Be(2 + 5 + 1, "padding (id 0, mask 0) must not contribute");
        output[767].Should().Be(RowValue(output, 0) + 767);
    }

    [Fact]
    public void Image_and_audio_features_reach_their_own_inputs()
    {
        var batch = TextBatch.Create(new[] { new long[] { 2, 7, 1 } });
        var image = new EncodedFeatures(Enumerable.Repeat(0.5f, 3 * 512).ToArray(), 3);   // sum 768
        var audio = new EncodedFeatures(Enumerable.Repeat(0.25f, 2 * 512).ToArray(), 2);  // sum 256

        RowValue(_model.Run(batch, image, null, CancellationToken.None), 0).Should().Be(10 + 1000 * 768);
        RowValue(_model.Run(batch, null, audio, CancellationToken.None), 0).Should().Be(10 + 1_000_000f * 256);
        RowValue(_model.Run(batch, image, audio, CancellationToken.None), 0).Should().Be(10 + 1000 * 768 + 1_000_000f * 256);
    }

    [Fact]
    public void Feature_data_that_does_not_match_its_rows_is_rejected()
    {
        var batch = TextBatch.Create(new[] { new long[] { 2, 1 } });
        var bad = new EncodedFeatures(new float[100], 3);
        var act = () => _model.Run(batch, bad, null, CancellationToken.None);
        act.Should().Throw<InvalidOperationException>().WithMessage("*expected rows*");
    }

    [Fact]
    public void Cancelled_token_throws_before_running()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => _model.Run(TextBatch.Create(new[] { new long[] { 2, 1 } }), null, null, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public async Task Service_end_to_end_on_the_synthetic_graph_returns_truncated_unit_vectors()
    {
        var service = GemmaEmbeddingService.Create(_options, () => new FixedTokenizer(), _model, new FixedVision(), new NoAudio());

        var query = await service.EmbedQueryAsync("anything");
        var docs = await service.EmbedDocumentTextAsync("t", new[] { "short", "a considerably longer chunk" });
        var image = await service.EmbedImageAsync(new PreprocessedImage(new float[768], new long[2], 1, 48, 48));

        // FixedTokenizer returns [5, 6, 7]; framed ids are 2,5,6,7,1 -> sum 21
        query.Should().Equal(Expected(21));
        docs[0].Should().Equal(Expected(21));
        docs[1].Should().Equal(Expected(21), "batch padding must not leak into the vector");
        // image: ids 2, 255999, 258880, 258880, 258882, 1 (2 rows) + 1000 * sum(features = 2 rows * 512 * 1.0)
        image.Should().Equal(Expected(2 + 255999 + 258880 * 2 + 258882 + 1 + 1000f * 1024));

        foreach (var v in new[] { query, docs[0], docs[1], image })
        {
            v.Should().HaveCount(256);
            Math.Sqrt(v.Sum(x => (double)x * x)).Should().BeApproximately(1.0, 1e-5);
        }
    }

    private static float[] Expected(float rowValue)
    {
        var raw = Enumerable.Range(0, 768).Select(j => rowValue + j).ToArray();
        return EmbeddingMath.TruncateAndNormalize(raw, 256);
    }

    private sealed class FixedTokenizer : IGemmaTokenizer
    {
        public int[] Encode(string text) => new[] { 5, 6, 7 };
    }

    private sealed class FixedVision : IVisionEncoder
    {
        public EncodedFeatures Encode(PreprocessedImage image) => new(Enumerable.Repeat(1f, 2 * 512).ToArray(), 2);
    }

    private sealed class NoAudio : IAudioEncoder
    {
        public EncodedFeatures Encode(PreprocessedAudio audio) => throw new NotSupportedException();
    }
}
