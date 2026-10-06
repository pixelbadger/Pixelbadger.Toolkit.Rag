#!/usr/bin/env python3
"""Builds a tiny stand-in for the EmbeddingGemma 2 text graph (model.onnx) so the ONNX Runtime plumbing of
OnnxGemmaTextModel can be tested without the real 1 GB model:

    pip install onnx numpy
    python make_tiny_text_graph.py [out.onnx]

Same I/O contract as the real graph (reference section 3):
  inputs : input_ids int64[b, s], attention_mask int64[b, s],
           image_features / video_features / audio_features float32[n, 512]  (n may be 0)
  output : sentence_embedding float32[b, 768]
The value is a deterministic function of the masked ids and of the SUM of each feature input, so a test can tell
whether padding was masked out and whether image vs audio features were wired to the right inputs.

  row value = sum(ids * mask) + 1000 * sum(image_features) + 1000000 * sum(audio_features) + 0.001 * sum(video_features)
  sentence_embedding[b, j] = row value[b] + j
"""
import sys

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper


def main() -> None:
    out = sys.argv[1] if len(sys.argv) > 1 else "tiny_text_graph.onnx"

    ids = helper.make_tensor_value_info("input_ids", TensorProto.INT64, ["b", "s"])
    mask = helper.make_tensor_value_info("attention_mask", TensorProto.INT64, ["b", "s"])
    image = helper.make_tensor_value_info("image_features", TensorProto.FLOAT, ["ni", 512])
    video = helper.make_tensor_value_info("video_features", TensorProto.FLOAT, ["nv", 512])
    audio = helper.make_tensor_value_info("audio_features", TensorProto.FLOAT, ["na", 512])
    out_info = helper.make_tensor_value_info("sentence_embedding", TensorProto.FLOAT, ["b", 768])

    init = [
        numpy_helper.from_array(np.array([1], dtype=np.int64), "axis1"),
        numpy_helper.from_array(np.array([0, 1], dtype=np.int64), "axes01"),
        numpy_helper.from_array(np.array(1000.0, dtype=np.float32), "w_image"),
        numpy_helper.from_array(np.array(1000000.0, dtype=np.float32), "w_audio"),
        numpy_helper.from_array(np.array(0.001, dtype=np.float32), "w_video"),
        numpy_helper.from_array(np.arange(768, dtype=np.float32).reshape(1, 768), "arange"),
    ]

    nodes = [
        helper.make_node("Mul", ["input_ids", "attention_mask"], ["masked_ids"]),
        helper.make_node("Cast", ["masked_ids"], ["masked_f"], to=TensorProto.FLOAT),
        helper.make_node("ReduceSum", ["masked_f", "axis1"], ["row_sum"], keepdims=1),  # [b,1]
        helper.make_node("ReduceSum", ["image_features", "axes01"], ["image_sum"], keepdims=0),  # scalar
        helper.make_node("ReduceSum", ["video_features", "axes01"], ["video_sum"], keepdims=0),
        helper.make_node("ReduceSum", ["audio_features", "axes01"], ["audio_sum"], keepdims=0),
        helper.make_node("Mul", ["image_sum", "w_image"], ["image_term"]),
        helper.make_node("Mul", ["audio_sum", "w_audio"], ["audio_term"]),
        helper.make_node("Mul", ["video_sum", "w_video"], ["video_term"]),
        helper.make_node("Add", ["row_sum", "image_term"], ["t1"]),
        helper.make_node("Add", ["t1", "audio_term"], ["t2"]),
        helper.make_node("Add", ["t2", "video_term"], ["t3"]),
        helper.make_node("Add", ["t3", "arange"], ["sentence_embedding"]),  # [b,1] + [1,768] -> [b,768]
    ]

    graph = helper.make_graph(nodes, "tiny_text_graph", [ids, mask, image, video, audio], [out_info], initializer=init)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 17)])
    model.ir_version = 9
    onnx.checker.check_model(model)
    onnx.save(model, out)
    print("wrote", out)


if __name__ == "__main__":
    main()
