# -*- coding: utf-8 -*-
"""Audit order classifications against meta title/objectName."""
from __future__ import annotations

import json
import random
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path("data/orders")
OUT = Path("tools/audit_classifications_report.json")


def load_meta(order_dir: Path) -> dict:
    meta_path = order_dir / "meta.json"
    if not meta_path.exists():
        return {}
    return json.loads(meta_path.read_text(encoding="utf-8"))


def load_class(order_dir: Path) -> dict | None:
    path = order_dir / "classification.json"
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def primary(c: dict) -> dict | None:
    cats = c.get("categories") or []
    return next((x for x in cats if x.get("isPrimary")), cats[0] if cats else None)


def main() -> None:
    rows = []
    by_l1 = Counter()
    by_l2 = Counter()
    by_version = Counter()
    unclassified = []

    for order_dir in sorted(ROOT.iterdir()):
        if not order_dir.is_dir() or order_dir.name.lower() == "index":
            continue
        cls = load_class(order_dir)
        if not cls:
            continue
        meta = load_meta(order_dir)
        prim = primary(cls) or {}
        version = cls.get("taxonomyVersion") or "?"
        by_version[version] += 1
        l1 = prim.get("l1Id") or "?"
        l2 = prim.get("l2Id") or "?"
        by_l1[l1] += 1
        by_l2[l2] += 1
        row = {
            "regNumber": order_dir.name,
            "taxonomyVersion": version,
            "method": cls.get("method"),
            "confidence": cls.get("confidence"),
            "l1Id": l1,
            "l1Name": prim.get("l1Name"),
            "l2Id": l2,
            "l2Name": prim.get("l2Name"),
            "score": prim.get("score"),
            "title": meta.get("title") or meta.get("Title"),
            "objectName": meta.get("objectName") or meta.get("ObjectName"),
            "customer": meta.get("customer") or meta.get("Customer"),
            "price": meta.get("price") or meta.get("Price"),
        }
        rows.append(row)
        if l2 == "g25c01" or (prim.get("score") or 0) == 0:
            unclassified.append(row)

    # Stratified sample: up to 3 per L1 for manual review hints
    by_l1_rows: dict[str, list] = defaultdict(list)
    for r in rows:
        if r["taxonomyVersion"] == "v2":
            by_l1_rows[r["l1Id"]].append(r)

    sample = []
    rng = random.Random(42)
    for l1, items in sorted(by_l1_rows.items()):
        pick = items if len(items) <= 3 else rng.sample(items, 3)
        sample.extend(pick)

    # Heuristic suspicious: short object vs category keywords mismatch markers
    suspicious_rules = [
        ("g07", ["уборк", "снег", "ремонт", "строител", "лекар", "продукт", "питан", "асфальт", "тротуар"]),
        ("g06", ["программ", "сервер", "лиценз", "компьютер", "принтер", "сетев"]),
        ("g01", ["лекар", "медикамент", "продукт пита", "программное"]),
        ("g05", ["асфальт", "тротуар", "снег", "уборк территор"]),
        ("g22", ["лекар", "программ", "сервер"]),
        ("g15", ["капитальн ремонт", "строител", "лекарствен"]),
    ]
    suspicious = []
    for r in rows:
        if r["taxonomyVersion"] != "v2":
            continue
        text = f"{r.get('objectName') or ''} {r.get('title') or ''}".lower()
        for l1, bad_words in suspicious_rules:
            if r["l1Id"] != l1:
                continue
            hit = [w for w in bad_words if w in text]
            if hit:
                suspicious.append({**r, "flags": hit})

    report = {
        "totalWithClassification": len(rows),
        "byVersion": dict(by_version),
        "byL1": by_l1.most_common(),
        "topL2": by_l2.most_common(30),
        "unclassifiedCount": len(unclassified),
        "unclassifiedSample": unclassified[:40],
        "suspiciousCount": len(suspicious),
        "suspiciousSample": suspicious[:60],
        "stratifiedSample": sample,
    }
    OUT.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {OUT}")
    print("total", len(rows), "byVersion", dict(by_version))
    print("unclassified", len(unclassified), "suspicious", len(suspicious))
    print("top L1:")
    for k, v in by_l1.most_common(15):
        print(f"  {k}: {v}")


if __name__ == "__main__":
    main()
