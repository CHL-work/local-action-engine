"""Builds the demo models: FBX files for Unity plus optional previews and .blend files.

Usage (with Blender's Python, or the `bpy` wheel):
    python art/models/build_models.py [names...] [--preview DIR] [--blend DIR]
Writes unity/Assets/Brainwash/Resources/Models/<name>.fbx and palette.json.
"""
import argparse
import json
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import bpy  # noqa: E402
import bwkit  # noqa: E402
import creatures  # noqa: E402

import glob  # noqa: E402
import importlib  # noqa: E402

REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(REPO, "unity", "Assets", "Brainwash", "Resources", "Models")


def recipes():
    """creatures.py plus every recipes_*.py module next to this script."""
    table = dict(creatures.RECIPES)
    for path in sorted(glob.glob(os.path.join(HERE, "recipes_*.py"))):
        name = os.path.splitext(os.path.basename(path))[0]
        try:
            module = importlib.import_module(name)
            table.update(module.RECIPES)
        except Exception as error:  # one broken recipe file must not stop the others
            print(f"warning: skipped {name}: {error!r}", file=sys.stderr)
    return table


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("names", nargs="*")
    ap.add_argument("--preview", default=None)
    ap.add_argument("--blend", default=None)
    ap.add_argument("--no-fbx", action="store_true")
    ap.add_argument("--pose", action="store_true", help="also render a bent test pose (<name>.pose.png)")
    args = ap.parse_args()
    table = recipes()
    names = args.names or list(table)
    palette_png = os.path.join(HERE, "palette.png")
    bwkit.write_palette(png_path=palette_png, json_path=os.path.join(OUT, "palette.json"))
    index_path = os.path.join(OUT, "models.json")
    index = {}
    if os.path.exists(index_path):
        with open(index_path) as f:
            index = {m["id"]: m for m in json.load(f).get("models", [])}
    for name in names:
        t = time.time()
        bwkit.reset_scene()
        model = table[name]()
        rig, objects = bwkit.build_model(model, palette_png)
        tris = bwkit.stats(objects)
        lo, hi = bwkit.model_bounds(objects)
        size = hi - lo
        index[name] = {"id": name, "height": round(model.height or bwkit.model_height(objects), 4), "gait": model.gait, "kind": model.kind,
                       # Unity axes: x, y (up), z
                       "size": [round(float(size[0]), 4), round(float(size[2]), 4), round(float(size[1]), 4)],
                       "center": [round(float(-(lo[0] + hi[0]) / 2), 4), round(float((lo[2] + hi[2]) / 2), 4), round(float(-(lo[1] + hi[1]) / 2), 4)],
                       "triangles": tris}
        if not args.no_fbx:
            bwkit.export_fbx(os.path.join(OUT, name + ".fbx"))
        if args.blend:
            os.makedirs(args.blend, exist_ok=True)
            bpy.ops.wm.save_as_mainfile(filepath=os.path.join(args.blend, name + ".blend"))
        if args.preview:
            os.makedirs(args.preview, exist_ok=True)
            if getattr(model, "preview_tint", None):
                bwkit.set_preview_tint(model.preview_tint)
            height = model.preview_height or model.height or bwkit.model_height(objects) or 1.0
            focus, floor_z = None, 0.0
            if model.kind != "creature":
                lo, hi = bwkit.model_bounds(objects)
                height = float(max(hi - lo))
                focus, floor_z = tuple((lo + hi) / 2), float(lo[2])
            bwkit.render_preview(os.path.join(args.preview, name + ".png"), height=height, focus=focus, floor_z=floor_z)
            if args.pose:
                bwkit.apply_test_pose(rig)
                bwkit.render_preview(os.path.join(args.preview, name + ".pose.png"), height=height, focus=focus, floor_z=floor_z)
        print(f"{name}: {tris} triangles, {len(objects)} objects, {time.time() - t:.1f}s", flush=True)
    if not args.no_fbx:
        with open(index_path, "w") as f:
            json.dump({"models": [index[k] for k in sorted(index)]}, f, indent=1)


if __name__ == "__main__":
    main()
