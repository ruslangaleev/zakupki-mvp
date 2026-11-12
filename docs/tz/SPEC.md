# SPEC — Zakupki MVP

Статусы AC: `planned` | `in_progress` | `done` | `blocked`.

## MVP-срез 1 — Смысловой поиск по закупке

### Цель

Пользователь вводит свободный текст («что ищу») и получает ранжированный список закупок.  
Поиск опирается на карточку и извлекаемый текст документов (без OCR).

### Поведение

1. UI показывает одну поисковую строку и список результатов.
2. `GET /api/search?q={text}&limit=20` возвращает ранжированные закупки.
3. Если точный BM25/knn пуст — повтор с fuzzy (`fuzziness: AUTO`); в ответе `fuzzy: true`.
4. Если запрос ещё не вводили — UI показывает последние проиндексированные закупки (`GET /api/orders/recent`), по умолчанию сортировка по сроку подачи заявок (ближайший сначала). Если поиск выполнен и совпадений нет — пустой список и явное «Ничего не найдено» (recent не подменяет выдачу).
5. Indexer периодически (или при старте) индексирует заказы из `data/orders`.
6. Неизвлекаемые файлы (скан PDF и т.п.) не роняют индексацию заказа; в UI `coverage=partial` показывается понятной подсказкой («Документы ещё обрабатываются…»), не сырым жаргоном.
7. UI показывает сколько закупок в индексе (`totalInIndex`) и сколько найдено по текущему запросу/фильтрам (`total`).
8. Поиск по номеру закупки (`regNumber`): полный номер или хвост/префикс из цифр (≥10), с допустимыми `№`/пробелами.
9. Если запрос выглядит как номер и закупки нет в индексе — пустой результат (не подменять семантическим/BM25 поиском по цифрам).
10. Многословный запрос (≥2 значимых слова) требует близости терминов (phrase/slop) или наличия всех слов в полях карточки; не считать совпадением далёкое со-вхождение слов в огромном `fullText` (типичный шум: «сайт», «услуги»).
11. Слабый kNN без lexical-якоря и короткие латинские токены (RPA) не должны заполнять выдачу мусором — лучше пусто.
12. Выдача поддерживает постраничный просмотр (`from` + `limit`), если `total` больше размера страницы.
13. Сортировка: `relevance` | `deadline` | `price` | `published`. Для текстового поиска по умолчанию `relevance`; без запроса — `deadline`.
14. Подсказки (`GET /api/suggest`): только то, что люди сами вводили и получали `total > 0` (из логов), плюс номера для цифр. Автофразы из `objectInfo` не используются.
15. UI пишет аналитику поведения: поиск, выбор подсказки, открытие карточки (`POST /api/analytics/events` → `data/analytics/events.jsonl`).
16. Открытая вкладка «Админка» показывает топ запросов, пустые выдачи, открытые закупки и ленту событий (`GET /api/admin/analytics/*`).

### Acceptance criteria

| ID | Критерий | Статус |
|----|----------|--------|
| `AC-SEARCH-01` | Запрос на естественном языке (`q`) возвращает JSON со списком items, у каждого есть `regNumber`, отсортированных по убыванию релевантности (`score`). | `done` |
| `AC-SEARCH-02` | Закупка находится по фрагменту текста из вложенного документа (DOCX/PDF с текстовым слоем), а не только по `title` карточки. | `done` |
| `AC-SEARCH-03` | PDF без текстового слоя не останавливает индексацию заказа; в результате/`coverage` заказ помечен как частичный (`partial`). | `done` |
| `AC-SEARCH-04` | Повторный прогон Indexer по тому же `regNumber` не создаёт дублей в выдаче (идемпотентность). | `done` |
| `AC-SEARCH-05` | UI: одна строка поиска; заголовок карточки = осмысленный объект закупки (`objectInfo` / первая позиция спецификации), не шаблон «Закупка… ст. 93…»; `regNumber` — одна мелкая строка под заголовком (без дубля в title). | `done` |
| `AC-SEARCH-06` | В результатах поиска видно, где найдено совпадение: карточка / документ / страница (`matchedIn` + `matchLabel`). | `done` |
| `AC-SEARCH-07` | Запрос с одной-двумя опечатками (например `тратуар` → `тротуар`, `ремонт крвли` → кровля) находит релевантные закупки; ответ помечается `fuzzy: true`. | `in_progress` |
| `AC-SEARCH-08` | Запрос с номером закупки (полный `regNumber` или ≥10 цифр) находит заказ через поле `regNumber`, даже если номера нет в title/objectInfo. | `done` |
| `AC-SEARCH-09` | Если запрос похож на номер закупки и точного/префиксного совпадения в индексе нет — возвращается пустая выдача (без BM25/knn «похожих» чужих заказов). | `done` |
| `AC-SEARCH-10` | Запрос из ≥2 слов (напр. «разработка сайта») не возвращает закупки, где слова встречаются далеко друг от друга только из‑за шаблонного `fullText`; нужны phrase/близость или все слова в `objectInfo`/`title`. | `done` |
| `AC-SEARCH-11` | При отсутствии lexical/phrase hits слабый kNN и короткие латинские аббревиатуры (напр. `RPA`) не подставляют нерелевантную выдачу. | `done` |
| `AC-SEARCH-12` | `GET /api/search` и `GET /api/orders/recent` принимают `from` (offset) + `limit`; UI даёт перейти на следующую/предыдущую страницу, если `total` > размера страницы. | `done` |
| `AC-CARD-01` | Из результата поиска открывается детальная карточка закупки с полями извещения и списком документов. | `done` |
| `AC-CARD-02` | `GET /api/orders/{regNumber}` читает `meta.json` с диска и возвращает 404, если заказа нет. | `done` |
| `AC-RECENT-01` | Пока запрос пуст — UI показывает закупки из индекса (по умолчанию сорт. по сроку подачи). После поиска без hits — сообщение «Ничего не найдено» и пустой список (без подмены recent). | `done` |
| `AC-SORT-01` | `GET /api/search` и `GET /api/orders/recent` принимают `sort=relevance|deadline|price|published`; UI даёт выбрать сортировку; дефолт search=`relevance`, recent=`deadline`. | `done` |
| `AC-COVER-01` | В карточке результата `coverage=partial` показывается с понятным текстом/tooltip («Документы ещё обрабатываются, поиск по ним может быть неполным»). | `done` |
| `AC-RECENT-02` | `GET /api/orders/recent?limit=20` возвращает items в том же формате, что и search hits. | `done` |
| `AC-STATS-01` | `GET /api/search` и `GET /api/orders/recent` возвращают `totalInIndex` (все заказы в индексе) и `total` (совпало с запросом и/или фильтрами, без учёта `limit`). | `done` |
| `AC-STATS-02` | UI показывает «В базе» и «Найдено» на основании `totalInIndex` / `total`. | `done` |
| `AC-UX-01` | Кнопка сброса фильтров (раздел/категория/цена) и поиск; при `priceMin > priceMax` — предупреждение. | `done` |
| `AC-UX-02` | Сырые `score` в разных шкалах не показываются пользователю в списке результатов. | `done` |
| `AC-UX-03` | Enter в строке поиска запускает поиск даже пока идёт загрузка; URL отражает `q`/фильтры/`sort`/`order`. | `done` |
| `AC-UX-04` | При смене запроса/фильтров/страницы старая выдача и «Найдено» сбрасываются сразу; `fetch` поиска отменяется через `AbortSignal`, чтобы поздний ответ не перезаписывал новый. | `done` |
| `AC-UX-05` | Открытие карточки делает `history.pushState` (не `replaceState`); «Назад» браузера и «К результатам» возвращают к той же выдаче и восстанавливают scroll. | `done` |
| `AC-UX-06` | Срок подачи заявок (`applicationDeadline`) показывается в списке и крупно в шапке карточки с отсчётом «осталось N дней» (или «срок истёк»). | `done` |
| `AC-UX-07` | Цена везде в формате `490 675,94 ₽` (ru-RU + символ рубля), без «Российский рубль». | `done` |
| `AC-UX-08` | При открытии карточки сразу видны данные из списка; детали/документы подгружаются со skeleton, без единственной надписи «Загружаем…». | `done` |
| `AC-UX-09` | Длинный заголовок карточки на узком экране ограничен 2–3 строками со ссылкой «показать полностью». | `done` |
| `AC-UX-10` | Кнопка «Найти» активна при пустом поле; пустой submit показывает выдачу по текущим фильтрам (recent). | `done` |
| `AC-UX-11` | Фильтр цены показывает подсказку диапазона из индекса (`GET /api/stats/prices`). | `done` |
| `AC-UX-12` | Ссылки на ЕИС подписаны «Открыть на zakupki.gov.ru» и имеют title с расшифровкой «Единая информационная система…». | `done` |
| `AC-SEARCH-14` | Порог отсечения по релевантности (min score / relative-to-top) для многословных запросов — убрать «мусор» в хвосте выдачи. | `planned` |
| `AC-CAT-09` | Доразметка ложных стройка/мебель: системные блоки, ИМН/лаборатория, дезсредства и сходные кейсы не должны попадать в ЖБИ/мебель/сантехнику. | `planned` |
| `AC-CAT-OKPD-01` | Collector/Indexer извлекают позиции ОКПД2/КТРУ из таблицы объекта извещения (не из ИКЗ) и сохраняют в `meta.positions`. | `done` |
| `AC-CAT-OKPD-02` | `GET /api/orders/{regNumber}` отдаёт `positions[]` с кодами/наименованиями/суммами. | `done` |
| `AC-CAT-MAP-01` | Маппинг префиксов ОКПД2/КТРУ → L2 (`okpd2-map.v1`, версия `v1.3`) имеет приоритет над rules; метод `okpd2-map` / `ktru-map`; сопоставление по цифрам кода (без точек), longest-prefix. | `done` |
| `AC-CAT-MAP-02` | Системные свалки v1 устранены: питание≠маркетинг, страхование/банк≠образование, электротехника≠станки, ремонт/ТО≠металлолом; молочка/овощи по 10.5/10.3. | `done` |
| `AC-CAT-MAP-03` | `84.24` охрана не уходит в Прочее; при `queued`/g25c01 срабатывают текстовые ловушки (охрана, сигнализация, реагенты, кислород, антивирус). | `done` |
| `AC-CAT-MAP-04` | Пищевые префиксы `01.11/12/13/21–25/47`, `03.1` → `g06*`; непищевые `01.*` остаются в `g23*`. | `done` |
| `AC-CAT-MAP-05` | Мультиметки: вес позиции = `amount` (×1.5 если primary); L1-доля ≥0.7 → primary + secondary (share≥0.15, ≤3); иначе `queued`. `GET /api/orders/{id}` отдаёт `categories[]` с `share`. | `done` |
| `AC-CAT-META-01` | Карточка и лента отдают `classificationMethod`, `classificationConfidence`, `taxonomyVersion`, `reviewStatus`. | `done` |
| `AC-CAT-META-02` | Неуверенная классификация ставит `reviewStatus=queued`, а не «молчаливое Прочее» без статуса. | `done` |
| `AC-CAT-MNN-01` | Словарь МНН (`inn-dict`) относит названия-МНН без слова «препарат» в `g05c01`. | `done` |
| `AC-CAT-TRAP-01` | Регрессия ловушек T01–T15 из product TZ зелёная (unit-тесты `Zakupki.Categorization.Tests`). | `done` |
| `AC-SEARCH-13` | Частые однословные запросы (`ремонт`, `бензин`, …) не должны обнуляться из‑за highlight `fullText` (max_analyzed_offset). | `done` |
| `AC-SUGGEST-01` | `GET /api/suggest` предлагает только успешные пользовательские запросы из логов (`search` с `total>0`) по prefix/contains; для цифр — `regNumber`. Без автофраз из индекса. | `done` |
| `AC-ANALYTICS-01` | UI шлёт `search` / `suggest_select` / `order_open` на `POST /api/analytics/events`; события пишутся в `data/analytics/events.jsonl`. | `done` |
| `AC-ANALYTICS-02` | `GET /api/admin/analytics/summary` и `/events` отдают агрегаты и ленту; UI имеет открытую вкладку «Админка». | `done` |

### API контракт

`POST /api/analytics/events`

```json
{
  "events": [
    {
      "type": "search",
      "sessionId": "…",
      "q": "уборка помещений",
      "total": 34,
      "fuzzy": false,
      "category": null,
      "priceMin": null,
      "priceMax": null,
      "ts": "2026-11-11T09:00:00Z"
    }
  ]
}
```

`type`: `search` | `suggest_select` | `order_open`.

`GET /api/admin/analytics/summary` — топ запросов, zero-result, топ `order_open`, последние события.  
`GET /api/admin/analytics/events?limit=50&type=search` — сырая лента.

`GET /api/suggest?q={text}&limit=8`

```json
{
  "query": "убор",
  "items": [
    { "text": "уборка помещений", "kind": "query" },
    { "text": "0376200013826000016", "kind": "regNumber", "regNumber": "0376200013826000016" }
  ]
}
```

`kind`: `query` | `regNumber`. Пустой/`q` короче 2 символов → `items: []`.

`GET /api/search?q={text}&limit=20&from=0&sort=relevance|deadline|price|published&category={l1OrL2Id}`

Параметр `sort` (по умолчанию `relevance` для search, `deadline` для recent): порядок выдачи. При `sort≠relevance` текстовый поиск остаётся фильтром совпадений, порядок задаёт поле OpenSearch.

```json
{
  "query": "уборка снега",
  "fuzzy": false,
  "totalInIndex": 1200,
  "total": 42,
  "from": 0,
  "limit": 20,
  "sort": "relevance",
  "items": [
    {
      "regNumber": "0373100094326001469",
      "title": "...",
      "customer": "...",
      "price": "...",
      "score": 1.23,
      "snippet": "...",
      "noticeUrl": "https://...",
      "coverage": "full",
      "matchedIn": "document",
      "matchLabel": "Документ: ТЗ.xlsx",
      "categoryL1Id": "g05",
      "categoryL1Name": "Медицина и здравоохранение",
      "categoryL2Id": "g05c02",
      "categoryL2Name": "Медицинские изделия и расходные материалы"
    }
  ]
}
```

`coverage`: `full` | `partial`.  
`matchedIn`: `card` | `document` | `page` (только для search, не для recent).  
`totalInIndex` — число документов в индексе заказов; `total` — сколько совпало с `q` и фильтрами (категория/цена), независимо от `limit`.  
Категории — primary L1/L2 после классификации Indexer (ADR-008).

`GET /api/orders/recent?limit=20&from=0&sort=deadline|price|published&category={l1OrL2Id}`

```json
{
  "totalInIndex": 1200,
  "total": 85,
  "from": 0,
  "limit": 20,
  "sort": "deadline",
  "items": [
    {
      "regNumber": "0373100094326001469",
      "title": "...",
      "customer": "...",
      "price": "...",
      "score": 0,
      "snippet": "...",
      "noticeUrl": "https://...",
      "coverage": "full"
    }
  ]
}
```

Для recent `total` — число заказов, прошедших фильтры (без текстового `q`).

`GET /api/orders/{regNumber}`

```json
{
  "regNumber": "0373100094326001469",
  "title": "...",
  "objectName": "...",
  "customer": "...",
  "price": "...",
  "currency": "...",
  "stage": "...",
  "law": "44-ФЗ",
  "ikz": "...",
  "placedAt": "...",
  "updatedAt": "...",
  "noticeUrl": "https://...",
  "documentsUrl": "https://...",
  "categoryL1Id": "g05",
  "categoryL1Name": "Медицина и здравоохранение",
  "categoryL2Id": "g05c06",
  "categoryL2Name": "Диагностика, реагенты, лабораторные исследования",
  "fields": [{ "name": "...", "value": "..." }],
  "documents": [{ "uid": "...", "fileName": "...", "title": "...", "downloadUrl": "https://..." }]
}
```

---

## MVP-срез 2 — Категории закупок (2 уровня)

### Цель

Каждая закупка получает primary-категорию L2 (и родительский L1). Пользователь видит категории в выдаче и карточке; дерево доступно через API.

### Поведение

1. Таксономия двухуровневая; классификация выполняется в Indexer после extract (`IOrderClassifier`).
2. На диск пишется `data/orders/<regNumber>/classification.json`.
3. В OpenSearch и API доступны поля категорий.
4. UI показывает L1 / L2 на результатах и в детальной карточке.
5. `GET /api/categories` возвращает дерево таксономии.
6. UI-фильтр по категории (L1 или L2); API принимает `category` на search/recent.
7. UI-фильтр по диапазону цен (`priceMin`/`priceMax`); в индексе числовое поле `priceValue`.

### Acceptance criteria

| ID | Критерий | Статус |
|----|----------|--------|
| `AC-CAT-01` | Таксономия имеет ровно 2 уровня (L1→L2); `GET /api/categories` отдаёт дерево (`taxonomy.v2`). | `done` |
| `AC-CAT-02` | Indexer классифицирует заказ и пишет `classification.json` + поля в OpenSearch. | `done` |
| `AC-CAT-03` | Search/recent/detail возвращают primary `categoryL1*` и `categoryL2*`. | `done` |
| `AC-CAT-04` | UI показывает бейджи категорий в списке и на карточке. | `done` |
| `AC-CAT-05` | Классификатор изолирован за `IOrderClassifier` (можно заменить rules→ML без смены пайплайна). | `done` |
| `AC-CAT-08` | Классификатор card-first + русский stem-match по целым словам: типовые ошибки (медицина→еда/офис/ИТ/стройка, «сок»/«соков», «водка»≠«воды», «кровь»≠«кровать», «системный блок»≠ЖБИ, «сахарный диабет»≠«сахар», «монитор»≠«мониторирование») не повторяются; слабый score → unclassified; `ClassifyOnly` обновляет категории без re-embed. | `done` |
| `AC-CAT-06` | `GET /api/search` и `GET /api/orders/recent` фильтруют по `category`: L1 → `categoryL1Id`, L2 → `categoryL2Id`. | `done` |
| `AC-CAT-07` | UI: два селекта — раздел (L1) и категория (L2); можно выбрать только L1 («весь раздел»). | `done` |
| `AC-PRICE-01` | Indexer пишет `priceValue` (double) из строки цены карточки. | `done` |
| `AC-PRICE-02` | `GET /api/search` и `GET /api/orders/recent` фильтруют по `priceMin`/`priceMax` через range на `priceValue`. | `done` |
| `AC-PRICE-03` | UI позволяет задать диапазон цен и сужает выдачу. | `done` |
| `AC-PRICE-04` | `GET /api/stats/prices` возвращает `min`/`max` по `priceValue` в индексе заказов. | `done` |

`GET /api/categories`

```json
{
  "version": "v2",
  "items": [
    {
      "id": "g05",
      "name": "Медицина и здравоохранение",
      "children": [
        { "id": "g05c02", "name": "Медицинские изделия и расходные материалы" }
      ]
    }
  ]
}
```

---

## MVP-срез 3 — Детекция аванса

### Цель

Отдельная модель (`IAdvanceDetector`) точно определяет, предусмотрен ли аванс по закупке (карточка/документы), и показывает это в выдаче.

### Поведение

1. После extract Indexer вызывает `IAdvanceDetector` (сейчас `rules-v1`).
2. На диск пишется `data/orders/<regNumber>/advance.json`.
3. В OpenSearch/API: `hasAdvance`, `advancePercent`, `advanceLabel`, `advanceSnippet`, `advanceSource`, `advanceMethod`.
4. UI в результатах поиска и на карточке показывает бейдж аванса, если `hasAdvance=true`.
5. Precision-first: игнор топонимов («Аванское…») и boilerplate («не менее суммы аванса» без размера); приоритет поля «Размер аванса, %».

### Acceptance criteria

| ID | Критерий | Статус |
|----|----------|--------|
| `AC-ADV-01` | `IAdvanceDetector` изолирован; Indexer пишет `advance.json` + поля в OpenSearch. | `done` |
| `AC-ADV-02` | При `Размер аванса, %` > 0 → `hasAdvance=true` и `advancePercent` заполнен. | `done` |
| `AC-ADV-03` | Топоним/boilerplate без размера не даёт `hasAdvance=true`. | `done` |
| `AC-ADV-04` | Search/recent/detail возвращают поля аванса; UI показывает бейдж, если аванс есть. | `done` |
