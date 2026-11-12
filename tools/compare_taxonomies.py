# -*- coding: utf-8 -*-
import json
from pathlib import Path

src = json.loads(Path(r"c:\Users\Ruslan\Downloads\categories_v2.json").read_text(encoding="utf-8"))
dst = json.loads(Path("backend/Zakupki.Categorization/Taxonomy/taxonomy.v2.json").read_text(encoding="utf-8"))

src_groups = {g["id"]: g for g in src["groups"]}
dst_l1 = {c["id"]: c for c in dst["categories"]}

print("SOURCE keys:", list(src.keys()))
print("SOURCE groups:", len(src["groups"]), "categories:", sum(len(g["categories"]) for g in src["groups"]))
print("EMBEDDED version:", dst.get("version"), "L1:", len(dst["categories"]), "L2:", sum(len(c["children"]) for c in dst["categories"]))
print("facets in source:", list((src.get("facets") or {}).keys()))
print("facets in embedded: NONE (not modeled)")

only_src = sorted(set(src_groups) - set(dst_l1))
only_dst = sorted(set(dst_l1) - set(src_groups))
print("L1 only in source:", only_src)
print("L1 only in embedded:", only_dst)

name_diff = []
for gid, g in src_groups.items():
    if gid not in dst_l1:
        continue
    if g["name"] != dst_l1[gid]["name"]:
        name_diff.append((gid, g["name"], dst_l1[gid]["name"]))
print("L1 name diffs:", len(name_diff))
for row in name_diff[:10]:
    print(" ", row)

# L2 compare
src_l2 = {}
for g in src["groups"]:
    for c in g["categories"]:
        src_l2[c["id"]] = (g["id"], c)
dst_l2 = {}
for g in dst["categories"]:
    for c in g["children"]:
        dst_l2[c["id"]] = (g["id"], c)

print("L2 only source:", sorted(set(src_l2) - set(dst_l2))[:20], "count", len(set(src_l2)-set(dst_l2)))
print("L2 only embedded:", sorted(set(dst_l2) - set(src_l2))[:20], "count", len(set(dst_l2)-set(src_l2)))

kw_changed = 0
examples = []
for cid, (_, sc) in src_l2.items():
    if cid not in dst_l2:
        continue
    sk = sc.get("keywords") or []
    dk = dst_l2[cid][1].get("keywords") or []
    if sk != dk:
        kw_changed += 1
        if len(examples) < 8:
            examples.append((cid, sc.get("name"), sk, dk))
print("L2 with changed keywords:", kw_changed)
for cid, name, sk, dk in examples:
    print(f"  {cid} {name}")
    print("    src:", sk)
    print("    emb:", dk)

out = Path("tools/taxonomy_compare.txt")
lines = [
    f"source groups={len(src['groups'])} cats={sum(len(g['categories']) for g in src['groups'])}",
    f"embedded L1={len(dst['categories'])} L2={sum(len(c['children']) for c in dst['categories'])}",
    f"facets source={list((src.get('facets') or {}).keys())}",
    f"L2 keyword-changed={kw_changed}",
]
for cid, name, sk, dk in examples:
    lines.append(f"{cid} | {name}")
    lines.append(f"  SRC {sk}")
    lines.append(f"  EMB {dk}")
out.write_text("\n".join(lines), encoding="utf-8")
print("wrote", out)
