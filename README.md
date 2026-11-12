# Zakupki MVP

**ASP.NET Core Web API** + **React (Vite)** + **Collector (RSS ЕИС)** + **Indexer (hybrid search)** + **Docker Compose**.

## ТЗ (источник правды)

- [`docs/tz/README.md`](docs/tz/README.md) — как пользоваться
- [`docs/tz/SPEC.md`](docs/tz/SPEC.md) — требования и `AC-*`
- [`docs/tz/DECISIONS.md`](docs/tz/DECISIONS.md) — ADR

## Структура

```
zakupki-mvp/
├── docs/tz/
├── backend/
│   ├── Zakupki.Api/            # API + search + карточка + категории
│   ├── Zakupki.Categorization/ # таксономия L1/L2 + IOrderClassifier
│   ├── Zakupki.Collector/      # RSS → диск
│   └── Zakupki.Indexer/        # extract → classify → embed → OpenSearch
├── services/embeddings/     # локальные эмбеддинги (fastembed)
├── frontend/
├── data/orders/
├── docker-compose.yml
└── Zakupki.slnx
```

## Быстрый старт через Docker Compose

```bash
docker compose up --build
```

После запуска:

- Frontend: http://localhost:3000
- API: http://localhost:5080/api/health
- Search: http://localhost:5080/api/search?q=реагенты
- OpenSearch: http://localhost:9200
- Embeddings: http://localhost:8090/health
- Collector → `./data/orders`
- Indexer читает `./data/orders` и пишет в OpenSearch

Первый билд embeddings скачивает модель — это может занять несколько минут.

## Локальная разработка

Нужны поднятые OpenSearch и embeddings (`docker compose up opensearch embeddings -d`).

### API

```bash
dotnet run --project backend/Zakupki.Api
```

### Collector

```bash
dotnet run --project backend/Zakupki.Collector
```

### Indexer

```bash
dotnet run --project backend/Zakupki.Indexer
```

### Eval (метрики на gold150)

```bash
dotnet run --project backend/Zakupki.Categorization.Eval -- --gold data/eval/gold150.csv --orders data/orders --out eval/metrics.json
```

Пишет `eval/metrics.json` (`l1_acc`, `l2_acc`, `other_share`, `queued_share`, `skipped`, список ошибок).

### Frontend

```bash
cd frontend
npm install
npm run dev
```

UI: http://localhost:5173

## API endpoints

| Method | Path | Описание |
|--------|------|----------|
| GET | `/api/health` | Health-check |
| GET | `/api/info` | Информация о сервисе |
| GET | `/api/search?q=&limit=20&category=&priceMin=&priceMax=` | Hybrid-поиск (+ фильтры) |
| GET | `/api/orders/recent?limit=20&category=&priceMin=&priceMax=` | Последние (+ фильтры) |
| GET | `/api/orders/{regNumber}` | Детальная карточка с диска (`meta.json`) |
| GET | `/api/categories` | Дерево категорий L1→L2 |

## Поиск: покрытие текста

Индексируются: карточка, HTML страниц, DOCX/XLSX, PDF с текстовым слоем.  
Сканы без OCR → `coverage=partial` (ADR-005).
