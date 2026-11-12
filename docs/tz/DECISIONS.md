# Архитектурные решения (ADR)

Формат: `ADR-XXX` — решение зафиксировано и обязательно, пока не отменено новым ADR.

---

## ADR-001 — Диск как первоисточник сырых данных

**Статус:** accepted  
**Дата:** 2026-11-10

Collector сохраняет закупки в `data/orders/<regNumber>/` (JSON + HTML + файлы документов).  
Поиск **не** сканирует папки на каждый запрос — для поиска используется отдельный индекс.

---

## ADR-002 — Collector независим от API

**Статус:** accepted  
**Дата:** 2026-11-10

`Zakupki.Collector` — отдельный процесс/сервис Docker. API и frontend не обязательны для сбора.

---

## ADR-003 — Поисковый движок: OpenSearch

**Статус:** accepted  
**Дата:** 2026-11-10

Для фильтров, фасетов и полнотекста используем **OpenSearch** (Apache 2.0), а не Elasticsearch.  
Причина: self-hosted без лицензионных ограничений Elastic SSPL, достаточный функционал для MVP.

Elasticsearch не запрещён навсегда, но смена движка = новый ADR.

---

## ADR-004 — География через нормализованные субъекты РФ

**Статус:** accepted  
**Дата:** 2026-11-10

Фильтр/поиск по географии опирается на:

1. справочник субъектов РФ + населённых пунктов → `subjectCode`
2. поля индекса: `deliverySubjectCodes[]`, `customerSubjectCode`, raw text
3. запрос «Башкортостан» резолвится в `subjectCode=02`, а не в substring match

Наивный full-text по слову «Башкортостан» **недостаточен**: не найдёт «Стерлитамак» без слова региона.

По умолчанию пользовательский гео-фильтр применяется к **месту поставки**, не к региону заказчика.  
Регион заказчика — отдельный фильтр.

---

## ADR-005 — MVP поиска без OCR

**Статус:** accepted  
**Дата:** 2026-11-10  
**Уточнение:** 2026-11-10

«Без OCR» **не** означает «без текста документов».

В MVP входит извлечение текста из:
- полей карточки (`meta.json`)
- HTML страниц (`pages/*.html`)
- DOCX / XLSX
- PDF **с текстовым слоем**

PDF-сканы без текстового слоя пропускаются; заказ помечается `partiallyIndexed` / `extractionCoverage=partial`.  
OCR сканов — вне MVP, отдельный ADR.

---

## ADR-006 — UX: одна строка, hybrid semantic search

**Статус:** accepted  
**Дата:** 2026-11-10  
**Уточнение:** 2026-11-10

Основной вход пользователя — **одна поисковая строка**.  
Ранжирование — **hybrid** (BM25 + vector/knn), чтобы смысловой запрос работал вместе с точными номерами/терминами.

Правила сигналов (precision-first):

1. **BM25** — приоритет `objectInfo`/`title` (в т.ч. `match_phrase` + `operator:and`); `fullText` слабее.
2. **Многословные запросы (≥2 токена)** — lexical match только через `match_phrase` (slop на карточке/fullText) или `operator:and` **без** `fullText`. Bag-of-words AND по всему `fullText` запрещён: частые токены вроде «сайт»/«услуга» из шаблонов ЕИС дают ложные попадания («разработка» + далёкий «сайт»).
3. Текстовые поля индекса — analyzer `zakupki_ru` (lowercase + russian stop + russian stemmer), чтобы «реагенты» находило «реагентов».
4. HTML `pages/*` **не** попадает в `fullText`; из document-текста вырезаются шаблонные фразы 44-ФЗ («ч. 9 ст. 37», «препараты или реагенты»). Pages остаются в knn-чанках.
5. **kNN** — только чанки `documents/*` и `pages/*` длиной ≥ `MinEmbedChunkChars` (по умолчанию 80). Короткие `meta:*` ярлыки **не** эмбеддятся. Порог чистого kNN ≥ **0.90** (слабые соседи не попадают в выдачу).
6. Если BM25 вернул хотя бы один hit — итоговый порядок = порядок BM25 (knn не переранжирует).
7. Если BM25 пуст — чистый kNN при score ≥ 0.90 (синонимы/парафразы).
8. Если после п.6–7 выдача пуста — **fuzzy BM25** (`fuzziness: AUTO`, `prefix_length: 1`) как fallback для опечаток; в API `fuzzy: true`. Fuzzy не включается, пока есть точные hits. Короткие латинские токены (напр. `RPA`) **не** fuzzy-расширяются. Для ≥2 слов fuzzy: card AND + fullText AND (без OR bag-of-words).
9. Пагинация: `from` + `limit` на search/recent.
10. Highlight в BM25 по заказам — только `objectInfo`/`title`/`customer`. Highlight `fullText` запрещён: поля >1MB дают HTTP 400 (`max_analyzed_offset`), и частые слова («ремонт») выглядят как пустая выдача. Сниппеты документов — через chunk lookup.
11. Синонимы запроса (replace): `ПО`→`программное`, `канцтовары`→`канцелярские`, `крыша`→`кровли`.

Гео-фасеты, умные чипы и парсинг сущностей из строки — следующие инкременты, не блокер среза 1.

---

## ADR-007 — Пайплайн индексации: extract → chunk → embed → OpenSearch

**Статус:** accepted  
**Дата:** 2026-11-10

1. Диск `data/orders/<regNumber>/` остаётся первоисточником сырых данных (ADR-001).
2. Отдельный worker `Zakupki.Indexer` читает диск, извлекает текст, режет на чанки, считает эмбеддинги, пишет в OpenSearch.
3. Эмбеддинги считаются **локально** (Docker-сервис), без внешнего API.
4. Индекс хранит агрегат заказа + чанки с векторами; hit по чанку поднимает родительский `regNumber`.
5. Повторная индексация идемпотентна по `regNumber`.
6. На заказ действует лимит чанков (`MaxChunksPerOrder`, по умолчанию 80): приоритет documents → pages.
7. В vector-индекс попадают только `documents/*` и `pages/*` с длиной ≥ `MinEmbedChunkChars`; meta остаётся в `fullText` для BM25 (см. ADR-006).

---

## ADR-008 — Двухуровневые категории и классификация заказов

**Статус:** accepted  
**Дата:** 2026-11-11  
**Обновлено:** 2026-11-11 — taxonomy `v2` (`categories_v2`)

### Модель

1. Таксономия **ровно 2 уровня**: L1 (раздел/`group`) → L2 (категория). L2 всегда принадлежит одному L1.
2. Каталог версионируется (`taxonomy.v2.json`), хранится в модуле `Zakupki.Categorization` (embedded JSON). Смена дерева = новая версия файла + переклассификация Indexer.
3. Идентификаторы L1/L2 могут быть без точки (напр. `g01` / `g01c01`); различение L1 vs L2 для фильтра — через `ITaxonomyCatalog`, не эвристикой по `.`.
4. У заказа есть **primary** назначение `(l1Id, l2Id)` и до 2 доп. L2 (мульти-label), если score близки.
5. Фасеты источника (`deal_type`, `customer_sector`) **не** входят в дерево категорий MVP; отдельные фильтры — later.
6. Классификатор `rules-v2`: лёгкий русский стемминг **по целым словам** (не substring) + **card-first** (object/title/customer). Документный `fullText` учитывается слабо и только как бонус/fallback — иначе boilerplate 44‑ФЗ уводит в чужие разделы. Слабый top-score → `unclassified`. Стем жёстче на коротких корнях (≤5) и при Δдлины>3: «водка»≠«воды», «кровь»≠«кровать», «смесь»≠«смеситель», «монитор»≠«мониторирование». Короткие омонимы в keywords уточняются («блоки»→«бетонные блоки», «сахар»→«сахар-песок», «сайт»→«разработка сайта»).
7. `Indexer:ClassifyOnly=true` — переклассификация без re-embed (патч полей категорий в OpenSearch); при пустом/шаблонном `objectName` резолвит объект из `common-info.html`.

### Где происходит

| Слой | Роль |
|------|------|
| `Zakupki.Categorization` | Справочник + контракт `IOrderClassifier` + реализации (сейчас `rules-v2`) |
| `Zakupki.Indexer` | После extract вызывает classifier, пишет `classification.json` на диск, кладёт поля в OpenSearch |
| `Zakupki.Api` | Отдаёт дерево `GET /api/categories`, прокидывает категории в search/detail |
| Collector | **Не** классифицирует (сырьё без семантики продукта) |
| Frontend | Бейджи L1/L2 + фильтр; API-параметр `category` → filter по `categoryL1Id` / `categoryL2Id` |

### Почему не в Collector / не online в API

- Классификация зависит от извлечённого текста (карточка + документы) → точка после extract в Indexer.
- Результат кэшируется артефактом на диске (`classification.json`) и в индексе — API не гоняет rules на каждый запрос.
- Смена алгоритма (rules → embeddings/LLM) = новая реализация `IOrderClassifier`, без ломки пайплайна.

### Поля индекса / API

- `categoryL1Id`, `categoryL1Name`, `categoryL2Id`, `categoryL2Name`
- `categoryIds[]` — L1+L2 для совместимости
- `classificationConfidence`, `classificationMethod` (напр. `rules-v2`)
- `taxonomyVersion` (напр. `v2`)
- `price` (keyword, как в карточке) + `priceValue` (double) для range-фильтра `priceMin`/`priceMax`
- Fallback без матча: L2 `g25c01` («Не классифицировано»)

---

## ADR-009 — Детекция аванса отдельной моделью

**Статус:** accepted  
**Дата:** 2026-11-11

### Решение

1. Аванс — **отдельный сигнал**, не категория таксономии: контракт `IAdvanceDetector` в `Zakupki.Categorization`.
2. Реализация MVP: `RuleBasedAdvanceDetector` (`advance-rules-v1`), precision-first.
3. Источники по приоритету: `meta.fields["Размер аванса, %"]` → страницы извещения → текст документов.
4. Indexer после extract пишет `advance.json` и поля в OpenSearch; API только читает.
5. `Indexer:AdvanceOnly=true` — пересчёт аванса без re-embed.

### Поля

- `hasAdvance` (bool)
- `advancePercent` (double?, 0–100+)
- `advanceLabel` (keyword, напр. `Аванс 30%`)
- `advanceSnippet` (text)
- `advanceSource` (`field` | `page` | `document`)
- `advanceMethod` (`advance-rules-v1`)

### Негативные кейсы

- «Аванское лесничество» и подобные топонимы
- Шаблонные фразы без конкретного размера («не менее суммы аванса», «если предусмотрена выплата аванса»)

## ADR-011 — Подсказки поиска (suggest)

**Статус:** accepted (superseded in part by ADR-012)  
**Дата:** 2026-11-11

### Решение

1. `GET /api/suggest` — autocomplete без embeddings.
2. Источник текста: **успешные пользовательские запросы** из analytics (`search` с `total > 0`), не автофразы из `objectInfo`.
3. Цифры ≥6 — prefix/wildcard по `regNumber` в OpenSearch.
4. UI: debounce ~200 ms, выпадающий список, клавиатура ↑/↓/Enter/Esc.

## ADR-012 — Аналитика поиска на диске + открытая админка

**Статус:** accepted  
**Дата:** 2026-11-11

### Решение

1. События пишутся append-only в `data/analytics/events.jsonl` (первоисточник на диске, как ADR-001).
2. Типы: `search`, `suggest_select`, `order_open` (+ `sessionId` с клиента).
3. API: `POST /api/analytics/events`, `GET /api/admin/analytics/summary`, `GET /api/admin/analytics/events`.
4. Админка MVP — открытая вкладка в UI (без auth), только для локальной работы.
5. Подсказки строятся из успешных `search` в этих логах.

## ADR-013 — Категоризация: ОКПД2/КТРУ → маппинг → rules/MNN → очередь

**Статус:** accepted  
**Дата:** 2026-11-11  
**Основание:** `docs/tz/product/TZ_kategorizaciya.docx`

### Решение

1. Коды продукции берутся из таблицы объекта/КТРУ извещения ЕИС (`meta.positions`), **не** из ИКЗ и не эвристикой по `fields[]`.
2. Классификатор `hybrid-v1`: приоритет `okpd2-map` / `ktru-map` → refine по карточке (для грубых классов 22/33/80) → `inn-dict` → traps → `rules-v2`.
3. Таблица маппинга версионируется (`okpd2-map.v1.json`, поле `mappingVersion`; текущая `v1.4`).
4. Неуверенный результат → `reviewStatus=queued` (не «молчаливое Прочее» без статуса).
5. Ручные метки (`manual` + confirmed/corrected) не затираются автопересчётом без явного флага (следующий инкремент).
6. После смены маппинга/правил — полная переклассификация индекса (`ClassifyOnly`).

### Поправка 2026-11-11 (v1.1)

Первая таблица `v1` содержала системные ошибки привязки разделов ОКПД (не «сдвиг на одну строку», а неверные L2):  
`56→PR`, `64/65→образование`, `27→станки`, `33→металлолом`, `10.3/10.5` молочка↔овощи, `61→терминалы`. Исправлено по структуре ОКПД2 + refine для неоднозначных кодов ремонта/пластмасс.

### Поправка 2026-11-11 (v1.2)

1. `84.24` (общественный порядок/охрана в ЕИС) → `g17c01`, а не «Прочее».
2. Если маппинг дал `queued`/`g25c01`, hybrid **не останавливается** — идут МНН/ловушки/rules (иначе «охрана» и «реагенты» молчали в Прочем).
3. Refine/traps для картриджей, медреагентов, дезсредств, медтехники, Kaspersky.

### Поправка 2026-11-12 (v1.3)

1. Сопоставление префиксов по **цифрам кода** (точки/дефисы игнорируются), longest-prefix.
2. Пищевые `01.11/12`→`g06c05`, `01.13/21–25`→`g06c04`, `01.47`→`g06c01`, `03.1`→`g06c02`; прочий `01.*` остаётся в `g23*`.
3. Мультиметки по весам позиций (`amount`, primary×1.5): L1-доля ≥0.7 → primary + до 2 secondary (share≥0.15); иначе `queued`.
4. В `classification.json` и detail API у assignment есть `share`; detail отдаёт полный `categories[]`.
5. Очередь ручной проверки: `GET/POST /api/review/queue` (POST защищён `X-Review-Key`); ручное решение → `method=manual`, `reviewStatus=resolved`, append в `data/manual-review.jsonl` для дообучения.
6. Порог авто-модели `Classification:AutoThreshold=0.65` зафиксирован в конфиге.
7. Обучающий набор ML.NET (`data/train/train.tsv`, вне git): СТЕ из `dantetemplar/zakupki-dataset` → метки L1/L2 через `OkpdKtruMapper`; без пересечения с `gold150`; сборка `python tools/build_train_tsv.py`.
8. ML.NET L1 (`FeaturizeText` + `SdcaMaximumEntropy`, `models/l1.zip` вне git) через `PredictionEnginePool` только для закупок **без** ОКПД2/КТРУ; ниже порога → `queued`. Нет файла модели → поведение как раньше. Обучение: `dotnet run --project backend/Zakupki.Categorization.TrainMl` (stratified cap per L1).

### Поправка 2026-11-12 (LLM-клиент, #50)

Инфраструктура для спорных закупок: `ILlmClient` / `FakeLlmClient` / `LlmOptions` (`Provider`, `Model`, `TimeoutSeconds`). Секрет только `LLM_API_KEY` из окружения. Пока нет live-провайдера — всегда фейк; `HybridOrderClassifier` LLM ещё не вызывает.

### Поправка 2026-11-12 (мультиметки пищевого ассорти, #44)

Для `TryFoodAssortment` (primary `g06c16`) вторичные метки = **все** различные L2 внутри `g06` по кодам ОКПД2/КТРУ, без `Take(2)` и без общего лимита ≤3 из весового пути. Иначе поиск по подкатегории не находит смешанную закупку.

### Поправка 2026-11-12 (v1.4, часть 1 — классы 01–33)

Перенесены префиксы прототипа `mapping_okpd2.py` для классов **01–33** (строго, без перекрывающих хвостов v1.3).  
`33.13` → `g07c09` как в прототипе; медтехника чинится `TrapRules.TryRefine` (текст «медицин*/медтех*»).

### Поправка 2026-11-12 (v1.4, часть 2 — классы 35–43)

Перенесены префиксы прототипа для классов **35–43** (энергия, вода, отходы, строительство), строго 41 правило. Версия остаётся `v1.4`.

## ADR-010a — Заголовок результата = объект закупки, не шаблон процедуры

**Статус:** accepted  
**Дата:** 2026-11-11

### Решение

1. Шаблонные title вроде «Закупка, осуществляемая в соответствии с частью 12 статьи 93… №…» не показываются как заголовок.
2. Источник заголовка: `objectName`/`objectInfo` → иначе первая позиция таблицы «Наименование товара, работы, услуги» из `common-info.html` → иначе нешаблонный title.
3. Collector и Indexer заполняют/чинят `objectName`; `Indexer:ObjectInfoOnly=true` — backfill без re-embed.
4. UI показывает `regNumber` одной мелкой строкой под заголовком; номер из title вырезается.

## ADR-010 — Сортировка выдачи

**Статус:** accepted  
**Дата:** 2026-11-11

### Решение

1. API принимает `sort=relevance|deadline|price|published` на `GET /api/search` и `GET /api/orders/recent`.
2. Дефолты: текстовый поиск — `relevance`; browse без `q` — `deadline` (ближайший срок подачи сначала).
3. `relevance` — hybrid/BM25 score (как раньше). Остальные режимы: lexical query как фильтр + field sort в OpenSearch (`applicationDeadline` asc, `priceValue` asc, `pubDate` desc); missing — в конец.
4. Indexer пишет `pubDate` / `applicationDeadline` / `priceValue`; `Indexer:DatesOnly=true` — backfill без re-embed.
5. UI: селект сортировки; `coverage=partial` — короткая пометка «документы обрабатываются» + tooltip с полной фразой.
