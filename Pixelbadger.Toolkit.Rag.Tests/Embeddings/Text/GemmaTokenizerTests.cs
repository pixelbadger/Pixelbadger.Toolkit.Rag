using System.Text;
using FluentAssertions;
using Pixelbadger.Toolkit.Rag.Embeddings.Text;

namespace Pixelbadger.Toolkit.Rag.Tests.Embeddings.Text;

/// <summary>Behavioural tests of the BPE implementation on a tiny hand-made tokenizer.json (no model files needed).</summary>
public class GemmaTokenizerTests
{
    private const string TinyTokenizer = """
    {
      "added_tokens": [
        {"id": 0, "content": "<pad>", "special": true, "normalized": false},
        {"id": 1, "content": "<eos>", "special": true, "normalized": false},
        {"id": 2, "content": "<bos>", "special": true, "normalized": false},
        {"id": 3, "content": "<unk>", "special": true, "normalized": false},
        {"id": 11, "content": "\n", "special": false, "normalized": false},
        {"id": 12, "content": "\n\n", "special": false, "normalized": false},
        {"id": 15, "content": "<x>", "special": true, "normalized": false},
        {"id": 16, "content": "<tag>", "special": false, "normalized": false},
        {"id": 258880, "content": "<|image|>", "special": false, "normalized": false}
      ],
      "normalizer": {"type": "Replace", "pattern": {"String": " "}, "content": "▁"},
      "pre_tokenizer": {"type": "Split", "pattern": {"String": " "}, "behavior": "MergedWithPrevious", "invert": false},
      "post_processor": {"type": "TemplateProcessing", "single": [{"SpecialToken": {"id": "<bos>", "type_id": 0}}, {"Sequence": {"id": "A", "type_id": 0}}]},
      "model": {
        "type": "BPE", "unk_token": "<unk>", "byte_fallback": true, "fuse_unk": true,
        "vocab": {"<pad>": 0, "<eos>": 1, "<bos>": 2, "<unk>": 3, "▁": 4, "a": 5, "b": 6, "c": 7, "ab": 8, "▁a": 9, "abc": 10,
                  "\n": 11, "\n\n": 12, "<0xC3>": 13, "<0xA9>": 14, "<x>": 15, "<tag>": 16, "<|image|>": 258880},
        "merges": [["a", "b"], ["▁", "a"], ["ab", "c"]]
      }
    }
    """;

    private static GemmaTokenizer Tiny(bool matchReserved = false, string? json = null)
        => GemmaTokenizer.Load(new MemoryStream(Encoding.UTF8.GetBytes(json ?? TinyTokenizer)), matchReserved);

    [Theory]
    [InlineData("abc", new[] { 10 })]            // a,b,c -> ab,c -> abc
    [InlineData(" abc", new[] { 4, 10 })]        // space becomes ▁; ▁ + abc (the lower-rank a+b merge wins over ▁+a)
    [InlineData(" ab", new[] { 4, 8 })]          // merge rank 0 (a,b) beats rank 1 (▁,a)
    [InlineData("aab", new[] { 5, 8 })]
    [InlineData("c", new[] { 7 })]
    [InlineData("", new int[0])]
    public void Encodes_by_merge_rank(string text, int[] expected)
        => Tiny().Encode(text).Should().Equal(expected);

    [Fact]
    public void Byte_fallback_is_used_for_characters_outside_the_vocabulary()
        => Tiny().Encode("é").Should().Equal(13, 14);

    [Fact]
    public void Unknown_characters_without_bytes_become_a_single_fused_unk()
    {
        Tiny().Encode("z").Should().Equal(3);
        Tiny().Encode("zz").Should().Equal(3);
        Tiny().Encode("azb").Should().Equal(5, 3, 6);
    }

    [Fact]
    public void Added_tokens_match_leftmost_longest_on_raw_text()
    {
        Tiny().Encode("a\n\nb").Should().Equal(5, 12, 6);
        Tiny().Encode("a\nb").Should().Equal(5, 11, 6);
        Tiny().Encode("a\n\n\nb").Should().Equal(5, 12, 11, 6);
        Tiny().Encode("a<tag>b").Should().Equal(5, 16, 6);
    }

    [Fact]
    public void Reserved_and_special_tokens_in_text_are_not_matched_by_default()
    {
        // "<x>" is special, "<|image|>" has a reserved id: both must stay plain text so text can never forge placeholders.
        var tokenizer = Tiny();
        tokenizer.Encode("a<x>b").Should().Equal(5, 3, 6);
        tokenizer.Encode("<|image|>").Should().NotContain(258880);
        tokenizer.Encode("<eos><bos><pad>").Should().NotContain(new[] { 0, 1, 2 });
    }

    [Fact]
    public void Reserved_tokens_match_in_text_when_requested_like_hugging_face()
    {
        var tokenizer = Tiny(matchReserved: true);
        tokenizer.Encode("a<x>b").Should().Equal(5, 15, 6);
        tokenizer.Encode("<|image|>").Should().Equal(258880);
        tokenizer.Encode("<eos>").Should().Equal(1);
    }

    [Fact]
    public void Post_processor_template_is_ignored_so_no_bos_is_added()
        => Tiny().Encode("abc").Should().NotContain(GemmaSpecialTokens.Bos);

    [Fact]
    public void Merges_in_legacy_string_form_are_supported()
    {
        var json = TinyTokenizer.Replace("""[["a", "b"], ["▁", "a"], ["ab", "c"]]""", """["a b", "▁ a", "ab c"]""");
        json.Should().NotBe(TinyTokenizer);
        Tiny(json: json).Encode(" abc").Should().Equal(4, 10);
    }

    [Fact]
    public void Unsupported_normalizer_is_rejected_instead_of_silently_drifting()
    {
        var json = TinyTokenizer.Replace("\"type\": \"Replace\"", "\"type\": \"NFC\"");
        var act = () => Tiny(json: json);
        act.Should().Throw<NotSupportedException>().WithMessage("*normalizer*");
    }

    [Fact]
    public void Unsupported_model_type_is_rejected()
    {
        var json = TinyTokenizer.Replace("\"type\": \"BPE\"", "\"type\": \"Unigram\"");
        var act = () => Tiny(json: json);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Missing_file_throws_FileNotFoundException()
    {
        var act = () => GemmaTokenizer.Load(Path.Combine(Path.GetTempPath(), "does-not-exist", "tokenizer.json"));
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Exposes_vocabulary_lookup()
    {
        var tokenizer = Tiny();
        tokenizer.VocabularySize.Should().Be(18);
        tokenizer.TokenToId("abc").Should().Be(10);
        tokenizer.TokenToId("nope").Should().BeNull();
    }
}
