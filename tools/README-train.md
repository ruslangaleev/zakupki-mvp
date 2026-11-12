# Train set (`data/train/train.tsv`)

One command from repo root (needs free space on `HF_HOME`, default `.hf-cache/`):

```bash
python tools/build_train_tsv.py
```

Steps: download `ste.parquet` from Hugging Face `dantetemplar/zakupki-dataset` → export unique `(text, okpd2)` → label with `OkpdKtruMapper` (`dotnet` project `backend/Zakupki.Categorization.TrainPrep`) → write `data/train/train.tsv` (columns `text`, `l1`, `l2`).

`data/` and `.hf-cache/` are gitignored.
