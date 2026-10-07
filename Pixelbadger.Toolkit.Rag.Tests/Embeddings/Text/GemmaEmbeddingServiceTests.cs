using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Audio;
using Pixelbadger.Toolkit.Rag.Embeddings.Onnx;
using Pixelbadger.Toolkit.Rag.Embeddings.Text;
using Pixelbadger.Toolkit.Rag.Embeddings.Vision;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Text;

/// <summary>Orchestration of <see cref="GemmaEmbeddingService"/> with fake tokeniser / ONNX / encoders (no model files).</summary>
public class GemmaEmbeddingServiceTests
{
    private const int Hidden = 768;

    /// <summary>One id per UTF-16 char, offset into a non-reserved range so ids are traceable.</summary>
    private sealed class CharTokenizer : IGemmaTokenizer
    {
        public List<string> Seen { get; } = new();

        public int[] Encode(string text)
        {
            lock (Seen) Seen.Add(text);
            return text.Select(c => 1000 + c).ToArray();
        }
    }

    private sealed record Call(long[] Ids, long[] Mask, int BatchSize, int SequenceLength, EncodedFeatures? Image, EncodedFeatures? Audio);

    private sealed class FakeTextModel : IGemmaTextModel
    {
        public List<Call> Calls { get; } = new();
        public Action<int>? OnCall { get; set; }

        public float[] Run(TextBatch batch, EncodedFeatures? imageFeatures, EncodedFeatures? audioFeatures, CancellationToken cancellationToken)
        {
            Calls.Add(new Call(batch.InputIds, batch.AttentionMask, batch.BatchSize, batch.SequenceLength, imageFeatures, audioFeatures));
            OnCall?.Invoke(Calls.Count);
            var output = new float[batch.BatchSize * Hidden];
            for (int r = 0; r < batch.BatchSize; r++)
            {
                var row = batch.InputIds.AsSpan(r * batch.SequenceLength, batch.SequenceLength).ToArray()
                    .Zip(batch.AttentionMask.AsSpan(r * batch.SequenceLength, batch.SequenceLength).ToArray(), (id, m) => (id, m))
                    .Where(x => x.m == 1).Select(x => x.id).ToArray();
                Fill(output.AsSpan(r * Hidden, Hidden), row);
            }
            return output;
        }

        /// <summary>Deterministic function of the unpadded ids, so tests can predict each row's output.</summary>
        public static void Fill(Span<float> destination, IReadOnlyList<long> ids)
        {
            var seed = ids.Aggregate(17L, (acc, id) => acc * 31 + id) % 9973;
            for (int j = 0; j < destination.Length; j++) destination[j] = ((seed + j * 7) % 23) - 11.5f;
        }

        public static float[] Expected(IReadOnlyList<long> ids)
        {
            var raw = new float[Hidden];
            Fill(raw, ids);
            return EmbeddingMath.TruncateAndNormalize(raw, 256);
        }
    }

    private sealed class FakeVision : IVisionEncoder
    {
        public int Rows { get; init; } = 4;
        public int DataLength { get; init; } = -1;
        public EncodedFeatures? Last { get; private set; }

        public EncodedFeatures Encode(PreprocessedImage image)
            => Last = new EncodedFeatures(new float[DataLength >= 0 ? DataLength : Rows * 512], Rows);
    }

    private sealed class FakeAudio : IAudioEncoder
    {
        public int Rows { get; init; } = 5;
        public EncodedFeatures? Last { get; private set; }

        public EncodedFeatures Encode(PreprocessedAudio audio) => Last = new EncodedFeatures(new float[Rows * 512], Rows);
    }

    private static readonly PreprocessedImage Image = new(new float[768 * 4], new long[8], 4, 48, 48);
    private static readonly PreprocessedAudio Audio = new(new float[128 * 10], new bool[10], 1, 10);

    private static (GemmaEmbeddingService Service, FakeTextModel Model, CharTokenizer Tokenizer, FakeVision Vision, FakeAudio AudioEncoder) Build(
        int maxBatchSize = 8, int maxPaddedTokens = 100_000, Func<IGemmaTokenizer>? tokenizerFactory = null, FakeVision? vision = null)
    {
        var tokenizer = new CharTokenizer();
        var model = new FakeTextModel();
        vision ??= new FakeVision();
        var audio = new FakeAudio();
        var service = GemmaEmbeddingService.Create(new EmbeddingModelOptions(), tokenizerFactory ?? (() => tokenizer), model, vision, audio, maxBatchSize, maxPaddedTokens);
        return (service, model, tokenizer, vision, audio);
    }

    private static long[] Framed(string prompt) => GemmaInputBuilder.BuildText(prompt.Select(c => 1000 + c).ToArray());

    [Fact]
    public void Public_constructor_does_not_touch_model_files()
    {
        var options = new EmbeddingModelOptions(); // no ModelPath
        using var sessions = new OnnxSessionProvider(options);
        var service = new GemmaEmbeddingService(options, sessions, new FakeVision(), new FakeAudio());

        service.Dimensions.Should().Be(256);
        service.ModelId.Should().Be("embeddinggemma-2-q8@256");
    }

    [Fact]
    public async Task Embedding_without_a_model_path_fails_with_a_clear_message()
    {
        var options = new EmbeddingModelOptions();
        using var sessions = new OnnxSessionProvider(options);
        var service = new GemmaEmbeddingService(options, sessions, new FakeVision(), new FakeAudio());

        var act = () => service.EmbedQueryAsync("hello");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*PBRAG_MODEL_PATH*");
    }

    [Fact]
    public async Task Query_uses_prompt_framing_and_empty_media_features()
    {
        var (service, model, tokenizer, _, _) = Build();

        var vector = await service.EmbedQueryAsync("northern lights");

        tokenizer.Seen.Should().Equal("task: search result | query: northern lights");
        model.Calls.Should().ContainSingle();
        var call = model.Calls[0];
        call.Ids.Should().Equal(Framed("task: search result | query: northern lights"));
        call.Mask.Should().OnlyContain(m => m == 1);
        call.Image.Should().BeNull();
        call.Audio.Should().BeNull();
        vector.Should().HaveCount(256);
        vector.Should().Equal(FakeTextModel.Expected(call.Ids));
    }

    [Fact]
    public async Task Document_chunks_use_the_title_prompt_and_come_back_in_order()
    {
        var (service, model, tokenizer, _, _) = Build(maxBatchSize: 3);
        var chunks = new[] { "a long chunk with many characters", "x", "medium chunk", "yy", "another fairly long chunk here", "zzz", "q" };

        var vectors = await service.EmbedDocumentTextAsync("Mars.md", chunks);

        vectors.Should().HaveCount(chunks.Length);
        for (int i = 0; i < chunks.Length; i++)
            vectors[i].Should().Equal(FakeTextModel.Expected(Framed($"title: Mars.md | text: {chunks[i]}")), $"chunk {i}");
        tokenizer.Seen.Should().BeEquivalentTo(chunks.Select(c => $"title: Mars.md | text: {c}"));
        model.Calls.Should().HaveCount(3); // 7 chunks / batch of 3
        model.Calls.Should().OnlyContain(c => c.BatchSize <= 3 && c.Image == null && c.Audio == null);
    }

    [Fact]
    public async Task Missing_title_becomes_none()
    {
        var (service, _, tokenizer, _, _) = Build();
        await service.EmbedDocumentTextAsync(null, new[] { "text" });
        tokenizer.Seen.Should().Equal("title: none | text: text");
    }

    [Fact]
    public async Task Padded_batches_do_not_change_a_chunks_vector()
    {
        var (service, _, _, _, _) = Build(maxBatchSize: 8);
        var alone = await service.EmbedDocumentTextAsync("t", new[] { "short" });
        var together = await service.EmbedDocumentTextAsync("t", new[] { "short", "a much longer neighbour that forces padding" });
        together[0].Should().Equal(alone[0]);
    }

    [Fact]
    public async Task Empty_chunk_list_returns_empty_without_running_the_model()
    {
        var (service, model, _, _, _) = Build();
        (await service.EmbedDocumentTextAsync("t", Array.Empty<string>())).Should().BeEmpty();
        model.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Image_passes_encoder_features_and_expanded_placeholders()
    {
        var (service, model, tokenizer, vision, _) = Build(vision: new FakeVision { Rows = 6 });

        var vector = await service.EmbedImageAsync(Image);

        tokenizer.Seen.Should().BeEmpty("media is never prefixed or tokenised");
        var call = model.Calls.Should().ContainSingle().Subject;
        call.Ids.Should().Equal(GemmaInputBuilder.BuildImage(6));
        call.Image.Should().BeSameAs(vision.Last);
        call.Audio.Should().BeNull();
        vector.Should().HaveCount(256);
    }

    [Fact]
    public async Task Audio_passes_encoder_features_and_expanded_placeholders()
    {
        var (service, model, tokenizer, _, audio) = Build();

        var vector = await service.EmbedAudioAsync(Audio);

        tokenizer.Seen.Should().BeEmpty();
        var call = model.Calls.Should().ContainSingle().Subject;
        call.Ids.Should().Equal(GemmaInputBuilder.BuildAudio(5));
        call.Audio.Should().BeSameAs(audio.Last);
        call.Image.Should().BeNull();
        vector.Should().HaveCount(256);
    }

    [Fact]
    public async Task Encoder_returning_no_rows_is_an_error()
    {
        var (service, model, _, _, _) = Build(vision: new FakeVision { Rows = 0 });
        var act = () => service.EmbedImageAsync(Image);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no soft tokens*");
        model.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Encoder_data_not_matching_rows_is_an_error()
    {
        var (service, _, _, _, _) = Build(vision: new FakeVision { Rows = 3, DataLength = 100 });
        var act = () => service.EmbedImageAsync(Image);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*expected rows*");
    }

    [Fact]
    public async Task Cancelled_token_stops_before_any_work()
    {
        var (service, model, tokenizer, _, _) = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await ((Func<Task>)(() => service.EmbedQueryAsync("q", cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => service.EmbedDocumentTextAsync("t", new[] { "a" }, cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => service.EmbedImageAsync(Image, cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        await ((Func<Task>)(() => service.EmbedAudioAsync(Audio, cts.Token))).Should().ThrowAsync<OperationCanceledException>();
        model.Calls.Should().BeEmpty();
        tokenizer.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_between_batches_stops_remaining_batches()
    {
        var (service, model, _, _, _) = Build(maxBatchSize: 1);
        using var cts = new CancellationTokenSource();
        model.OnCall = n => { if (n == 2) cts.Cancel(); };

        var act = () => service.EmbedDocumentTextAsync("t", new[] { "a", "b", "c", "d", "e" }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        model.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Tokenizer_is_loaded_lazily_once_and_a_failed_load_is_retried()
    {
        var loads = 0;
        var tokenizer = new CharTokenizer();
        var (service, _, _, _, _) = Build(tokenizerFactory: () =>
        {
            if (Interlocked.Increment(ref loads) == 1) throw new FileNotFoundException("tokenizer.json missing");
            return tokenizer;
        });
        loads.Should().Be(0, "construction must not load anything");

        var first = () => service.EmbedQueryAsync("q");
        await first.Should().ThrowAsync<FileNotFoundException>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => service.EmbedQueryAsync($"q{i}")));
        loads.Should().Be(2, "one failed attempt, then exactly one successful load shared by concurrent callers");
    }

    [Fact]
    public async Task Concurrent_calls_return_the_right_vector_to_each_caller()
    {
        var (service, _, _, _, _) = Build();
        var queries = Enumerable.Range(0, 24).Select(i => $"query number {i}").ToArray();

        var vectors = await Task.WhenAll(queries.Select(q => service.EmbedQueryAsync(q)));

        for (int i = 0; i < queries.Length; i++)
            vectors[i].Should().Equal(FakeTextModel.Expected(Framed(GemmaPrompts.Query(queries[i]))));
    }
}
