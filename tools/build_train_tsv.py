#!/usr/bin/env python3
"""Build data/train/train.tsv from dantetemplar/zakupki-dataset (STE) via OkpdKtruMapper.

One-liner from repo root (HF cache on D: by default):

  python tools/build_train_tsv.py

Requires: pyarrow, huggingface_hub, .NET SDK, project Zakupki.Categorization.TrainPrep.
"""
from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path


REPO = Path(__file__).resolve().parents[1]


def ensure_ste(hf_home: Path) -> Path:
    os.environ["HF_HOME"] = str(hf_home)
    from huggingface_hub import hf_hub_download

    path = hf_hub_download(
        "dantetemplar/zakupki-dataset",
        "data/ste.parquet",
        repo_type="dataset",
    )
    return Path(path)


def escape_field(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace("\t", "\\t")
        .replace("\n", "\\n")
        .replace("\r", "\\r")
    )


def export_candidates(ste_path: Path, out_path: Path, desc_limit: int, max_rows: int | None) -> int:
    import pyarrow.parquet as pq

    out_path.parent.mkdir(parents=True, exist_ok=True)
    pf = pq.ParquetFile(ste_path)
    cols = ["name", "description", "okpd2_code"]
    written = 0
    seen = set()

    with out_path.open("w", encoding="utf-8", newline="\n") as f:
        f.write("text\tokpd2\tktru\n")
        for rg in range(pf.num_row_groups):
            table = pf.read_row_group(rg, columns=cols)
            names = table.column("name").to_pylist()
            descs = table.column("description").to_pylist()
            okpds = table.column("okpd2_code").to_pylist()
            for name, desc, okpd in zip(names, descs, okpds):
                if not okpd:
                    continue
                okpd_s = str(okpd).strip()
                if not okpd_s:
                    continue
                name_s = (name or "").strip()
                if not name_s:
                    continue
                desc_s = (desc or "").strip()
                if desc_limit > 0 and len(desc_s) > desc_limit:
                    desc_s = desc_s[:desc_limit]
                text = name_s if not desc_s else f"{name_s} {desc_s}"
                key = " ".join(text.lower().split())
                if key in seen:
                    continue
                seen.add(key)
                f.write(f"{escape_field(text)}\t{escape_field(okpd_s)}\t\n")
                written += 1
                if max_rows is not None and written >= max_rows:
                    return written
            print(f"  row_group {rg + 1}/{pf.num_row_groups}: candidates so far {written}", flush=True)
    return written


def run_labeler(candidates: Path, gold: Path, out: Path) -> int:
    cmd = [
        "dotnet",
        "run",
        "--project",
        str(REPO / "backend" / "Zakupki.Categorization.TrainPrep"),
        "-c",
        "Release",
        "--",
        "--in",
        str(candidates),
        "--gold",
        str(gold),
        "--out",
        str(out),
    ]
    print(">", " ".join(cmd), flush=True)
    return subprocess.call(cmd, cwd=str(REPO))


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--hf-home", default=str(REPO / ".hf-cache"))
    p.add_argument("--ste", default="", help="Path to ste.parquet (skip download if set)")
    p.add_argument("--candidates", default=str(REPO / "data" / "train" / "_candidates.tsv"))
    p.add_argument("--gold", default=str(REPO / "data" / "eval" / "gold150.csv"))
    p.add_argument("--out", default=str(REPO / "data" / "train" / "train.tsv"))
    p.add_argument("--desc-limit", type=int, default=400)
    p.add_argument("--max-rows", type=int, default=0, help="Optional cap on candidate rows (0=all)")
    p.add_argument("--skip-export", action="store_true")
    args = p.parse_args()

    candidates = Path(args.candidates)
    out = Path(args.out)
    gold = Path(args.gold)

    if not args.skip_export:
        ste = Path(args.ste) if args.ste else ensure_ste(Path(args.hf_home))
        print(f"STE parquet: {ste}", flush=True)
        n = export_candidates(
            ste,
            candidates,
            desc_limit=args.desc_limit,
            max_rows=(args.max_rows or None),
        )
        print(f"Exported {n} unique candidates → {candidates}", flush=True)
    elif not candidates.exists():
        print(f"Candidates missing: {candidates}", file=sys.stderr)
        return 1

    return run_labeler(candidates, gold, out)


if __name__ == "__main__":
    raise SystemExit(main())
