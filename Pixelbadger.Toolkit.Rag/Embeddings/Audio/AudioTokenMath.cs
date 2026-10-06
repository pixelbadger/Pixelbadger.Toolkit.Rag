namespace Pixelbadger.Toolkit.Rag.Embeddings.Audio;

/// <summary>
/// Soft-token arithmetic of the audio tower, ported from <c>EmbeddingGemma2Processor.replace_audio_token</c> and
/// <c>_compute_audio_num_tokens</c> (transformers/models/embedding_gemma2/processing_embedding_gemma2.py): two stride-2,
/// kernel-3, pad-1 convolutions over the mel-frame axis, applied to the frame mask. Used for sanity checks; the
/// authoritative row count is the encoder output's.
/// </summary>
public static class AudioTokenMath
{
    /// <summary>Soft tokens for one clip's frame mask: simulate the two stride-2 convs on the mask and count true entries.</summary>
    public static int CountTokens(ReadOnlySpan<bool> clipMask)
    {
        var current = clipMask.ToArray();
        for (var layer = 0; layer < 2; layer++)
        {
            var tOut = (current.Length + 2 - 3) / 2 + 1;
            var next = new List<bool>(tOut);
            for (var i = 0; i < current.Length && next.Count < tOut; i += 2) next.Add(current[i]);
            current = [.. next];
        }
        return current.Count(m => m);
    }

    /// <summary>Total tokens expected from the encoder for a window (sum over clips).</summary>
    public static int CountTokens(PreprocessedAudio audio)
    {
        var total = 0;
        for (var c = 0; c < audio.Clips; c++)
            total += CountTokens(audio.Mask.AsSpan(c * audio.Frames, audio.Frames));
        return total;
    }
}
