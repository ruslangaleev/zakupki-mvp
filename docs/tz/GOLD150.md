# gold150

Regression set for categorization (`data/eval/gold150.csv`).

## Provenance (important)

Most labels are **not** independent: 150 random assigned purchases (`seed=7`, morning 11.11) took L1/L2 from **rules-v2 by title**. Only ~16 rows were hand-corrected (`source=xlsx-fix`); the rest are `sample-as-is`.

So accuracy on `--strip-codes` / text-only paths is **inflated**. Treat gold150 as a **regression floor** (“not worse than …”). Independent measurement waits for gold1000 (#17).

## Eval

```bash
dotnet run -c Release --project backend/Zakupki.Categorization.Eval -- --out eval/metrics.json
```

Flags: `--strip-codes`, `--no-ml`. Console prints a leakage warning and a separate `xlsx-fix` L1/L2 slice.
