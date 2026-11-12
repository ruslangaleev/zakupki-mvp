# Train set + L1 model

## `data/train/train.tsv`

One command from repo root (needs free space on `HF_HOME`, default `.hf-cache/`):

```bash
python tools/build_train_tsv.py
```

Steps: download `ste.parquet` from Hugging Face `dantetemplar/zakupki-dataset` → export unique `(text, okpd2)` → label with `OkpdKtruMapper` (`dotnet` project `backend/Zakupki.Categorization.TrainPrep`) → write `data/train/train.tsv` (columns `text`, `l1`, `l2`).

## L1 model

```bash
dotnet run -c Release --project backend/Zakupki.Categorization.TrainMl -- --in data/train/train.tsv --out models/l1.zip --per-class 50000
```

Trainer applies a **per-L1 cap** (default 50 000, stratified reservoir) before `SdcaMaximumEntropy`. Escaped `\t` / `\n` in `train.tsv` are unescaped then normalized to spaces so ML.NET TSV loader does not split fields.

`data/`, `.hf-cache/`, and `models/` are gitignored. Api/Indexer use `Classification:L1ModelPath`.
