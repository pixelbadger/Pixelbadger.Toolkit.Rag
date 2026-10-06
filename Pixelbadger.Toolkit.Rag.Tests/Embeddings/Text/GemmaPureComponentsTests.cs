using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings;
using Pixelbadger.Toolkit.Rag.Embeddings.Text;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Text;

public class GemmaPromptsTests
{
    [Fact]
    public void Query_uses_the_search_result_task_prefix()
        => GemmaPrompts.Query("Which planet is known as the Red Planet?")
            .Should().Be("task: search result | query: Which planet is known as the Red Planet?");

    [Fact]
    public void Document_uses_title_and_text()
        => GemmaPrompts.Document("Mars.md", "Red planet").Should().Be("title: Mars.md | text: Red planet");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Document_without_title_uses_none(string? title)
        => GemmaPrompts.Document(title, "x").Should().Be("title: none | text: x");

    [Fact]
    public void Text_is_not_altered()
        => GemmaPrompts.Document("t", "  keeps\nwhitespace  ").Should().Be("title: t | text:   keeps\nwhitespace  ");
}

public class GemmaInputBuilderTests
{
    [Fact]
    public void Text_is_framed_with_bos_and_eos_exactly_once()
    {
        var ids = GemmaInputBuilder.BuildText(new[] { 10, 20, 30 });
        ids.Should().Equal(2, 10, 20, 30, 1);
    }

    [Fact]
    public void Empty_body_still_gets_framing()
        => GemmaInputBuilder.BuildText(ReadOnlySpan<int>.Empty).Should().Equal(2, 1);

    [Fact]
    public void Overlong_text_is_truncated_but_keeps_both_markers()
    {
        var body = Enumerable.Range(100, 20).ToArray();
        var ids = GemmaInputBuilder.BuildText(body, maxTokens: 8);
        ids.Should().Equal(2, 100, 101, 102, 103, 104, 105, 1);
    }

    [Fact]
    public void Default_limit_is_the_8192_token_context()
    {
        var ids = GemmaInputBuilder.BuildText(new int[20_000]);
        ids.Should().HaveCount(8192);
        ids[0].Should().Be(GemmaSpecialTokens.Bos);
        ids[^1].Should().Be(GemmaSpecialTokens.Eos);
    }

    [Fact]
    public void Image_expands_one_placeholder_per_feature_row()
    {
        var ids = GemmaInputBuilder.BuildImage(3);
        ids.Should().Equal(2, 255999, 258880, 258880, 258880, 258882, 1);
    }

    [Fact]
    public void Audio_expands_one_placeholder_per_feature_row()
    {
        var ids = GemmaInputBuilder.BuildAudio(2);
        ids.Should().Equal(2, 256000, 258881, 258881, 258883, 1);
    }

    [Fact]
    public void Image_with_256_soft_tokens_has_the_expected_shape()
    {
        var ids = GemmaInputBuilder.BuildImage(256);
        ids.Should().HaveCount(260);
        ids.Count(x => x == GemmaSpecialTokens.ImagePlaceholder).Should().Be(256);
        ids.Count(x => x == GemmaSpecialTokens.Bos).Should().Be(1);
        ids.Count(x => x == GemmaSpecialTokens.Eos).Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(8189)]
    public void Media_with_invalid_row_count_is_rejected(int rows)
    {
        Action image = () => GemmaInputBuilder.BuildImage(rows);
        Action audio = () => GemmaInputBuilder.BuildAudio(rows);
        image.Should().Throw<ArgumentOutOfRangeException>();
        audio.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reserved_ids_cover_framing_and_placeholders()
    {
        new[] { 0, 1, 2, 255999, 256000, 258880, 258881, 258882, 258883, 258884 }
            .Should().OnlyContain(id => GemmaSpecialTokens.IsReserved(id));
        GemmaSpecialTokens.IsReserved(5).Should().BeFalse();
        GemmaSpecialTokens.IsReserved(106).Should().BeFalse();
    }
}

public class TextBatchTests
{
    [Fact]
    public void Pads_right_with_zero_and_masks_padding()
    {
        var batch = TextBatch.Create(new[] { new long[] { 2, 7, 1 }, new long[] { 2, 8, 9, 6, 1 } });

        batch.BatchSize.Should().Be(2);
        batch.SequenceLength.Should().Be(5);
        batch.InputIds.Should().Equal(2, 7, 1, 0, 0, /**/ 2, 8, 9, 6, 1);
        batch.AttentionMask.Should().Equal(1, 1, 1, 0, 0, /**/ 1, 1, 1, 1, 1);
    }

    [Fact]
    public void Single_sequence_needs_no_padding()
    {
        var batch = TextBatch.Create(new[] { new long[] { 2, 5, 1 } });
        batch.InputIds.Should().Equal(2, 5, 1);
        batch.AttentionMask.Should().Equal(1, 1, 1);
    }

    [Fact]
    public void Rejects_empty_input()
    {
        var none = () => TextBatch.Create(Array.Empty<long[]>());
        var emptyRow = () => TextBatch.Create(new[] { new long[] { 1 }, Array.Empty<long>() });
        none.Should().Throw<ArgumentException>();
        emptyRow.Should().Throw<ArgumentException>();
    }
}

public class BatchPlannerTests
{
    [Fact]
    public void Every_index_appears_exactly_once()
    {
        var lengths = Enumerable.Range(0, 37).Select(i => 5 + (i * 7919) % 300).ToArray();
        var plan = BatchPlanner.Plan(lengths, maxBatchSize: 8, maxPaddedTokens: 4000);
        plan.SelectMany(b => b).OrderBy(i => i).Should().Equal(Enumerable.Range(0, 37));
    }

    [Fact]
    public void Batches_respect_the_size_cap()
    {
        var plan = BatchPlanner.Plan(Enumerable.Repeat(10, 20).ToArray(), maxBatchSize: 8, maxPaddedTokens: 100_000);
        plan.Select(b => b.Length).Should().Equal(8, 8, 4);
    }

    [Fact]
    public void Batches_respect_the_padded_token_cap()
    {
        var lengths = new[] { 3000, 3000, 3000, 100, 100 };
        var plan = BatchPlanner.Plan(lengths, maxBatchSize: 8, maxPaddedTokens: 6000);

        foreach (var batch in plan)
            (batch.Length * batch.Max(i => lengths[i])).Should().BeLessThanOrEqualTo(6000);
        plan.Should().HaveCount(3); // {100,100} | {3000,3000} | {3000}: 3 x 3000 would exceed the cap
    }

    [Fact]
    public void A_single_oversized_sequence_still_gets_its_own_batch()
    {
        var plan = BatchPlanner.Plan(new[] { 8192, 8192 }, maxBatchSize: 8, maxPaddedTokens: 4096);
        plan.Should().HaveCount(2);
        plan.Should().OnlyContain(b => b.Length == 1);
    }

    [Fact]
    public void Similar_lengths_are_grouped_to_limit_padding()
    {
        var lengths = new[] { 500, 10, 480, 12, 11, 505 };
        var plan = BatchPlanner.Plan(lengths, maxBatchSize: 3, maxPaddedTokens: 100_000);
        plan[0].OrderBy(i => i).Should().Equal(1, 3, 4);
        plan[1].OrderBy(i => i).Should().Equal(0, 2, 5);
    }

    [Fact]
    public void Plan_is_deterministic_and_empty_input_gives_no_batches()
    {
        var lengths = new[] { 5, 5, 5, 5 };
        BatchPlanner.Plan(lengths, 2).Select(b => string.Join(",", b)).Should().Equal("0,1", "2,3");
        BatchPlanner.Plan(Array.Empty<int>()).Should().BeEmpty();
    }
}

public class GemmaOutputProcessorTests
{
    [Fact]
    public void Truncates_and_renormalises_each_row()
    {
        // two rows of width 4; keep 2 dims
        var raw = new float[] { 3, 4, 100, 100, 0, 2, -5, 5 };
        var vectors = GemmaOutputProcessor.Process(raw, batchSize: 2, dimensions: 2);

        vectors[0].Should().BeEquivalentTo(new[] { 0.6f, 0.8f }, o => o.WithStrictOrdering());
        vectors[1].Should().BeEquivalentTo(new[] { 0f, 1f }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Output_rows_have_unit_norm()
    {
        var rng = new Random(1);
        var raw = Enumerable.Range(0, 3 * 768).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        foreach (var v in GemmaOutputProcessor.Process(raw, 3, 256))
        {
            v.Should().HaveCount(256);
            Math.Sqrt(v.Sum(x => (double)x * x)).Should().BeApproximately(1.0, 1e-5);
        }
    }

    [Fact]
    public void Matches_EmbeddingMath()
    {
        var row = Enumerable.Range(1, 768).Select(i => (float)Math.Sin(i)).ToArray();
        GemmaOutputProcessor.Process(row, 1, 256)[0].Should().Equal(EmbeddingMath.TruncateAndNormalize(row, 256));
    }

    [Fact]
    public void Rejects_shapes_that_do_not_fit()
    {
        var notDivisible = () => GemmaOutputProcessor.Process(new float[10], 3, 2);
        var tooNarrow = () => GemmaOutputProcessor.Process(new float[8], 2, 8);
        var empty = () => GemmaOutputProcessor.Process(ReadOnlySpan<float>.Empty, 1, 2);
        notDivisible.Should().Throw<InvalidOperationException>();
        tooNarrow.Should().Throw<InvalidOperationException>();
        empty.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Zero_vector_is_an_error_not_a_silent_NaN()
    {
        var act = () => GemmaOutputProcessor.Process(new float[8], 2, 4);
        act.Should().Throw<InvalidOperationException>();
    }
}
