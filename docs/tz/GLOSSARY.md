# Глоссарий

| Термин | Значение |
|--------|----------|
| ЕИС | Единая информационная система закупок (`zakupki.gov.ru`) |
| regNumber | Реестровый номер закупки (ключ сущности) |
| Collector | Независимый worker сбора RSS/страниц/документов |
| meta.json | Нормализованная карточка закупки на диске |
| Место поставки | Адрес/регион исполнения (куда везти/оказывать услугу) |
| Регион заказчика | Субъект РФ организации-заказчика |
| subjectCode | Код субъекта РФ (нормализованный), напр. `02` = Башкортостан |
| OpenSearch | Поисковый индекс для фильтров, полнотекста и knn |
| Indexer | Worker: extract → chunk → embed → OpenSearch |
| hybrid search | Совмещение BM25 и vector/knn при ранжировании |
| extractionCoverage | `full` \| `partial` — полнота извлечения текста заказа |
| partiallyIndexed | Заказ проиндексирован, часть файлов пропущена (нет текста / OCR вне scope) |
| applicationDeadline | Дата окончания подачи заявок (из полей карточки ЕИС), для сортировки |
| pubDate | Дата публикации извещения (RSS/карточка), для сортировки |
| AC-* | Acceptance criterion — проверяемый критерий приёмки |
| L1 / L2 | Уровни таксономии: раздел (L1) и категория (L2) |
| taxonomy.v2 | Версия справочника категорий в `Zakupki.Categorization` (из `categories_v2`) |
| IOrderClassifier | Контракт назначения категорий заказу |
| classification.json | Артефакт классификации на диске рядом с meta.json |
| classificationMethod | Идентификатор алгоритма, напр. `rules-v2` |
| IAdvanceDetector | Контракт детекции аванса по карточке/документам |
| advance.json | Артефакт детекции аванса на диске |
| hasAdvance | Признак: в закупке найден аванс с размером > 0 |
| advancePercent | Размер аванса в процентах, если извлечён |
| searchEvent | Событие аналитики поиска (`search` / `suggest_select` / `order_open`) |
| sessionId | Анонимный id сессии UI (localStorage), связывает события |
| events.jsonl | Append-only лог аналитики в `data/analytics/` |
