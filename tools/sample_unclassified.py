# -*- coding: utf-8 -*-
import json
from pathlib import Path

ROOT = Path("data/orders")
rows = []
for p in ROOT.iterdir():
    if not p.is_dir():
        continue
    cpath = p / "classification.json"
    mpath = p / "meta.json"
    if not cpath.exists() or not mpath.exists():
        continue
    cls = json.loads(cpath.read_text(encoding="utf-8"))
    prim = next((c for c in cls.get("categories", []) if c.get("isPrimary")), None)
    if not prim or prim.get("l2Id") != "g25c01":
        continue
    meta = json.loads(mpath.read_text(encoding="utf-8"))
    rows.append((p.name, meta.get("objectName") or meta.get("title") or ""))

Path("tools/unclassified_sample.txt").write_text(
    "\n".join(f"{r}\t{o}" for r, o in rows[:40]),
    encoding="utf-8",
)
print("unclassified", len(rows))
print(Path("tools/unclassified_sample.txt").read_text(encoding="utf-8")[:2500])
