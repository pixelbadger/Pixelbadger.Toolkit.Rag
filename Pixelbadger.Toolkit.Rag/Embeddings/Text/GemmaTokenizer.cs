using System.Text;
using System.Text.Json;

namespace Pixelbadger.Toolkit.Rag.Embeddings.Text;

/// <summary>Turns text into raw token ids (no <c>&lt;bos&gt;</c>/<c>&lt;eos&gt;</c>; see <see cref="GemmaInputBuilder"/>).</summary>
public interface IGemmaTokenizer
{
    /// <summary>Raw ids for <paramref name="text"/>, exactly as Hugging Face <c>tokenizers</c> returns them with <c>add_special_tokens=False</c>.</summary>
    int[] Encode(string text);
}

/// <summary>
/// Gemma (SentencePiece-style) BPE tokenizer that loads a Hugging Face <c>tokenizer.json</c>
/// (262,144-entry vocabulary, byte fallback). Written in-house because Microsoft.ML.Tokenizers 2.0.0
/// cannot load this file: <c>BpeOptions</c> has no byte-fallback support and the normaliser / added-token
/// semantics are not covered. Behaviour is pinned against Python <c>tokenizers</c> by the parity tests.
/// <para>
/// Supported pipeline (anything else makes <see cref="Load(string, bool)"/> throw, rather than drift silently):
/// normaliser <c>Replace " " → "▁"</c>; no effective pre-tokeniser (the Gemma <c>Split " "</c> is a no-op after the
/// normaliser); BPE with merge ranks = index in <c>merges</c>, optional byte fallback (<c>&lt;0xNN&gt;</c>), fused unknowns;
/// added tokens matched leftmost-longest on the raw text before normalisation.
/// </para>
/// <para>
/// The <c>post_processor</c> template is deliberately ignored: the Gemma 3 file adds only <c>&lt;bos&gt;</c> while
/// EmbeddingGemma 2 wraps <c>&lt;bos&gt; … &lt;eos&gt;</c>; <see cref="GemmaInputBuilder"/> applies the framing once.
/// </para>
/// </summary>
public sealed class GemmaTokenizer : IGemmaTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<int, int> _runeIds = new();
    private readonly Dictionary<long, MergeInfo> _merges;
    private readonly int[] _byteIds = new int[256];
    private readonly bool _byteFallback;
    private readonly bool _fuseUnknown;
    private readonly int _unknownId;
    private readonly string _spaceReplacement;
    private readonly TrieNode _addedTokens = new();
    private readonly HashSet<char> _addedTokenFirstChars = new();

    private readonly record struct MergeInfo(int Rank, int NewId);

    private sealed class TrieNode
    {
        public Dictionary<char, TrieNode>? Next;
        public int Id = -1;
    }

    private struct Symbol
    {
        public int Id;
        public int Prev;
        public int Next;
    }

    private GemmaTokenizer(JsonElement root, bool matchReservedTokensInText)
    {
        var model = root.GetProperty("model");
        var type = model.TryGetProperty("type", out var t) ? t.GetString() : "BPE";
        if (type != "BPE") throw new NotSupportedException($"tokenizer.json model type '{type}' is not supported (expected BPE).");

        _spaceReplacement = ReadSpaceReplacement(root);
        ValidatePreTokenizer(root);

        _byteFallback = model.TryGetProperty("byte_fallback", out var bf) && bf.ValueKind == JsonValueKind.True;
        _fuseUnknown = model.TryGetProperty("fuse_unk", out var fu) && fu.ValueKind == JsonValueKind.True;

        var vocabElement = model.GetProperty("vocab");
        _vocab = new Dictionary<string, int>(262_144);
        foreach (var p in vocabElement.EnumerateObject()) _vocab[p.Name] = p.Value.GetInt32();

        _unknownId = -1;
        if (model.TryGetProperty("unk_token", out var unk) && unk.ValueKind == JsonValueKind.String
            && _vocab.TryGetValue(unk.GetString()!, out var unkId))
            _unknownId = unkId;

        foreach (var (token, id) in _vocab)
        {
            if (token.Length is 1 or 2 && Rune.TryGetRuneAt(token, 0, out var rune) && rune.Utf16SequenceLength == token.Length)
                _runeIds[rune.Value] = id;
        }

        Array.Fill(_byteIds, -1);
        if (_byteFallback)
        {
            for (int b = 0; b < 256; b++)
                if (_vocab.TryGetValue($"<0x{b:X2}>", out var id)) _byteIds[b] = id;
        }

        _merges = LoadMerges(model.GetProperty("merges"), _vocab);
        LoadAddedTokens(root, matchReservedTokensInText);
    }

    /// <summary>Loads <c>tokenizer.json</c> from disk.</summary>
    /// <param name="path">Path to tokenizer.json.</param>
    /// <param name="matchReservedTokensInText">
    /// When true, literal text such as <c>&lt;eos&gt;</c> or <c>&lt;|image|&gt;</c> becomes the special id, like Hugging Face does.
    /// Default false: document text can then never forge framing/placeholder ids (which would desynchronise the
    /// placeholder count from the feature rows). Non-special added tokens (newline runs, HTML tags...) always match.
    /// </param>
    public static GemmaTokenizer Load(string path, bool matchReservedTokensInText = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Tokenizer file not found: {path}", path);
        using var stream = File.OpenRead(path);
        return Load(stream, matchReservedTokensInText);
    }

    /// <summary>Loads <c>tokenizer.json</c> from a stream.</summary>
    public static GemmaTokenizer Load(Stream stream, bool matchReservedTokensInText = false)
    {
        using var doc = JsonDocument.Parse(stream);
        return new GemmaTokenizer(doc.RootElement, matchReservedTokensInText);
    }

    /// <summary>Number of vocabulary entries (without added tokens that are not in the vocabulary).</summary>
    public int VocabularySize => _vocab.Count;

    /// <summary>Id of a token string, or null if not in the vocabulary.</summary>
    public int? TokenToId(string token) => _vocab.TryGetValue(token, out var id) ? id : null;

    /// <inheritdoc />
    public int[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ids = new List<int>(text.Length / 3 + 4);

        var segmentStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (_addedTokenFirstChars.Contains(text[i]))
            {
                var length = LongestAddedTokenAt(text, i, out var id);
                if (length > 0)
                {
                    EncodeSegment(text.AsSpan(segmentStart, i - segmentStart), ids);
                    ids.Add(id);
                    i += length;
                    segmentStart = i;
                    continue;
                }
            }
            i++;
        }
        EncodeSegment(text.AsSpan(segmentStart), ids);
        return ids.ToArray();
    }

    private int LongestAddedTokenAt(string text, int start, out int id)
    {
        id = -1;
        var best = 0;
        var node = _addedTokens;
        for (int i = start; i < text.Length; i++)
        {
            if (node.Next is null || !node.Next.TryGetValue(text[i], out node)) break;
            if (node.Id >= 0) { best = i - start + 1; id = node.Id; }
        }
        return best;
    }

    private void EncodeSegment(ReadOnlySpan<char> segment, List<int> output)
    {
        if (segment.IsEmpty) return;
        var normalized = segment.ToString().Replace(" ", _spaceReplacement, StringComparison.Ordinal);
        EncodeBpe(normalized, output);
    }

    private void EncodeBpe(string text, List<int> output)
    {
        var symbols = new List<Symbol>(text.Length);
        Span<byte> utf8 = stackalloc byte[4];

        foreach (var rune in text.EnumerateRunes())
        {
            if (_runeIds.TryGetValue(rune.Value, out var id))
            {
                symbols.Add(new Symbol { Id = id });
                continue;
            }

            var byteCount = rune.EncodeToUtf8(utf8);
            var allBytesKnown = _byteFallback;
            for (int b = 0; allBytesKnown && b < byteCount; b++)
                allBytesKnown = _byteIds[utf8[b]] >= 0;

            if (allBytesKnown)
            {
                for (int b = 0; b < byteCount; b++) symbols.Add(new Symbol { Id = _byteIds[utf8[b]] });
            }
            else if (_unknownId >= 0)
            {
                if (!(_fuseUnknown && symbols.Count > 0 && symbols[^1].Id == _unknownId))
                    symbols.Add(new Symbol { Id = _unknownId });
            }
            // else: dropped, like Hugging Face without an unk token
        }

        if (symbols.Count == 0) return;

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(symbols);
        for (int i = 0; i < span.Length; i++)
        {
            span[i].Prev = i - 1;
            span[i].Next = i + 1 < span.Length ? i + 1 : -1;
        }

        // Lowest rank first, then leftmost: priority = rank << 32 | leftIndex.
        var queue = new PriorityQueue<(int Left, int Right, int LeftId, int RightId, int NewId), long>();
        void TryEnqueue(int left)
        {
            var right = symbols[left].Next;
            if (right < 0) return;
            var a = symbols[left].Id;
            var b = symbols[right].Id;
            if (_merges.TryGetValue(PairKey(a, b), out var merge))
                queue.Enqueue((left, right, a, b, merge.NewId), ((long)merge.Rank << 32) | (uint)left);
        }

        for (int i = 0; i < symbols.Count - 1; i++) TryEnqueue(i);

        while (queue.TryDequeue(out var item, out _))
        {
            var left = symbols[item.Left];
            var right = symbols[item.Right];
            if (left.Id != item.LeftId || right.Id != item.RightId || left.Next != item.Right) continue; // stale

            var l = item.Left;
            var r = item.Right;
            var after = symbols[r].Next;
            var s = symbols[l];
            s.Id = item.NewId;
            s.Next = after;
            symbols[l] = s;
            var dead = symbols[r];
            dead.Id = -1;
            symbols[r] = dead;
            if (after >= 0)
            {
                var n = symbols[after];
                n.Prev = l;
                symbols[after] = n;
            }

            if (symbols[l].Prev >= 0) TryEnqueue(symbols[l].Prev);
            TryEnqueue(l);
        }

        for (int i = 0; i >= 0; i = symbols[i].Next) output.Add(symbols[i].Id);
    }

    private static long PairKey(int a, int b) => ((long)a << 32) | (uint)b;

    private static Dictionary<long, MergeInfo> LoadMerges(JsonElement merges, Dictionary<string, int> vocab)
    {
        var result = new Dictionary<long, MergeInfo>(merges.GetArrayLength());
        var rank = 0;
        foreach (var merge in merges.EnumerateArray())
        {
            string? a, b;
            if (merge.ValueKind == JsonValueKind.Array)
            {
                a = merge[0].GetString();
                b = merge[1].GetString();
            }
            else
            {
                var s = merge.GetString() ?? string.Empty;
                var space = s.IndexOf(' ');
                a = space > 0 ? s[..space] : null;
                b = space > 0 ? s[(space + 1)..] : null;
            }

            // Like Hugging Face: merges whose parts or result are missing from the vocabulary are skipped, but still consume a rank.
            if (a is not null && b is not null
                && vocab.TryGetValue(a, out var ia) && vocab.TryGetValue(b, out var ib)
                && vocab.TryGetValue(a + b, out var merged))
            {
                result.TryAdd(PairKey(ia, ib), new MergeInfo(rank, merged));
            }
            rank++;
        }
        return result;
    }

    private void LoadAddedTokens(JsonElement root, bool matchReservedTokensInText)
    {
        if (!root.TryGetProperty("added_tokens", out var added) || added.ValueKind != JsonValueKind.Array) return;

        foreach (var token in added.EnumerateArray())
        {
            var id = token.GetProperty("id").GetInt32();
            var content = token.GetProperty("content").GetString();
            if (string.IsNullOrEmpty(content)) continue;

            var special = token.TryGetProperty("special", out var sp) && sp.ValueKind == JsonValueKind.True;
            if (!matchReservedTokensInText && (special || GemmaSpecialTokens.IsReserved(id))) continue;

            var node = _addedTokens;
            foreach (var c in content)
            {
                node.Next ??= new Dictionary<char, TrieNode>();
                if (!node.Next.TryGetValue(c, out var child)) node.Next[c] = child = new TrieNode();
                node = child;
            }
            node.Id = id;
            _addedTokenFirstChars.Add(content[0]);
        }
    }

    private static string ReadSpaceReplacement(JsonElement root)
    {
        if (!root.TryGetProperty("normalizer", out var n) || n.ValueKind == JsonValueKind.Null)
            return " "; // no normaliser: spaces stay spaces

        if (n.TryGetProperty("type", out var type) && type.GetString() == "Replace"
            && n.TryGetProperty("pattern", out var pattern) && pattern.TryGetProperty("String", out var pat) && pat.GetString() == " "
            && n.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } replacement)
            return replacement;

        throw new NotSupportedException("Unsupported normalizer in tokenizer.json (only Replace \" \" → \"▁\" is implemented).");
    }

    private static void ValidatePreTokenizer(JsonElement root)
    {
        if (!root.TryGetProperty("pre_tokenizer", out var p) || p.ValueKind == JsonValueKind.Null) return;

        // Gemma: Split on " " (MergedWithPrevious) runs after the space→▁ normaliser, so it never splits anything.
        if (p.TryGetProperty("type", out var type) && type.GetString() == "Split"
            && p.TryGetProperty("pattern", out var pattern) && pattern.TryGetProperty("String", out var pat) && pat.GetString() == " ")
            return;

        throw new NotSupportedException("Unsupported pre_tokenizer in tokenizer.json (only the Gemma 'Split \" \"' no-op is implemented).");
    }
}
