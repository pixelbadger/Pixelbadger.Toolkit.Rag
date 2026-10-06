#!/usr/bin/env python3
"""Regenerates the vision fixtures (resize_cases.json, images/*, e2e_cases.json, vision_stub.onnx).

    pip install pillow numpy onnx
    python generate_fixtures.py

Source pixels are produced by `synth()`, which the C# tests mirror exactly (SynthImage.Rgb).
`get_aspect_ratio_preserving_size` and `convert_image_to_patches` are copied from
transformers/models/gemma4/image_processing_pil_gemma4.py (transformers 5.19.0).
"""
import base64, hashlib, json, math, os
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))


def synth(w, h):
    """Deterministic RGB test pattern; keep in sync with SynthImage.Rgb in the C# tests."""
    a = np.zeros((h, w, 3), dtype=np.uint8)
    for y in range(h):
        for x in range(w):
            a[y, x, 0] = (x * 37 + y * 11) & 255
            a[y, x, 1] = (y * 91 + (x * y) % 251) & 255
            a[y, x, 2] = ((x ^ y) * 53 + 7) & 255
    return a


def get_aspect_ratio_preserving_size(height, width, patch_size, max_patches, pooling_kernel_size):
    total_px = height * width
    target_px = max_patches * (patch_size**2)
    factor = math.sqrt(target_px / total_px)
    ideal_height = factor * height
    ideal_width = factor * width
    side_mult = pooling_kernel_size * patch_size
    target_height = int(math.floor(ideal_height / side_mult)) * side_mult
    target_width = int(math.floor(ideal_width / side_mult)) * side_mult
    if target_height == 0 and target_width == 0:
        raise ValueError("0x0")
    max_side_length = (max_patches // pooling_kernel_size**2) * side_mult
    if target_height == 0:
        target_height = side_mult
        target_width = min(int(math.floor(width / height)) * side_mult, max_side_length)
    elif target_width == 0:
        target_width = side_mult
        target_height = min(int(math.floor(height / width)) * side_mult, max_side_length)
    if target_height * target_width > target_px:
        raise ValueError("exceeds")
    return target_height, target_width


def convert_image_to_patches(image, patch_size):
    c, h, w = image.shape
    nh, nw = h // patch_size, w // patch_size
    p = image.reshape(c, nh, patch_size, nw, patch_size).transpose(1, 3, 2, 4, 0)
    return p.reshape(nh * nw, -1)


def b64(a):
    return base64.b64encode(np.ascontiguousarray(a).tobytes()).decode()


def process(pil, max_soft_tokens=280, patch=16, pool=3):
    """Mirror of Gemma4ImageProcessorPil._preprocess for one PIL image (after convert('RGB'))."""
    pil = pil.convert("RGB")
    w, h = pil.size
    th, tw = get_aspect_ratio_preserving_size(h, w, patch, max_soft_tokens * pool**2, pool)
    if (th, tw) != (h, w):
        pil = pil.resize((tw, th), resample=Image.BICUBIC)
    arr = np.array(pil)  # H,W,C uint8
    chw = arr.transpose(2, 0, 1)
    scaled = (chw.astype(np.float64) * (1 / 255)).astype(np.float32)
    patches = convert_image_to_patches(scaled, patch)
    gx, gy = np.meshgrid(np.arange(tw // patch), np.arange(th // patch), indexing="xy")
    pos = np.stack([gx, gy], axis=-1).reshape(patches.shape[0], 2).astype(np.int64)
    return th, tw, arr, patches, pos


def main():
    # 1. raw resize cases (up- and down-scaling, one axis only, tiny sources)
    cases = []
    for (sw, sh, ow, oh) in [(17, 13, 8, 6), (20, 30, 48, 48), (100, 70, 48, 144), (64, 64, 96, 48),
                             (33, 7, 33, 20), (9, 40, 5, 40), (200, 120, 48, 48), (5, 5, 48, 96),
                             (150, 150, 96, 96), (97, 53, 96, 48), (100, 70, 144, 96)]:
        out = np.array(Image.fromarray(synth(sw, sh)).resize((ow, oh), resample=Image.BICUBIC))
        cases.append(dict(srcW=sw, srcH=sh, outW=ow, outH=oh, rgbBase64=b64(out)))
    json.dump(cases, open(os.path.join(HERE, "resize_cases.json"), "w"), separators=(",", ":"))

    # 2. end-to-end image files: decode (RGB / RGBA / gray / palette), resize rule, patchify
    os.makedirs(os.path.join(HERE, "images"), exist_ok=True)
    rgb = synth(100, 70)
    imgs = {}
    imgs["rgb_100x70.png"] = Image.fromarray(rgb)
    alpha = ((np.arange(100)[None, :] * 2 + np.arange(70)[:, None]) % 256).astype(np.uint8)
    imgs["rgba_100x70.png"] = Image.fromarray(np.dstack([rgb, alpha]), "RGBA")
    imgs["gray_100x70.png"] = Image.fromarray(rgb[:, :, 0], "L")
    imgs["palette_100x70.png"] = Image.fromarray(rgb).quantize(colors=16, dither=Image.Dither.NONE)
    imgs["wide_300x40.png"] = Image.fromarray(synth(300, 40))
    imgs["tiny_7x5.png"] = Image.fromarray(synth(7, 5))
    e2e = []
    for name, im in imgs.items():
        path = os.path.join(HERE, "images", name)
        im.save(path, optimize=True)
        for budget in (70, 280):
            th, tw, arr, patches, pos = process(Image.open(path), budget)
            e2e.append(dict(file=name, maxSoftTokens=budget, height=th, width=tw,
                            rgbSha256=hashlib.sha256(arr.tobytes()).hexdigest(),
                            pixelValuesSha256=hashlib.sha256(patches.tobytes()).hexdigest(),
                            positionIdsSha256=hashlib.sha256(pos.tobytes()).hexdigest(),
                            numPatches=int(patches.shape[0])))
    json.dump(e2e, open(os.path.join(HERE, "e2e_cases.json"), "w"), indent=1)

    # 3. EXIF-oriented JPEG (orientation 6: display rotated 90 deg CW). Stored 96x48 quadrants.
    q = np.zeros((48, 96, 3), np.uint8)
    q[:24, :48] = (255, 0, 0); q[:24, 48:] = (0, 255, 0); q[24:, :48] = (0, 0, 255); q[24:, 48:] = (255, 255, 0)
    ex = Image.Exif(); ex[0x0112] = 6
    Image.fromarray(q).save(os.path.join(HERE, "images", "exif_rot6.jpg"), quality=95, subsampling=0, exif=ex)

    make_stub_model()


def make_stub_model():
    """Tiny stand-in for vision_encoder.onnx exercising only the I/O plumbing:
    image_features[r] = mean of all pixel_values of patches 9r..9r+8, shape [P/9, 512]."""
    import onnx
    from onnx import TensorProto as T, helper as H
    pv = H.make_tensor_value_info("pixel_values", T.FLOAT, [1, "P", 768])
    pp = H.make_tensor_value_info("pixel_position_ids", T.INT64, [1, "P", 2])
    out = H.make_tensor_value_info("image_features", T.FLOAT, ["N", 512])
    nodes = [
        H.make_node("Constant", [], ["shape1"], value=H.make_tensor("s1", T.INT64, [2], [-1, 768])),
        H.make_node("Reshape", ["pixel_values", "shape1"], ["flat"]),
        H.make_node("Constant", [], ["wshape"], value=H.make_tensor("ws", T.INT64, [2], [768, 512])),
        H.make_node("ConstantOfShape", ["wshape"], ["w"], value=H.make_tensor("wv", T.FLOAT, [1], [1.0 / 768])),
        H.make_node("MatMul", ["flat", "w"], ["proj"]),
        H.make_node("Constant", [], ["shape2"], value=H.make_tensor("s2", T.INT64, [3], [-1, 9, 512])),
        H.make_node("Reshape", ["proj", "shape2"], ["grp"]),
        H.make_node("ReduceMean", ["grp"], ["image_features"], axes=[1], keepdims=0),
    ]
    g = H.make_graph(nodes, "vision_stub", [pv, pp], [out])
    m = H.make_model(g, opset_imports=[H.make_opsetid("", 13)], ir_version=8)
    onnx.checker.check_model(m)
    onnx.save(m, os.path.join(HERE, "vision_stub.onnx"))


if __name__ == "__main__":
    main()
