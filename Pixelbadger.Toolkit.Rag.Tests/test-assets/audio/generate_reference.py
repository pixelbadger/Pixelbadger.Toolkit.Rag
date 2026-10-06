"""Generates logmel_reference.json: reference log-mel features from the real Hugging Face
Gemma4AudioFeatureExtractor (transformers/models/gemma4/feature_extraction_gemma4.py, the extractor
EmbeddingGemma 2 reuses). Needs only `pip install transformers numpy` (no model downloads).

Signals are defined by formula so the C# tests can regenerate them (float64 -> float32):
  tones:  0.5*sin(2*pi*440*t) + 0.25*sin(2*pi*3000*t), n=6400 (multiple of 128, no padding)
  chirp:  0.6*sin(2*pi*(100*t + 0.5*k*t^2)), k=(7000-100)/(n/16000), n=5000 (padded to 5120, masked frames)
  short:  0.3*sin(2*pi*1000*t), n=300 (tiny, a couple of frames)
Run: python generate_reference.py  (writes next to this script)
"""
import base64
import json
import os

import numpy as np
from transformers.models.gemma4.feature_extraction_gemma4 import Gemma4AudioFeatureExtractor

SR = 16000


def tones(n):
    t = np.arange(n) / SR
    return (0.5 * np.sin(2 * np.pi * 440 * t) + 0.25 * np.sin(2 * np.pi * 3000 * t)).astype(np.float32)


def chirp(n):
    t = np.arange(n) / SR
    k = (7000 - 100) / (n / SR)
    return (0.6 * np.sin(2 * np.pi * (100 * t + 0.5 * k * t * t))).astype(np.float32)


def short(n):
    t = np.arange(n) / SR
    return (0.3 * np.sin(2 * np.pi * 1000 * t)).astype(np.float32)


fe = Gemma4AudioFeatureExtractor()  # defaults == processor_config.json values in the reference doc
out = {"extractor": "Gemma4AudioFeatureExtractor", "cases": {}}
for name, gen, n in [("tones", tones, 6400), ("chirp", chirp, 5000), ("short", short, 300)]:
    r = fe([gen(n)])  # list form: the bare-ndarray path of the extractor squeezes wrongly
    feats = np.asarray(r["input_features"][0], dtype=np.float32)
    mask = np.asarray(r["input_features_mask"][0], dtype=bool)
    out["cases"][name] = {
        "samples": n,
        "frames": int(feats.shape[0]),
        "validFrames": int(mask.sum()),
        "mask": "".join("1" if m else "0" for m in mask),
        "features_f32_b64": base64.b64encode(feats.astype("<f4").tobytes()).decode(),
    }
    print(name, feats.shape, int(mask.sum()))
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "logmel_reference.json"), "w") as f:
    json.dump(out, f, separators=(",", ":"))
