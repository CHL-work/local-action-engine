# Third-party components

The root Apache-2.0 license covers this repository's original code and generated
demo assets. Dependencies and separately downloaded models retain their own terms.

| Component | Source and license |
| --- | --- |
| Laya base checkpoint | [Pinned model card](https://huggingface.co/convaiinnovations/laya/blob/7b928d828b7b0e022f929d9bd2e44165aa270148/README.md), Apache-2.0 |
| Laya Python package | [Upstream](https://github.com/NandhaKishorM/laya), Apache-2.0; installed version 0.4.6 |
| ModernBERT encoder | [answerdotai/ModernBERT-large](https://huggingface.co/answerdotai/ModernBERT-large), Apache-2.0 |
| Parakeet Unified English 0.6B | [NVIDIA model card and license](https://huggingface.co/nvidia/parakeet-unified-en-0.6b), NVIDIA Open Model License |
| Parakeet ONNX export | [Pinned export](https://huggingface.co/csukuangfj2/sherpa-onnx-nemo-parakeet-unified-en-0.6b-non-streaming/tree/e69c9fb5322532e9995b116b6f1e4b34ab0c4b13), inherits model terms |
| sherpa-onnx | [Upstream](https://github.com/k2-fsa/sherpa-onnx), Apache-2.0 |
| PyTorch | [Upstream license](https://github.com/pytorch/pytorch/blob/main/LICENSE), BSD-style and included third-party notices |
| Transformers / tokenizers / safetensors | Hugging Face projects, Apache-2.0 |
| Ajv | [Upstream](https://github.com/ajv-validator/ajv), MIT |
| Node.js / Python | Respective upstream licenses; installed by the user, not redistributed here |
| Newtonsoft.Json and its Unity adaptations | MIT notices and copyrights are preserved in [the pinned package notice](licenses/unity-packages/com.unity.nuget.newtonsoft-json@3.2.2/Third%20Party%20Notices.md) |
| Unity Player and Unity packages | [Unity terms](https://unity.com/legal/terms-of-service); exact package license/third-party notice texts are explicitly included under [licenses/](licenses/README.md) in the Windows ZIP |

The [Unity package notice index](licenses/unity-packages/index.json) records the
resolved versions, upstream origins, source-relative filenames and byte hashes.
It includes the full resolved dependency notice collection, including some
build/test-only packages; listing a component does not claim it is linked into
the Player. The collection includes Newtonsoft's MIT notices, URP's FXAA notice,
render-pipeline core notices, and the referenced packages' own license texts.
These texts are included explicitly by the release allowlist rather than relying
on the Unity build to copy notices automatically. Built-in engine modules remain
subject to Unity's applicable runtime terms.

The six original checkpoint files are unmodified. The model ZIP adds LICENSE,
NOTICE and MODEL_CARD.md; its file and archive hashes are versioned in the
repository. No optimizer state or private data archive is distributed.

The generated FBX models and synthesized OGG effects are reproduced by the
project's own scripts in `art/`. No external art pack is included.
