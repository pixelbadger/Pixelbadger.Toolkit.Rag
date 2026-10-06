#!/usr/bin/env python3
"""Generates the tokenizer parity fixture for GemmaTokenizerParityTests.

    pip install tokenizers
    python tokenizer_parity.py <path/to/tokenizer.json> [out.json]

Expected ids come from the Hugging Face Rust tokenizer (Python binding) with
add_special_tokens=False, i.e. no <bos>/<eos> template. The C# side adds those once itself.
The set is deterministic (fixed seed) so regenerating against the same tokenizer.json is a no-op diff.
Re-run it against the tokenizer.json that ships with YOUR EmbeddingGemma 2 snapshot to confirm parity there.
"""
import hashlib
import json
import random
import sys

from tokenizers import Tokenizer

RESERVED_LOOKALIKES = [
    "<pad>", "<eos>", "<bos>", "<unk>", "<mask>", "<start_of_turn>", "<end_of_turn>",
    "<start_of_image>", "<end_of_image>", "<image_soft_token>", "<|image>", "<|image|>", "<image|>",
    "<|audio>", "<|audio|>", "<audio|>", "<|video|>", "<unused0>", "<unused2978>",
]


def curated() -> list[str]:
    s: list[str] = []

    # --- English / prompts (the EG2 formats) -----------------------------------------------------
    s += [
        "", " ", "a", "Hello world", "Hello, world!", " Hello world", "Hello world ", "  Hello  world  ",
        "task: search result | query: northern lights",
        "task: search result | query: Which planet is known as the Red Planet?",
        "title: none | text: Mars, known for its reddish appearance, is often referred to as the Red Planet.",
        "title: Annual Report 2025.pdf | text: Revenue grew 14% year over year, driven by subscriptions.",
        "The quick brown fox jumps over the lazy dog.",
        "It's a dog-eat-dog world; don't you think? (Yes!) \"Quoted\" text -- with dashes... and ellipses…",
        "NASA's James Webb telescope observed 2,500+ galaxies on 12/03/2024 at 14:35:59 UTC.",
        "Supercalifragilisticexpialidocious pneumonoultramicroscopicsilicovolcanoconiosis",
        "URL: https://example.com/path/to/page?query=string&other=1#fragment email@example.org",
    ]

    # --- Multilingual ------------------------------------------------------------------------------
    s += [
        "Die schnelle braune Fuchs springt über den faulen Hund. Größe, Straße, Übergrößenträger.",
        "Le renard brun rapide saute par-dessus le chien paresseux. Où est l'été? Ça va très bien.",
        "El veloz murciélago hindú comía feliz cardillo y kiwi. La cigüeña tocaba el saxofón.",
        "Съешь же ещё этих мягких французских булок, да выпей чаю.",
        "Γαζίες και μυρτιές δεν θα βρω πια στο χρυσαφί ξέφωτο.",
        "نص عربي للاختبار: مرحبا بالعالم، كيف حالك اليوم؟",
        "שלום עולם, זהו טקסט לבדיקה בעברית.",
        "हिन्दी में एक परीक्षण वाक्य: नमस्ते दुनिया, आप कैसे हैं?",
        "ภาษาไทยไม่มีการเว้นวรรคระหว่างคำ ทดสอบการตัดคำ",
        "日本語のテキストをテストします。吾輩は猫である。名前はまだ無い。",
        "한국어 문장을 테스트합니다. 안녕하세요, 세계!",
        "中文测试句子：你好，世界！今天天气很好。",
        "Tiếng Việt có nhiều dấu: Cộng hòa xã hội chủ nghĩa Việt Nam.",
        "Türkçe karakterler: ığüşöç İĞÜŞÖÇ, İstanbul'da yağmur yağıyor.",
        "Zażółć gęślą jaźń. Příliš žluťoučký kůň úpěl ďábelské ódy.",
        "Mixed: Hello 世界 мир שלום 🌍 مرحبا",
        "ǅ ǆ Ǉ ǈ ǉ ǋ ǌ ß ẞ ŉ ﬁ ﬂ ﬃ Å Å é é",  # compat / composed vs decomposed (no NFC applied)
        "e\u0301 e\u0301\u0302 \u0e01\u0e34\u0e49 \u0915\u094d\u0937",
    ]

    # --- Code ----------------------------------------------------------------------------------------
    s += [
        "public static async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)\n{\n    return await Task.FromResult(new float[256]);\n}",
        "def fib(n: int) -> int:\n    if n < 2:\n        return n\n    return fib(n - 1) + fib(n - 2)\n",
        "const sum = (a, b) => a + b;\nconsole.log(`sum=${sum(1, 2)}`);\n",
        "SELECT TOP (10) c.ChunkId, VECTOR_DISTANCE('cosine', c.Embedding, @q) AS distance FROM dbo.Chunks c ORDER BY distance;",
        '{"name": "pbrag", "version": "2.0.0", "tags": ["rag", "mcp"], "nested": {"a": [1, 2, 3], "b": null}}',
        "<html>\n  <head><title>Test</title></head>\n  <body><h1>Header</h1><p>Paragraph</p><table><tr><td>cell</td></tr></table></body>\n</html>",
        "# Heading\n\n- item one\n- item two\n\n```bash\ndotnet build && dotnet test\n```\n\n> quote\n",
        "if (a != b && c <= d || !e) { x += 1; y >>= 2; z ??= null; } // comment\n/* block */",
        "#include <stdio.h>\nint main(void) { printf(\"%d\\n\", 42); return 0; }",
        "\\begin{equation} E = mc^2 \\end{equation} $\\alpha + \\beta = \\gamma$",
        "C:\\Users\\name\\Documents\\file.txt /usr/local/bin/pbrag --index-path ./index",
        "0x1F4A9 0b1010 1e-9 3.14159 -42 +7 1_000_000 12345678901234567890",
        "\tindented\twith\ttabs\n\t\tdouble",
    ]

    # --- Emoji ------------------------------------------------------------------------------------------
    s += [
        "😀", "😀😃😄", "I love 🍕 and 🍣!", "👨‍👩‍👧‍👦 family", "👍🏽 skin tone", "🇬🇧🇫🇷🇯🇵 flags", "❤️ heart ✨ sparkles",
        "🧑‍💻 developer", "🏳️‍🌈 pride", "\U0001F9EC dna \U0001FAE0 melting", "\U0001D54F math double-struck \U0001D7D8",
        "\U00010348 gothic \U00013000 hieroglyph \U0001F600",
    ]

    # --- Whitespace runs ----------------------------------------------------------------------------------
    for n in (1, 2, 3, 4, 7, 8, 15, 16, 31, 32, 33, 64):
        s.append("a" + " " * n + "b")
    for n in (1, 2, 3, 5, 10, 20, 31, 32, 33, 40):
        s.append("para one" + "\n" * n + "para two")
    s += [
        "\n", "\n\n", "\n\n\n\n", "\r\n", "line1\r\nline2\r\n", "line1\rline2", "\t", "\t\t\t", " \t \n \t ",
        "trailing spaces   ", "   leading spaces", "tabs\tand\nnewlines\r\nmixed", "nbsp\u00a0here", "en\u2002em\u2003thin\u2009space",
        "zero\u200bwidth\u200cjoiner\u200d here", "line\u2028separator\u2029paragraph", "bom\ufeffinside", "vertical\u000btab\u000cformfeed",
        "\u3000ideographic space\u3000", " ▁ literal lower-eighth-block ▁▁",
    ]

    # --- Added tokens / reserved lookalikes (HF matches these in raw text) ---------------------------------------
    for tok in RESERVED_LOOKALIKES:
        s.append(tok)
        s.append(f"before {tok} after")
        s.append(f"{tok}{tok}")
    s += ["<table><tr><td>", "<h1>title</h1>", "<unused5><unused6>", "[multimodal]", "<mask> fill", "<start_of_turn>user\nhi<end_of_turn>\n<start_of_turn>model\n"]

    # --- Control / rare ------------------------------------------------------------------------------------------
    s += [
        "\x00\x01\x02\x1f\x7f", "\x1b[31mred\x1b[0m", "\ue000\ue001 private use", "\uffff\ufffe noncharacters", "\ufffd replacement",
        "𝕳𝖊𝖑𝖑𝖔 𝔴𝔬𝔯𝔩𝔡", "①②③ ⅷ ㎏ ℃", "ａｂｃ　ＡＢＣ　１２３ fullwidth", "ᚠᚢᚦᚨᚱᚲ runic", "☃ ☂ ★ ♠ ♣ ♥ ♦ ∑ ∫ √ ∞ ≠ ≤ ≥",
    ]

    # --- Long / repetitive ----------------------------------------------------------------------------------------
    s += [
        "word " * 200, "a" * 300, "ab" * 150, "The cat. " * 120,
        " ".join(["token"] * 600),
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. " * 20,
    ]
    return s


def fuzz(rng: random.Random, count: int) -> list[str]:
    # Valid scalar values only (no lone surrogates, which cannot be round-tripped through UTF-8 JSON).
    blocks = [
        (0x20, 0x7E), (0xA0, 0x24F), (0x370, 0x3FF), (0x400, 0x4FF), (0x5D0, 0x5EA), (0x600, 0x6FF), (0x900, 0x97F),
        (0xE00, 0xE7F), (0x1100, 0x11FF), (0x2000, 0x206F), (0x2190, 0x23FF), (0x2500, 0x27BF), (0x3040, 0x30FF),
        (0x4E00, 0x4FFF), (0xAC00, 0xAD00), (0x1F300, 0x1F64F), (0x1F900, 0x1F9FF), (0x20000, 0x2000F), (0xE000, 0xE0FF),
    ]
    out = []
    for _ in range(count):
        length = rng.choice([1, 2, 3, 5, 8, 13, 21, 34, 55, 89])
        chars = []
        for _ in range(length):
            roll = rng.random()
            if roll < 0.15:
                chars.append(" ")
            elif roll < 0.18:
                chars.append(rng.choice(["\n", "\t", "\r\n", "  ", "\n\n"]))
            else:
                lo, hi = rng.choice(blocks)
                chars.append(chr(rng.randint(lo, hi)))
        out.append("".join(chars))
    return out


def ascii_words(rng: random.Random, count: int) -> list[str]:
    syll = ["ka", "to", "mi", "ra", "sen", "tion", "ing", "ex", "pro", "con", "un", "re", "al", "ous", "ment", "ly", "th", "qu", "x", "z"]
    punct = [" ", " ", " ", ", ", ". ", "; ", " - ", "(", ")", "\n", "\n\n", "  "]
    out = []
    for _ in range(count):
        parts = []
        for _ in range(rng.randint(3, 60)):
            parts.append("".join(rng.choice(syll) for _ in range(rng.randint(1, 4))))
            parts.append(rng.choice(punct))
        out.append("".join(parts))
    return out


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    path = sys.argv[1]
    out_path = sys.argv[2] if len(sys.argv) > 2 else "tokenizer-parity.json"

    tok = Tokenizer.from_file(path)
    raw = open(path, "rb").read()

    rng = random.Random(20261006)
    texts = curated() + ascii_words(rng, 250) + fuzz(rng, 600)

    seen = set()
    cases = []
    for t in texts:
        if t in seen:
            continue
        seen.add(t)
        enc = tok.encode(t, add_special_tokens=False)
        cases.append({
            "text": t,
            "ids": enc.ids,
            "hasReservedLookalike": any(r in t for r in RESERVED_LOOKALIKES),
        })

    doc = {
        "generator": "tools/golden/tokenizer_parity.py",
        "tokenizersVersion": __import__("tokenizers").__version__,
        "tokenizerSha256": hashlib.sha256(raw).hexdigest(),
        "vocabSize": tok.get_vocab_size(),
        "addSpecialTokens": False,
        "cases": cases,
    }
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=True, separators=(",", ":"))
    print(f"wrote {len(cases)} cases to {out_path}")


if __name__ == "__main__":
    main()
