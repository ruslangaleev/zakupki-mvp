# -*- coding: utf-8 -*-
from __future__ import annotations

import json
from pathlib import Path

ROOT = Path("data/orders")
CASES = [
    "0102200001626002634",  # drugs -> was telecom
    "0102200001626002641",  # drugs
    "0108500000426006099",  # reagents -> was electro
    "0112200000826005187",  # dopamine -> was construction
    "0112200000826005188",  # reagents -> was construction
    "0112200000826005210",  # food -> was construction
    "0112200000826005205",  # herbicide disposal
]

OUT = Path("tools/verify_reclass_out.txt")
lines = []
for reg in CASES:
    d = ROOT / reg
    meta = json.loads((d / "meta.json").read_text(encoding="utf-8"))
    cls = json.loads((d / "classification.json").read_text(encoding="utf-8"))
    prim = next(c for c in cls["categories"] if c.get("isPrimary"))
    lines.append(
        f"{reg}\n  OBJ: {meta.get('objectName')}\n  -> {prim['l1Id']} / {prim['l2Id']} {prim['l2Name']} (score={prim.get('score')})\n"
    )

# distribution
from collections import Counter

by_l1 = Counter()
unclassified = 0
for p in ROOT.iterdir():
    if not p.is_dir():
        continue
    cpath = p / "classification.json"
    if not cpath.exists():
        continue
    cls = json.loads(cpath.read_text(encoding="utf-8"))
    if cls.get("taxonomyVersion") != "v2":
        continue
    prim = next((c for c in cls.get("categories", []) if c.get("isPrimary")), None)
    if not prim:
        continue
    by_l1[prim.get("l1Id")] += 1
    if prim.get("l2Id") == "g25c01":
        unclassified += 1

lines.append("L1 distribution (v2):")
for k, v in by_l1.most_common():
    lines.append(f"  {k}: {v}")
lines.append(f"unclassified g25c01: {unclassified}")
OUT.write_text("\n".join(lines), encoding="utf-8")
print(OUT.read_text(encoding="utf-8"))
