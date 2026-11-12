# -*- coding: utf-8 -*-
from __future__ import annotations

import json
import re
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path("data/orders")
TAX = json.loads(Path("backend/Zakupki.Categorization/Taxonomy/taxonomy.v2.json").read_text(encoding="utf-8"))
OUT = Path("tools/audit_deep_report.json")

L2_KW = {}
L2_NAME = {}
L1_NAME = {}
for g in TAX["categories"]:
    L1_NAME[g["id"]] = g["name"]
    for c in g["children"]:
        L2_KW[c["id"]] = [k.lower() for k in (c.get("keywords") or [])]
        L2_NAME[c["id"]] = c["name"]


def norm(text: str) -> str:
    text = text.lower()
    text = re.sub(r"[^0-9a-zа-яё]+", " ", text, flags=re.I)
    return " ".join(text.split())


def keyword_hits(hay: str, keywords: list[str]) -> list[str]:
    hits = []
    for kw in keywords:
        parts = [p for p in norm(kw).split() if len(p) >= 3]
        if parts and all(p in hay for p in parts):
            hits.append(kw)
    return hits


def main() -> None:
    weak = []  # classified but no keyword hit in title/object
    samples_by_l2 = defaultdict(list)
    l2_counts = Counter()
    no_object_hit_but_fulltext = 0

    for order_dir in ROOT.iterdir():
        if not order_dir.is_dir() or order_dir.name.lower() == "index":
            continue
        cpath = order_dir / "classification.json"
        mpath = order_dir / "meta.json"
        if not cpath.exists() or not mpath.exists():
            continue
        cls = json.loads(cpath.read_text(encoding="utf-8"))
        if cls.get("taxonomyVersion") != "v2":
            continue
        meta = json.loads(mpath.read_text(encoding="utf-8"))
        cats = cls.get("categories") or []
        prim = next((x for x in cats if x.get("isPrimary")), cats[0] if cats else None)
        if not prim:
            continue
        l2 = prim.get("l2Id")
        title = meta.get("title") or ""
        obj = meta.get("objectName") or ""
        customer = meta.get("customer") or ""
        card = norm(f"{obj} {title} {customer}")
        kws = L2_KW.get(l2, [])
        hits = keyword_hits(card, kws)
        l2_counts[l2] += 1
        row = {
            "regNumber": order_dir.name,
            "l1Id": prim.get("l1Id"),
            "l1Name": prim.get("l1Name"),
            "l2Id": l2,
            "l2Name": prim.get("l2Name"),
            "score": prim.get("score"),
            "confidence": cls.get("confidence"),
            "objectName": obj,
            "title": title,
            "cardHits": hits,
        }
        if l2 != "g25c01" and not hits:
            weak.append(row)
        if len(samples_by_l2[l2]) < 2:
            samples_by_l2[l2].append(row)

    # Print worst L2 buckets with many weak hits
    weak_by_l2 = Counter(r["l2Id"] for r in weak)
    report = {
        "v2WeakNoCardKeywordHit": len(weak),
        "weakByL2": weak_by_l2.most_common(25),
        "weakSample": weak[:80],
        "topL2": l2_counts.most_common(25),
        "samplePerL2": {k: v for k, v in list(samples_by_l2.items())[:40]},
    }
    OUT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("weak (no keyword in title/object/customer):", len(weak))
    print("top weak L2:")
    for k, v in weak_by_l2.most_common(15):
        print(f"  {k} {L2_NAME.get(k)}: {v}")
    print("\nexamples:")
    for r in weak[:25]:
        print("---")
        print(r["regNumber"], r["l1Id"], "->", r["l2Id"], r["l2Name"], "score", r["score"])
        print("OBJ:", (r["objectName"] or "")[:160])
        print("TITLE:", (r["title"] or "")[:120])


if __name__ == "__main__":
    main()
