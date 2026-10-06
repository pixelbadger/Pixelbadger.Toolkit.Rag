#!/usr/bin/env node
// Golden fixture generator for the EmbeddingGemma 2 C# port (reference doc section 9).
//
// transformers.js runs the reference processor + the SAME ONNX files, so its tensors are the oracle for every
// [verify] item (patch order, position-id order, mel scale, clip segmentation, placeholder framing, ...).
//
//   cd tools/golden && npm install
//   node generate.mjs --model /path/to/embeddinggemma-2-ONNX          # local snapshot (recommended)
//   node generate.mjs --inputs-only                                   # only (re)write the synthetic media inputs
//
// Options / env:
//   --model <dir|hf-id>   default: $PBRAG_MODEL_PATH, else onnx-community/embeddinggemma-2-ONNX (needs hub access)
//   --out <dir>           default: ../../Pixelbadger.Toolkit.Rag.Tests/test-assets/golden
//   --dtype <fp32|...>    default fp32 (never fp16 activations)
//
// Output (all under --out):
//   manifest.json                        provenance (model, versions, date)
//   txt.texts.json                       the exact queries/documents that were embedded
//   img-square.png, img-wide.png         synthetic, deterministic images (768x768 and 640x384)
//   aud-short.wav, aud-multiclip.wav     synthetic, deterministic 16 kHz mono 16-bit speech-like audio (5.0 s and 30.0 s)
//   <case>.<tensor>.json + .bin.gz       tensor metadata ({dims,type}) + little-endian raw data, gzip-compressed
// where <case> is txt | img-square | img-wide | aud-short | aud-multiclip and <tensor> is a processor output
// (input_ids, attention_mask, pixel_values, pixel_position_ids, input_features, input_features_mask, ...) or "embedding".
//
// Commit the generated files; the C# golden tests (Pixelbadger.Toolkit.Rag.Tests/Golden) skip when they are absent.

import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));

// ---------------------------------------------------------------------------------------------------------------
// args
// ---------------------------------------------------------------------------------------------------------------
const args = process.argv.slice(2);
const flag = (name) => args.includes(name);
const opt = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : fallback;
};

const modelId = opt("--model", process.env.PBRAG_MODEL_PATH || "onnx-community/embeddinggemma-2-ONNX");
const outDir = path.resolve(opt("--out", path.join(here, "../../Pixelbadger.Toolkit.Rag.Tests/test-assets/golden")));
const dtype = opt("--dtype", "fp32");
const inputsOnly = flag("--inputs-only");
fs.mkdirSync(outDir, { recursive: true });

// ---------------------------------------------------------------------------------------------------------------
// Deterministic synthetic inputs (no external assets, bit-identical on every run)
// ---------------------------------------------------------------------------------------------------------------
function xorshift32(seed) {
  let s = seed >>> 0 || 1;
  return () => {
    s ^= s << 13; s >>>= 0;
    s ^= s >>> 17;
    s ^= s << 5; s >>>= 0;
    return s / 0x100000000;
  };
}

const crcTable = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();
const crc32 = (buf) => {
  let c = 0xffffffff;
  for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
};

function pngChunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

function writePng(file, width, height, pixel /* (x,y) => [r,g,b] */) {
  const raw = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width * 3 + 1)] = 0; // filter: none
    for (let x = 0; x < width; x++) {
      const [r, g, b] = pixel(x, y);
      const o = y * (width * 3 + 1) + 1 + x * 3;
      raw[o] = r; raw[o + 1] = g; raw[o + 2] = b;
    }
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0); ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0; // 8-bit RGB
  fs.writeFileSync(file, Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    pngChunk("IHDR", ihdr),
    pngChunk("IDAT", zlib.deflateSync(raw, { level: 9 })),
    pngChunk("IEND", Buffer.alloc(0)),
  ]));
}

// Smooth gradients + a few hard-edged shapes + mild texture: exercises resize filters and patch ordering
// (the image is deliberately NOT symmetric, so x/y and row/column mix-ups change the tensors).
function scene(width, height, seed) {
  const rnd = xorshift32(seed);
  const noise = new Uint8Array(width * height).map(() => Math.floor(rnd() * 24));
  return (x, y) => {
    const u = x / (width - 1), v = y / (height - 1);
    let r = 255 * u, g = 255 * v, b = 255 * (1 - u) * (0.4 + 0.6 * v);
    if ((x - width * 0.30) ** 2 + (y - height * 0.35) ** 2 < (Math.min(width, height) * 0.18) ** 2) { r = 230; g = 40; b = 40; }
    if (x > width * 0.55 && x < width * 0.85 && y > height * 0.60 && y < height * 0.90) { r = 20; g = 90; b = 230; }
    if (y > height * 0.08 && y < height * 0.12 && x > width * 0.1) { r = 250; g = 250; b = 30; }
    const n = noise[y * width + x];
    return [Math.min(255, r + n) | 0, Math.min(255, g + n / 2) | 0, Math.min(255, b + n / 3) | 0];
  };
}

// Speech-like: a glottal-ish harmonic source with slowly moving pitch, formant-ish spectral tilt switching per
// "syllable" (~4 Hz), pauses, and light noise. Energy is spread over the whole 0-8 kHz mel range.
function synthSpeech(seconds, seed) {
  const sr = 16000, n = Math.round(seconds * sr);
  const rnd = xorshift32(seed);
  const out = new Int16Array(n);
  let phase = 0;
  for (let i = 0; i < n; i++) {
    const t = i / sr;
    const syllable = Math.floor(t * 4);
    const inSyllable = (t * 4) % 1;
    const envelope = Math.sin(Math.PI * inSyllable) ** 2 * (syllable % 7 === 6 ? 0.05 : 1); // every 7th syllable is a pause
    const f0 = 110 + 40 * Math.sin(2 * Math.PI * 0.35 * t) + 12 * (syllable % 5);
    phase += (2 * Math.PI * f0) / sr;
    const formant = 500 + 250 * ((syllable * 37) % 11) / 10; // Hz
    let s = 0;
    for (let h = 1; h <= 40; h++) {
      const f = h * f0;
      if (f > 7800) break;
      const gain = Math.exp(-((f - formant) ** 2) / (2 * 300 ** 2)) + 0.6 * Math.exp(-((f - 2.4 * formant) ** 2) / (2 * 500 ** 2)) + 0.02;
      s += gain * Math.sin(h * phase) / h ** 0.5;
    }
    s = 0.35 * envelope * s + 0.01 * (rnd() * 2 - 1);
    out[i] = Math.max(-32768, Math.min(32767, Math.round(s * 32767)));
  }
  return out;
}

function writeWav(file, samples /* Int16Array */, sampleRate = 16000) {
  const data = Buffer.from(samples.buffer, samples.byteOffset, samples.byteLength);
  const header = Buffer.alloc(44);
  header.write("RIFF", 0); header.writeUInt32LE(36 + data.length, 4); header.write("WAVE", 8);
  header.write("fmt ", 12); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(1, 22);
  header.writeUInt32LE(sampleRate, 24); header.writeUInt32LE(sampleRate * 2, 28); header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34);
  header.write("data", 36); header.writeUInt32LE(data.length, 40);
  fs.writeFileSync(file, Buffer.concat([header, data]));
}

// Decode the WAV exactly like `ffmpeg -ac 1 -ar 16000 -f f32le` does for 16-bit PCM: sample / 32768.
function readWavAsFloat32(file) {
  const buf = fs.readFileSync(file);
  const samples = new Int16Array(buf.buffer, buf.byteOffset + 44, (buf.length - 44) / 2);
  return Float32Array.from(samples, (s) => s / 32768);
}

const files = {
  imgSquare: path.join(outDir, "img-square.png"),
  imgWide: path.join(outDir, "img-wide.png"),
  audShort: path.join(outDir, "aud-short.wav"),
  audMulti: path.join(outDir, "aud-multiclip.wav"),
};

writePng(files.imgSquare, 768, 768, scene(768, 768, 1));
writePng(files.imgWide, 640, 384, scene(640, 384, 2));
writeWav(files.audShort, synthSpeech(5.0, 3));
writeWav(files.audMulti, synthSpeech(30.0, 4)); // exactly one 30 s window = three 11.2 s clips along the clips axis
console.log("wrote synthetic inputs to", outDir);
if (inputsOnly) process.exit(0);

// ---------------------------------------------------------------------------------------------------------------
// Reference run (transformers.js)
// ---------------------------------------------------------------------------------------------------------------
const { AutoProcessor, AutoModel, load_image, env, version } = await import("@huggingface/transformers");
if (fs.existsSync(modelId)) { env.allowRemoteModels = false; env.localModelPath = path.dirname(path.resolve(modelId)); }

// [verify] Output tensor names differ between transformers.js releases / processors. Map them to the names the
// C# golden tests read. Add entries here instead of editing the C# side.
const ALIASES = {
  pixel_values: "pixel_values",
  pixel_position_ids: "pixel_position_ids",
  image_position_ids: "pixel_position_ids",
  input_features: "input_features",
  input_features_mask: "input_features_mask",
  audio_attention_mask: "input_features_mask",
  input_ids: "input_ids",
  attention_mask: "attention_mask",
};

const TYPE_NAMES = { float32: "float32", int64: "int64", bool: "bool", int32: "int32", uint8: "uint8" };

function dump(caseName, tensorName, tensor) {
  const type = TYPE_NAMES[tensor.type];
  if (!type) throw new Error(`unsupported tensor type ${tensor.type} for ${caseName}.${tensorName}`);
  const base = path.join(outDir, `${caseName}.${tensorName}`);
  let bytes;
  switch (type) {
    case "float32": bytes = Buffer.from(Float32Array.from(tensor.data).buffer); break;
    case "int64": bytes = Buffer.from(BigInt64Array.from(Array.from(tensor.data, (x) => BigInt(x))).buffer); break;
    case "int32": bytes = Buffer.from(Int32Array.from(tensor.data).buffer); break;
    case "bool": case "uint8": bytes = Buffer.from(Uint8Array.from(Array.from(tensor.data, (x) => (x ? 1 : 0)))); break;
  }
  fs.writeFileSync(`${base}.json`, JSON.stringify({ dims: Array.from(tensor.dims), type }));
  fs.writeFileSync(`${base}.bin.gz`, zlib.gzipSync(bytes, { level: 9 }));
  console.log(`  ${caseName}.${tensorName}  [${Array.from(tensor.dims)}] ${type}`);
}

function dumpProcessorOutputs(caseName, outputs) {
  for (const [key, tensor] of Object.entries(outputs)) {
    if (!tensor || typeof tensor !== "object" || !("dims" in tensor)) continue;
    dump(caseName, ALIASES[key] ?? key, tensor);
  }
}

const processor = await AutoProcessor.from_pretrained(modelId);
const model = await AutoModel.from_pretrained(modelId, { device: "cpu", dtype });

async function embeddingOf(inputs) {
  const out = await model(inputs);
  return out.sentence_embedding; // [batch, 768], L2-normalised, full width: the C# side truncates to 256 + re-normalises
}

// ---- text: queries use the search prompt, documents the title/text prompt (reference section 5) ----------------------------------
const queries = [
  "Which planet is known as the Red Planet?",
  "northern lights",
  "wie funktioniert ein Verbrennungsmotor?",
  "日本の首都はどこですか",
  "def fibonacci(n):",
];
const documents = [
  { title: null, text: "Venus is often called Earth's twin because of its similar size and proximity." },
  { title: null, text: "Mars, known for its reddish appearance, is often referred to as the Red Planet." },
  { title: null, text: "Jupiter, the largest planet in our solar system, has a prominent red spot." },
  { title: null, text: "Saturn, famous for its rings, is sometimes mistaken for the Red Planet." },
  { title: "Aurora.md", text: "The aurora borealis is caused by charged solar particles colliding with the upper atmosphere near the poles." },
  { title: "Motor.txt", text: "Ein Viertaktmotor arbeitet in vier Takten: Ansaugen, Verdichten, Arbeiten und Ausstoßen. 🚗" },
  { title: "fib.py", text: "def fibonacci(n):\n    return n if n < 2 else fibonacci(n - 1) + fibonacci(n - 2)\n" },
  { title: "Tokyo.md", text: "東京は日本の首都であり、世界最大級の都市圏の一つです。" },
];
fs.writeFileSync(path.join(outDir, "txt.texts.json"), JSON.stringify({ queries, documents }, null, 2));

const prompts = [
  ...queries.map((q) => `task: search result | query: ${q}`),
  ...documents.map((d) => `title: ${d.title ?? "none"} | text: ${d.text}`),
];
console.log("txt");
const txtInputs = await processor(prompts);
dumpProcessorOutputs("txt", txtInputs);
dump("txt", "embedding", await embeddingOf(txtInputs));

// ---- image -------------------------------------------------------------------------------------------------------------------------------
// [verify] The processor call signature for multimodal input follows the reference doc (text, image, audio).
for (const [name, file] of [["img-square", files.imgSquare], ["img-wide", files.imgWide]]) {
  console.log(name);
  const image = await load_image(file);
  const inputs = await processor(null, image);
  dumpProcessorOutputs(name, inputs);
  dump(name, "embedding", await embeddingOf(inputs));
}

// ---- audio -------------------------------------------------------------------------------------------------------------------------------
for (const [name, file] of [["aud-short", files.audShort], ["aud-multiclip", files.audMulti]]) {
  console.log(name);
  const pcm = readWavAsFloat32(file);
  const inputs = await processor(null, null, pcm);
  dumpProcessorOutputs(name, inputs);
  dump(name, "embedding", await embeddingOf(inputs));
}

fs.writeFileSync(path.join(outDir, "manifest.json"), JSON.stringify({
  generator: "tools/golden/generate.mjs",
  model: modelId,
  dtype,
  transformersJs: version,
  node: process.version,
  generatedUtc: new Date().toISOString(),
}, null, 2));
console.log("done ->", outDir);
