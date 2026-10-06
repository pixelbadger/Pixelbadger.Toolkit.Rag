namespace Pixelbadger.Toolkit.Rag.Embeddings.Text;

/// <summary>Right-padded (pad id 0, mask 0) row-major [BatchSize, SequenceLength] int64 tensors.</summary>
public sealed record TextBatch(long[] InputIds, long[] AttentionMask, int BatchSize, int SequenceLength)
{
    public static TextBatch Create(IReadOnlyList<long[]> sequences)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        if (sequences.Count == 0) throw new ArgumentException("At least one sequence is required.", nameof(sequences));

        var length = 0;
        foreach (var s in sequences)
        {
            if (s.Length == 0) throw new ArgumentException("Empty sequence.", nameof(sequences));
            length = Math.Max(length, s.Length);
        }

        var ids = new long[sequences.Count * length]; // zero-filled == <pad>
        var mask = new long[ids.Length];
        for (int i = 0; i < sequences.Count; i++)
        {
            var s = sequences[i];
            Array.Copy(s, 0, ids, i * length, s.Length);
            Array.Fill(mask, 1L, i * length, s.Length);
        }
        return new TextBatch(ids, mask, sequences.Count, length);
    }
}

/// <summary>
/// Groups sequences into batches. Sorting by length keeps padding low; each batch is capped by
/// sequence count and by padded token count so a few 8k-token inputs cannot blow up memory.
/// Returned index groups refer to the input list; callers scatter results back to input order.
/// </summary>
public static class BatchPlanner
{
    public const int DefaultMaxBatchSize = 8;
    public const int DefaultMaxPaddedTokens = 8192;

    public static IReadOnlyList<int[]> Plan(IReadOnlyList<int> lengths, int maxBatchSize = DefaultMaxBatchSize, int maxPaddedTokens = DefaultMaxPaddedTokens)
    {
        if (maxBatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxBatchSize));
        if (maxPaddedTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxPaddedTokens));

        // Ascending length, ties by original index, so the plan is deterministic.
        var order = Enumerable.Range(0, lengths.Count).OrderBy(i => lengths[i]).ThenBy(i => i).ToArray();

        var batches = new List<int[]>();
        var current = new List<int>();
        foreach (var index in order)
        {
            // Ascending order: the candidate is the longest so far, so it defines the padded length.
            var padded = lengths[index];
            if (current.Count > 0 && (current.Count >= maxBatchSize || (current.Count + 1) * padded > maxPaddedTokens))
            {
                batches.Add(current.ToArray());
                current.Clear();
            }
            current.Add(index);
        }
        if (current.Count > 0) batches.Add(current.ToArray());
        return batches;
    }
}
