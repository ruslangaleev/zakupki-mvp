import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent, KeyboardEvent } from 'react'
import './App.css'

type SearchHit = {
  regNumber: string
  title?: string | null
  objectInfo?: string | null
  customer?: string | null
  price?: string | null
  score: number
  snippet?: string | null
  noticeUrl?: string | null
  coverage: string
  matchedIn?: string | null
  matchLabel?: string | null
  categoryL1Id?: string | null
  categoryL1Name?: string | null
  categoryL2Id?: string | null
  categoryL2Name?: string | null
  hasAdvance?: boolean
  advancePercent?: number | null
  advanceLabel?: string | null
  advanceSnippet?: string | null
  applicationDeadline?: string | null
}

const EIS_TITLE = 'Единая информационная система в сфере закупок (zakupki.gov.ru)'
const EIS_LINK_LABEL = 'Открыть на zakupki.gov.ru'

type SearchResponse = {
  query: string
  items: SearchHit[]
  fuzzy?: boolean
  totalInIndex?: number
  total?: number
  from?: number
  limit?: number
  sort?: string
}

type RecentResponse = {
  items: SearchHit[]
  totalInIndex?: number
  total?: number
  from?: number
  limit?: number
  sort?: string
}

type SortMode = 'relevance' | 'deadline' | 'price' | 'published'

type SuggestItem = {
  text: string
  kind: 'query' | 'regNumber' | string
  regNumber?: string | null
}

type SuggestResponse = {
  query: string
  items: SuggestItem[]
}

type AnalyticsCountItem = {
  key: string
  count: number
}

type AnalyticsEventRow = {
  type: string
  sessionId: string
  ts: string
  q?: string | null
  total?: number | null
  fuzzy?: boolean | null
  category?: string | null
  suggestion?: string | null
  regNumber?: string | null
}

type AnalyticsSummary = {
  topQueries: AnalyticsCountItem[]
  zeroResultQueries: AnalyticsCountItem[]
  topOpenedOrders: AnalyticsCountItem[]
  recent: AnalyticsEventRow[]
  totalEvents: number
}

type AppView = 'search' | 'admin'

const PAGE_SIZE = 20
const SUGGEST_MIN_CHARS = 2
const SUGGEST_DEBOUNCE_MS = 200
const SESSION_KEY = 'zakupki.sessionId'
const PARTIAL_COVERAGE_HINT =
  'Документы ещё обрабатываются, поиск по ним может быть неполным'

function getSessionId(): string {
  try {
    const existing = window.localStorage.getItem(SESSION_KEY)
    if (existing) {
      return existing
    }
    const created = crypto.randomUUID()
    window.localStorage.setItem(SESSION_KEY, created)
    return created
  } catch {
    return 'anonymous'
  }
}

function trackAnalytics(
  events: Array<Record<string, unknown>>,
): void {
  if (events.length === 0) {
    return
  }

  const payload = {
    events: events.map((event) => ({
      sessionId: getSessionId(),
      ts: new Date().toISOString(),
      ...event,
    })),
  }

  void fetch('/api/analytics/events', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
    keepalive: true,
  }).catch(() => {
    /* analytics must not break UX */
  })
}

type CategoryL2 = {
  id: string
  name: string
}

type CategoryL1 = {
  id: string
  name: string
  children: CategoryL2[]
}

type CategoriesResponse = {
  version: string
  items: CategoryL1[]
}

type OrderField = {
  name: string
  value: string
}

type OrderDocument = {
  uid: string
  fileName?: string | null
  title?: string | null
  downloadUrl?: string | null
}

type OrderDetail = {
  regNumber: string
  title?: string | null
  objectName?: string | null
  customer?: string | null
  price?: string | null
  currency?: string | null
  stage?: string | null
  law?: string | null
  ikz?: string | null
  placedAt?: string | null
  updatedAt?: string | null
  pubDate?: string | null
  collectedAt?: string | null
  applicationDeadline?: string | null
  noticeUrl?: string | null
  documentsUrl?: string | null
  categoryL1Id?: string | null
  categoryL1Name?: string | null
  categoryL2Id?: string | null
  categoryL2Name?: string | null
  hasAdvance?: boolean
  advancePercent?: number | null
  advanceLabel?: string | null
  advanceSnippet?: string | null
  fields: OrderField[]
  documents: OrderDocument[]
}

type PriceStats = {
  min?: number | null
  max?: number | null
}

type ListMode = 'recent' | 'search' | 'search-empty' | 'search-fuzzy'

type ListFilters = {
  categoryId: string
  priceMin: string
  priceMax: string
}

function appendFilters(params: URLSearchParams, filters: ListFilters) {
  if (filters.categoryId) {
    params.set('category', filters.categoryId)
  }
  if (filters.priceMin.trim()) {
    params.set('priceMin', filters.priceMin.trim())
  }
  if (filters.priceMax.trim()) {
    params.set('priceMax', filters.priceMax.trim())
  }
}

function App() {
  const [query, setQuery] = useState(() => new URLSearchParams(window.location.search).get('q') ?? '')
  const [submittedQuery, setSubmittedQuery] = useState(
    () => new URLSearchParams(window.location.search).get('q') ?? '',
  )
  const [categoryL1Id, setCategoryL1Id] = useState(
    () => new URLSearchParams(window.location.search).get('l1') ?? '',
  )
  const [categoryL2Id, setCategoryL2Id] = useState(
    () => new URLSearchParams(window.location.search).get('l2') ?? '',
  )
  const [priceMin, setPriceMin] = useState(
    () => new URLSearchParams(window.location.search).get('priceMin') ?? '',
  )
  const [priceMax, setPriceMax] = useState(
    () => new URLSearchParams(window.location.search).get('priceMax') ?? '',
  )
  const [page, setPage] = useState(() => {
    const raw = Number(new URLSearchParams(window.location.search).get('page') ?? '1')
    return Number.isFinite(raw) && raw > 1 ? Math.floor(raw) - 1 : 0
  })
  const [sort, setSort] = useState<SortMode>(() => {
    const raw = new URLSearchParams(window.location.search).get('sort')
    if (raw === 'deadline' || raw === 'price' || raw === 'published' || raw === 'relevance') {
      return raw
    }
    return new URLSearchParams(window.location.search).get('q') ? 'relevance' : 'deadline'
  })
  const [categories, setCategories] = useState<CategoryL1[]>([])
  const [items, setItems] = useState<SearchHit[]>([])
  const [totalInIndex, setTotalInIndex] = useState(0)
  const [totalFound, setTotalFound] = useState(0)
  const [listMode, setListMode] = useState<ListMode>('recent')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [selectedRegNumber, setSelectedRegNumber] = useState<string | null>(
    () => new URLSearchParams(window.location.search).get('order'),
  )
  const [detail, setDetail] = useState<OrderDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState<string | null>(null)
  const [orderPreview, setOrderPreview] = useState<SearchHit | null>(null)
  const [titleExpanded, setTitleExpanded] = useState(false)
  const [priceStats, setPriceStats] = useState<PriceStats | null>(null)
  const [suggestions, setSuggestions] = useState<SuggestItem[]>([])
  const [suggestOpen, setSuggestOpen] = useState(false)
  const [suggestIndex, setSuggestIndex] = useState(-1)
  const [view, setView] = useState<AppView>(() =>
    new URLSearchParams(window.location.search).get('view') === 'admin' ? 'admin' : 'search',
  )
  const blurTimerRef = useRef<number | null>(null)
  const scrollBeforeOrderRef = useRef(0)
  const pendingScrollRestoreRef = useRef<number | null>(null)
  const openedOrderViaPushRef = useRef(false)

  // Prefer precise L2; otherwise whole L1 section.
  const categoryId = categoryL2Id || categoryL1Id
  const selectedL1 = categories.find((c) => c.id === categoryL1Id)
  const filters: ListFilters = { categoryId, priceMin, priceMax }

  const buildListSearchParams = useCallback(() => {
    const params = new URLSearchParams()
    if (submittedQuery.trim()) {
      params.set('q', submittedQuery.trim())
    }
    if (categoryL1Id) {
      params.set('l1', categoryL1Id)
    }
    if (categoryL2Id) {
      params.set('l2', categoryL2Id)
    }
    if (priceMin.trim()) {
      params.set('priceMin', priceMin.trim())
    }
    if (priceMax.trim()) {
      params.set('priceMax', priceMax.trim())
    }
    const defaultSort = submittedQuery.trim() ? 'relevance' : 'deadline'
    if (sort !== defaultSort) {
      params.set('sort', sort)
    }
    if (page > 0) {
      params.set('page', String(page + 1))
    }
    return params
  }, [submittedQuery, categoryL1Id, categoryL2Id, priceMin, priceMax, sort, page])
  const priceRangeInvalid =
    Boolean(priceMin.trim()) &&
    Boolean(priceMax.trim()) &&
    Number(priceMin) > Number(priceMax)
  const pageCount = Math.max(1, Math.ceil(totalFound / PAGE_SIZE) || 1)
  const canPrev = page > 0
  const canNext = (page + 1) * PAGE_SIZE < totalFound

  const loadRecent = useCallback(
    async (nextFilters: ListFilters, from: number, nextSort: SortMode, signal?: AbortSignal) => {
      const params = new URLSearchParams({
        limit: String(PAGE_SIZE),
        from: String(from),
        sort: nextSort === 'relevance' ? 'deadline' : nextSort,
      })
      appendFilters(params, nextFilters)
      const response = await fetch(`/api/orders/recent?${params}`, { signal })
      if (!response.ok) {
        throw new Error(`Не удалось загрузить последние закупки (${response.status})`)
      }
      return (await response.json()) as RecentResponse
    },
    [],
  )

  const runSearch = useCallback(
    async (
      q: string,
      nextFilters: ListFilters,
      from: number,
      nextSort: SortMode,
      signal?: AbortSignal,
    ) => {
      const params = new URLSearchParams({
        q,
        limit: String(PAGE_SIZE),
        from: String(from),
        sort: nextSort,
      })
      appendFilters(params, nextFilters)
      const response = await fetch(`/api/search?${params}`, { signal })
      if (!response.ok) {
        throw new Error(`Поиск недоступен (${response.status})`)
      }
      return (await response.json()) as SearchResponse
    },
    [],
  )

  useEffect(() => {
    fetch('/api/categories')
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(`Не удалось загрузить категории (${response.status})`)
        }
        return (await response.json()) as CategoriesResponse
      })
      .then((data) => setCategories(data.items ?? []))
      .catch(() => setCategories([]))
  }, [])

  useEffect(() => {
    fetch('/api/stats/prices')
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(`prices ${response.status}`)
        }
        return (await response.json()) as PriceStats
      })
      .then((data) => setPriceStats(data))
      .catch(() => setPriceStats(null))
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    const q = submittedQuery.trim()
    const from = page * PAGE_SIZE

    if (priceRangeInvalid) {
      setItems([])
      setTotalFound(0)
      setLoading(false)
      setError('Цена «от» не может быть больше цены «до»')
      return
    }

    // Drop stale rows/totals immediately so filters don't show the previous page.
    setLoading(true)
    setError(null)
    setItems([])
    setTotalFound(0)

    const load = async () => {
      if (q) {
        const data = await runSearch(q, filters, from, sort, controller.signal)
        if (controller.signal.aborted) {
          return
        }
        const found = data.items ?? []
        const total = data.total ?? found.length
        setTotalInIndex(data.totalInIndex ?? 0)
        setTotalFound(total)
        setItems(found)
        setListMode(found.length > 0 ? (data.fuzzy ? 'search-fuzzy' : 'search') : 'search-empty')
        if (from === 0) {
          trackAnalytics([
            {
              type: 'search',
              q,
              total,
              fuzzy: Boolean(data.fuzzy),
              category: filters.categoryId || null,
              priceMin: filters.priceMin.trim() ? Number(filters.priceMin) : null,
              priceMax: filters.priceMax.trim() ? Number(filters.priceMax) : null,
            },
          ])
        }
        return
      }

      const recent = await loadRecent(filters, from, sort, controller.signal)
      if (controller.signal.aborted) {
        return
      }
      setItems(recent.items ?? [])
      setTotalInIndex(recent.totalInIndex ?? 0)
      setTotalFound(recent.total ?? recent.items?.length ?? 0)
      setListMode('recent')
    }

    load()
      .catch((err: unknown) => {
        if (controller.signal.aborted) {
          return
        }
        setItems([])
        setTotalFound(0)
        setError(err instanceof Error ? err.message : 'Не удалось загрузить закупки')
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false)
        }
      })

    return () => controller.abort()
  }, [
    categoryL1Id,
    categoryL2Id,
    priceMin,
    priceMax,
    page,
    sort,
    submittedQuery,
    priceRangeInvalid,
    loadRecent,
    runSearch,
  ])

  useEffect(() => {
    if (!selectedRegNumber) {
      setDetail(null)
      setDetailError(null)
      setOrderPreview(null)
      setTitleExpanded(false)
      return
    }

    let cancelled = false
    setDetailLoading(true)
    setDetailError(null)
    setTitleExpanded(false)

    fetch(`/api/orders/${encodeURIComponent(selectedRegNumber)}`)
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(
            response.status === 404
              ? 'Карточка не найдена на диске'
              : `Не удалось загрузить карточку (${response.status})`,
          )
        }
        return (await response.json()) as OrderDetail
      })
      .then((data) => {
        if (!cancelled) {
          setDetail(data)
        }
      })
      .catch((err: unknown) => {
        if (!cancelled) {
          setDetail(null)
          setDetailError(err instanceof Error ? err.message : 'Ошибка загрузки карточки')
        }
      })
      .finally(() => {
        if (!cancelled) {
          setDetailLoading(false)
        }
      })

    return () => {
      cancelled = true
    }
  }, [selectedRegNumber])

  const showBrowse = () => {
    setSelectedRegNumber(null)
    setOrderPreview(null)
    setPage(0)
    setSuggestions([])
    setSuggestOpen(false)
    setSuggestIndex(-1)
    setView('search')
    setQuery('')
    setSubmittedQuery('')
    setSort((prev) => (prev === 'relevance' ? 'deadline' : prev))
  }

  const submitQuery = (raw: string, source: 'typed' | 'suggest' = 'typed') => {
    const q = raw.trim()
    if (!q) {
      showBrowse()
      return
    }

    if (source === 'suggest') {
      trackAnalytics([
        {
          type: 'suggest_select',
          q: query.trim() || null,
          suggestion: q,
        },
      ])
    }

    setSelectedRegNumber(null)
    setOrderPreview(null)
    setPage(0)
    setSuggestions([])
    setSuggestOpen(false)
    setSuggestIndex(-1)
    setView('search')
    // Search defaults to relevance when leaving the empty-query (deadline) browse mode.
    setSort((prev) => (prev === 'deadline' ? 'relevance' : prev))
    setQuery(q)
    setSubmittedQuery(q)
  }

  const openOrder = (regNumber: string, preview?: SearchHit | null) => {
    trackAnalytics([
      {
        type: 'order_open',
        regNumber,
        q: submittedQuery.trim() || null,
      },
    ])
    scrollBeforeOrderRef.current = window.scrollY
    const params = buildListSearchParams()
    params.set('order', regNumber)
    const next = params.toString()
    const url = next ? `${window.location.pathname}?${next}` : window.location.pathname
    window.history.pushState({ zakupkiOrder: true }, '', url)
    openedOrderViaPushRef.current = true
    setOrderPreview(preview && preview.regNumber === regNumber ? preview : null)
    setSelectedRegNumber(regNumber)
    window.scrollTo(0, 0)
  }

  const closeOrder = () => {
    if (openedOrderViaPushRef.current) {
      openedOrderViaPushRef.current = false
      window.history.back()
      return
    }
    setOrderPreview(null)
    setSelectedRegNumber(null)
  }

  useEffect(() => {
    const q = query.trim()
    if (q.length < SUGGEST_MIN_CHARS) {
      setSuggestions([])
      setSuggestOpen(false)
      setSuggestIndex(-1)
      return
    }

    const controller = new AbortController()
    const timer = window.setTimeout(() => {
      const params = new URLSearchParams({ q, limit: '8' })
      fetch(`/api/suggest?${params}`, { signal: controller.signal })
        .then(async (response) => {
          if (!response.ok) {
            throw new Error(`suggest ${response.status}`)
          }
          return (await response.json()) as SuggestResponse
        })
        .then((data) => {
          const items = data.items ?? []
          setSuggestions(items)
          setSuggestOpen(items.length > 0)
          setSuggestIndex(-1)
        })
        .catch((err: unknown) => {
          if (controller.signal.aborted) {
            return
          }
          setSuggestions([])
          setSuggestOpen(false)
          setSuggestIndex(-1)
          void err
        })
    }, SUGGEST_DEBOUNCE_MS)

    return () => {
      controller.abort()
      window.clearTimeout(timer)
    }
  }, [query])

  const onSubmit = (event: FormEvent) => {
    event.preventDefault()
    if (suggestOpen && suggestIndex >= 0 && suggestions[suggestIndex]) {
      submitQuery(suggestions[suggestIndex].text, 'suggest')
      return
    }
    submitQuery(query, 'typed')
  }

  const onSearchKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown' && suggestOpen && suggestions.length > 0) {
      event.preventDefault()
      setSuggestIndex((prev) => (prev + 1) % suggestions.length)
      return
    }
    if (event.key === 'ArrowUp' && suggestOpen && suggestions.length > 0) {
      event.preventDefault()
      setSuggestIndex((prev) => (prev <= 0 ? suggestions.length - 1 : prev - 1))
      return
    }
    if (event.key === 'Escape') {
      setSuggestOpen(false)
      setSuggestIndex(-1)
      return
    }
    // Disabled submit buttons block native Enter; keep search runnable while loading.
    if (event.key === 'Enter') {
      event.preventDefault()
      if (suggestOpen && suggestIndex >= 0 && suggestions[suggestIndex]) {
        submitQuery(suggestions[suggestIndex].text, 'suggest')
        return
      }
      submitQuery(event.currentTarget.value, 'typed')
    }
  }

  const resetFilters = () => {
    setCategoryL1Id('')
    setCategoryL2Id('')
    setPriceMin('')
    setPriceMax('')
    setQuery('')
    setSubmittedQuery('')
    setSort('deadline')
    setPage(0)
    setSelectedRegNumber(null)
    setSuggestions([])
    setSuggestOpen(false)
    setSuggestIndex(-1)
  }

  // List/admin URL stays shareable via replaceState. Card open uses pushState separately.
  useEffect(() => {
    if (selectedRegNumber) {
      return
    }
    const params =
      view === 'admin' ? new URLSearchParams({ view: 'admin' }) : buildListSearchParams()
    const next = params.toString()
    const url = next ? `${window.location.pathname}?${next}` : window.location.pathname
    const current = `${window.location.pathname}${window.location.search}`
    if (current !== url) {
      window.history.replaceState(window.history.state, '', url)
    }
  }, [
    view,
    selectedRegNumber,
    buildListSearchParams,
  ])

  useEffect(() => {
    const onPopState = () => {
      const params = new URLSearchParams(window.location.search)
      const order = params.get('order')
      const nextView: AppView = params.get('view') === 'admin' ? 'admin' : 'search'
      setView(nextView)
      if (order) {
        openedOrderViaPushRef.current = true
        setSelectedRegNumber(order)
        return
      }
      openedOrderViaPushRef.current = false
      if (selectedRegNumber) {
        pendingScrollRestoreRef.current = scrollBeforeOrderRef.current
      }
      setSelectedRegNumber(null)
    }
    window.addEventListener('popstate', onPopState)
    return () => window.removeEventListener('popstate', onPopState)
  }, [selectedRegNumber])

  useEffect(() => {
    if (selectedRegNumber || loading) {
      return
    }
    const y = pendingScrollRestoreRef.current
    if (y == null) {
      return
    }
    pendingScrollRestoreRef.current = null
    requestAnimationFrame(() => window.scrollTo(0, y))
  }, [selectedRegNumber, loading, items])

  if (view === 'admin') {
    return (
      <main className="app">
        <AppNav
          view={view}
          onChange={(next) => {
            if (next === 'search' && selectedRegNumber) {
              closeOrder()
            }
            setView(next)
          }}
        />
        <AdminPanel />
      </main>
    )
  }

  if (selectedRegNumber) {
    const preview = orderPreview?.regNumber === selectedRegNumber ? orderPreview : null
    const headline = resolveHeadline(
      detail?.objectName ?? preview?.objectInfo,
      detail?.title ?? preview?.title,
      selectedRegNumber,
    )
    const deadline = detail?.applicationDeadline ?? preview?.applicationDeadline
    const priceRaw = detail?.price ?? preview?.price
    const noticeUrl = detail?.noticeUrl ?? preview?.noticeUrl

    return (
      <main className="app">
        <AppNav
          view={view}
          onChange={(next) => {
            if (next === 'admin') {
              openedOrderViaPushRef.current = false
              setOrderPreview(null)
              setSelectedRegNumber(null)
            }
            setView(next)
          }}
        />
        <button className="back-button" type="button" onClick={closeOrder}>
          ← К результатам
        </button>

        {detailError && <p className="error">{detailError}</p>}

        <article className="order-card">
          <header className="order-header">
            <p className="eyebrow result-reg">№ {selectedRegNumber}</p>
            <h1 className={titleExpanded ? 'order-title is-expanded' : 'order-title'}>{headline}</h1>
            {headline.length > 90 && (
              <button
                type="button"
                className="title-expand"
                onClick={() => setTitleExpanded((prev) => !prev)}
              >
                {titleExpanded ? 'свернуть' : 'показать полностью'}
              </button>
            )}
            <DeadlineBanner deadline={deadline} />
            {(detail?.title || preview?.title) &&
              (detail?.objectName || preview?.objectInfo) &&
              (detail?.title ?? preview?.title) !== (detail?.objectName ?? preview?.objectInfo) && (
                <p className="order-subtitle">{detail?.title ?? preview?.title}</p>
              )}
            <CategoryBadges
              l1={detail?.categoryL1Name ?? preview?.categoryL1Name}
              l2={detail?.categoryL2Name ?? preview?.categoryL2Name}
            />
            <AdvanceBadge
              hasAdvance={detail?.hasAdvance ?? preview?.hasAdvance}
              label={detail?.advanceLabel ?? preview?.advanceLabel}
              percent={detail?.advancePercent ?? preview?.advancePercent}
              snippet={detail?.advanceSnippet ?? preview?.advanceSnippet}
            />
          </header>

          <dl className="facts">
            {(detail?.customer || preview?.customer) && (
              <>
                <dt>Заказчик</dt>
                <dd>{detail?.customer ?? preview?.customer}</dd>
              </>
            )}
            {priceRaw && (
              <>
                <dt>НМЦК</dt>
                <dd>{formatPriceRub(priceRaw)}</dd>
              </>
            )}
            {detail?.stage && (
              <>
                <dt>Этап</dt>
                <dd>{detail.stage}</dd>
              </>
            )}
            {detail?.law && (
              <>
                <dt>Закон</dt>
                <dd>{detail.law}</dd>
              </>
            )}
            {detail?.ikz && (
              <>
                <dt>ИКЗ</dt>
                <dd>{detail.ikz}</dd>
              </>
            )}
            {detail?.placedAt && (
              <>
                <dt>Размещено</dt>
                <dd>{detail.placedAt}</dd>
              </>
            )}
            {detail?.updatedAt && (
              <>
                <dt>Обновлено</dt>
                <dd>{detail.updatedAt}</dd>
              </>
            )}
          </dl>

          <div className="order-links">
            {noticeUrl && (
              <a href={noticeUrl} target="_blank" rel="noreferrer" title={EIS_TITLE}>
                {EIS_LINK_LABEL}
              </a>
            )}
            {detail?.documentsUrl && (
              <a href={detail.documentsUrl} target="_blank" rel="noreferrer" title={EIS_TITLE}>
                Документы на zakupki.gov.ru
              </a>
            )}
          </div>

          {detailLoading && (
            <div className="skeleton-stack" aria-hidden="true">
              <div className="skeleton-block" />
              <div className="skeleton-block" />
              <div className="skeleton-block is-short" />
            </div>
          )}

          {!detailLoading && detail && detail.fields.length > 0 && (
            <section className="order-section">
              <h2>Сведения из извещения</h2>
              <dl className="fields">
                {detail.fields.map((field) => (
                  <div key={field.name} className="field-row">
                    <dt>{field.name}</dt>
                    <dd>{field.value}</dd>
                  </div>
                ))}
              </dl>
            </section>
          )}

          {!detailLoading && detail && detail.documents.length > 0 && (
            <section className="order-section">
              <h2>Документы</h2>
              <ul className="doc-list">
                {detail.documents.map((doc) => (
                  <li key={doc.uid}>
                    {doc.downloadUrl ? (
                      <a href={doc.downloadUrl} target="_blank" rel="noreferrer">
                        {doc.title || doc.fileName || doc.uid}
                      </a>
                    ) : (
                      <span>{doc.title || doc.fileName || doc.uid}</span>
                    )}
                    {doc.fileName && doc.title && doc.fileName !== doc.title && (
                      <span className="doc-filename">{doc.fileName}</span>
                    )}
                  </li>
                ))}
              </ul>
            </section>
          )}
        </article>
      </main>
    )
  }

  const listTitle =
    listMode === 'search'
      ? null
      : listMode === 'search-fuzzy'
        ? 'Учтены возможные опечатки'
        : listMode === 'search-empty'
          ? looksLikeRegNumber(submittedQuery)
            ? 'Закупка с таким номером не найдена в базе'
            : 'Ничего не найдено по этому запросу'
          : 'Закупки в индексе'

  const hasQuery = Boolean(submittedQuery.trim())

  return (
    <main className="app">
      <AppNav view={view} onChange={setView} />
      <header className="header">
        <p className="eyebrow">Zakupki MVP</p>
        <h1>Поиск закупок</h1>
        <p className="subtitle">Опишите, что ищете — подберём закупки по смыслу карточки и документов</p>
      </header>

      <form className="search-form" onSubmit={onSubmit}>
        <div className="search-field">
          <input
            className="search-input"
            type="search"
            value={query}
            onChange={(e) => {
              setQuery(e.target.value)
              setSuggestOpen(true)
            }}
            onKeyDown={onSearchKeyDown}
            onFocus={() => {
              if (suggestions.length > 0) {
                setSuggestOpen(true)
              }
            }}
            onBlur={() => {
              if (blurTimerRef.current) {
                window.clearTimeout(blurTimerRef.current)
              }
              blurTimerRef.current = window.setTimeout(() => {
                setSuggestOpen(false)
                setSuggestIndex(-1)
              }, 120)
            }}
            placeholder="Например: реагенты для проточной цитометрии"
            autoFocus
            autoComplete="off"
            role="combobox"
            aria-autocomplete="list"
            aria-expanded={suggestOpen}
            aria-controls="search-suggest-list"
          />
          {suggestOpen && suggestions.length > 0 && (
            <ul id="search-suggest-list" className="suggest-list" role="listbox">
              {suggestions.map((item, index) => (
                <li key={`${item.kind}:${item.text}:${item.regNumber ?? index}`} role="option">
                  <button
                    type="button"
                    className={`suggest-item${index === suggestIndex ? ' is-active' : ''}`}
                    onMouseDown={(e) => e.preventDefault()}
                    onClick={() => submitQuery(item.text, 'suggest')}
                  >
                    <span className="suggest-text">{item.text}</span>
                    {item.kind === 'regNumber' && <span className="suggest-kind">номер</span>}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
        <button className="search-button" type="submit">
          {loading ? 'Ищем…' : 'Найти'}
        </button>
      </form>

      <div className="filters">
        <div className="category-filters">
          <div className="filter-field">
            <label className="filter-label" htmlFor="category-l1-filter">
              Раздел
            </label>
            <select
              id="category-l1-filter"
              className="category-filter"
              value={categoryL1Id}
              onChange={(e) => {
                setPage(0)
                setCategoryL1Id(e.target.value)
                setCategoryL2Id('')
              }}
              disabled={loading && categories.length === 0}
            >
              <option value="">Все разделы</option>
              {categories.map((l1) => (
                <option key={l1.id} value={l1.id}>
                  {l1.name}
                </option>
              ))}
            </select>
          </div>

          <div className="filter-field">
            <label className="filter-label" htmlFor="category-l2-filter">
              Категория
            </label>
            <select
              id="category-l2-filter"
              className="category-filter"
              value={categoryL2Id}
              onChange={(e) => {
                setPage(0)
                setCategoryL2Id(e.target.value)
              }}
              disabled={!categoryL1Id}
            >
              <option value="">{categoryL1Id ? 'Все категории раздела' : 'Сначала выберите раздел'}</option>
              {(selectedL1?.children ?? []).map((l2) => (
                <option key={l2.id} value={l2.id}>
                  {l2.name}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="price-filters">
          <div className="filter-field">
            <label className="filter-label" htmlFor="price-min">
              Цена от, ₽
            </label>
            <input
              id="price-min"
              className="price-input"
              type="number"
              min={0}
              step={1000}
              inputMode="numeric"
              placeholder={
                typeof priceStats?.min === 'number' ? formatPriceHint(priceStats.min) : '0'
              }
              value={priceMin}
              onChange={(e) => {
                setPage(0)
                setPriceMin(e.target.value)
              }}
            />
          </div>
          <div className="filter-field">
            <label className="filter-label" htmlFor="price-max">
              Цена до, ₽
            </label>
            <input
              id="price-max"
              className="price-input"
              type="number"
              min={0}
              step={1000}
              inputMode="numeric"
              placeholder={
                typeof priceStats?.max === 'number'
                  ? formatPriceHint(priceStats.max)
                  : 'без ограничения'
              }
              value={priceMax}
              onChange={(e) => {
                setPage(0)
                setPriceMax(e.target.value)
              }}
            />
          </div>
        </div>
        {typeof priceStats?.min === 'number' && typeof priceStats?.max === 'number' && (
          <p className="price-range-hint">
            В базе: {formatPriceRub(priceStats.min)} – {formatPriceRub(priceStats.max)}
          </p>
        )}

        <div className="sort-filters">
          <div className="filter-field">
            <label className="filter-label" htmlFor="sort-filter">
              Сортировка
            </label>
            <select
              id="sort-filter"
              className="category-filter"
              value={hasQuery ? sort : sort === 'relevance' ? 'deadline' : sort}
              onChange={(e) => {
                setPage(0)
                setSort(e.target.value as SortMode)
              }}
            >
              {hasQuery && <option value="relevance">По релевантности</option>}
              <option value="deadline">По сроку подачи</option>
              <option value="price">По цене</option>
              <option value="published">По дате публикации</option>
            </select>
          </div>
        </div>

        <button type="button" className="reset-filters" onClick={resetFilters} disabled={loading}>
          Сбросить
        </button>
      </div>

      {!loading && !error && (
        <p className="stats" aria-live="polite">
          <span>
            В базе: <strong>{formatCount(totalInIndex)}</strong>
          </span>
          <span className="stats-sep" aria-hidden="true">
            ·
          </span>
          <span>
            Найдено: <strong>{formatCount(totalFound)}</strong>
          </span>
        </p>
      )}

      {error && <p className="error">{error}</p>}

      <section className="results" aria-live="polite">
        {loading && <p className="meta">Обновляем выдачу…</p>}
        {listTitle && !loading && !error && <p className="list-heading">{listTitle}</p>}

        {!loading && !error && items.length === 0 && (
          <p className="empty">
            {listMode === 'search-empty'
              ? 'Попробуйте другой запрос или снимите фильтры.'
              : categoryId
                ? 'В этой категории пока нет проиндексированных закупок.'
                : 'Пока нет проиндексированных закупок.'}
          </p>
        )}

        <ul className="result-list">
          {!loading && items.map((item) => {
            const headline = resolveHeadline(item.objectInfo, item.title, item.regNumber)
            const showSnippet = Boolean(item.snippet && /<em>/i.test(item.snippet))

            return (
            <li key={item.regNumber} className="result-item">
              <div className="result-top">
                <h2>
                  <button
                    type="button"
                    className="result-title-button"
                    onClick={() => openOrder(item.regNumber, item)}
                  >
                    {headline}
                  </button>
                </h2>
              </div>
              <p className="result-reg">{item.regNumber}</p>
              <DeadlineBanner deadline={item.applicationDeadline} compact />
              {(item.price || item.coverage === 'partial') && (
                <p className="meta">
                  {item.price && <span>{formatPriceRub(item.price)}</span>}
                  {item.coverage === 'partial' && (
                    <span
                      className="coverage"
                      title={PARTIAL_COVERAGE_HINT}
                      aria-label={PARTIAL_COVERAGE_HINT}
                    >
                      {item.price ? ' · ' : ''}
                      документы обрабатываются
                    </span>
                  )}
                </p>
              )}
              <CategoryBadges l1={item.categoryL1Name} l2={item.categoryL2Name} />
              <AdvanceBadge
                hasAdvance={item.hasAdvance}
                label={item.advanceLabel}
                percent={item.advancePercent}
                snippet={item.advanceSnippet}
              />
              {item.customer && <p className="customer">{item.customer}</p>}
              {item.matchLabel && (
                <p className={`match-source match-${item.matchedIn ?? 'card'}`}>
                  Найдено: {item.matchLabel}
                </p>
              )}
              {showSnippet && (
                <p
                  className="snippet"
                  dangerouslySetInnerHTML={{ __html: sanitizeHighlight(item.snippet!) }}
                />
              )}
              <div className="result-actions">
                <button type="button" className="open-card" onClick={() => openOrder(item.regNumber, item)}>
                  Открыть карточку
                </button>
                {item.noticeUrl && (
                  <a href={item.noticeUrl} target="_blank" rel="noreferrer" title={EIS_TITLE}>
                    {EIS_LINK_LABEL}
                  </a>
                )}
              </div>
            </li>
            )
          })}
        </ul>

        {!loading && !error && totalFound > PAGE_SIZE && (
          <div className="pagination">
            <button type="button" disabled={!canPrev} onClick={() => setPage((p) => Math.max(0, p - 1))}>
              Назад
            </button>
            <span>
              Страница {page + 1} из {pageCount}
            </span>
            <button type="button" disabled={!canNext} onClick={() => setPage((p) => p + 1)}>
              Вперёд
            </button>
          </div>
        )}
      </section>
    </main>
  )
}

function sanitizeHighlight(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/&lt;em&gt;/g, '<em>')
    .replace(/&lt;\/em&gt;/g, '</em>')
}

function CategoryBadges({ l1, l2 }: { l1?: string | null; l2?: string | null }) {
  if (!l1 && !l2) {
    return null
  }

  return (
    <p className="category-badges">
      {l1 && <span className="category-l1">{l1}</span>}
      {l2 && <span className="category-l2">{l2}</span>}
    </p>
  )
}

function AdvanceBadge({
  hasAdvance,
  label,
  percent,
  snippet,
}: {
  hasAdvance?: boolean
  label?: string | null
  percent?: number | null
  snippet?: string | null
}) {
  if (!hasAdvance) {
    return null
  }

  const text =
    label ||
    (typeof percent === 'number' ? `Аванс ${new Intl.NumberFormat('ru-RU').format(percent)}%` : 'Аванс')

  return (
    <p className="advance-badge" title={snippet || undefined}>
      <span className="advance-pill">{text}</span>
    </p>
  )
}

function parsePriceNumber(price: string | number): number | null {
  if (typeof price === 'number') {
    return Number.isFinite(price) ? price : null
  }
  const normalized = price.replace(/\s/g, '').replace(',', '.')
  const value = Number(normalized)
  return Number.isFinite(value) ? value : null
}

function formatPriceRub(price: string | number): string {
  const value = parsePriceNumber(price)
  if (value == null) {
    return String(price)
  }
  return `${new Intl.NumberFormat('ru-RU', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value)} ₽`
}

function formatPriceHint(value: number): string {
  return new Intl.NumberFormat('ru-RU', {
    maximumFractionDigits: 0,
  }).format(value)
}

function formatDeadlineParts(deadline: string | null | undefined): { date: string; remaining: string } | null {
  if (!deadline) {
    return null
  }
  const date = new Date(deadline)
  if (Number.isNaN(date.getTime())) {
    return null
  }
  const dateLabel = date.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
  const msLeft = date.getTime() - Date.now()
  const daysLeft = Math.ceil(msLeft / (24 * 60 * 60 * 1000))
  let remaining: string
  if (daysLeft < 0) {
    remaining = 'срок истёк'
  } else if (daysLeft === 0) {
    remaining = 'осталось меньше дня'
  } else if (daysLeft === 1) {
    remaining = 'остался 1 день'
  } else if (daysLeft >= 2 && daysLeft <= 4) {
    remaining = `осталось ${daysLeft} дня`
  } else {
    remaining = `осталось ${daysLeft} дней`
  }
  return { date: dateLabel, remaining }
}

function DeadlineBanner({
  deadline,
  compact = false,
}: {
  deadline?: string | null
  compact?: boolean
}) {
  const parts = formatDeadlineParts(deadline)
  if (!parts) {
    return null
  }
  if (compact) {
    return (
      <p className="deadline-compact">
        Подача до {parts.date} · {parts.remaining}
      </p>
    )
  }
  return (
    <div className="deadline-banner">
      <p className="deadline-label">Срок подачи заявок</p>
      <p className="deadline-date">{parts.date}</p>
      <p className="deadline-remaining">{parts.remaining}</p>
    </div>
  )
}

function formatCount(value: number): string {
  return new Intl.NumberFormat('ru-RU').format(value)
}

function isGenericHeadline(text: string | null | undefined): boolean {
  if (!text || !text.trim()) {
    return true
  }

  const value = text.trim()
  return /осуществляем\w*\s+в\s+соответствии\s+с\s+частью\s+\d+\s+статьи\s+93/i.test(value)
    || /частью\s+\d+\s+статьи\s+93/i.test(value)
    || /ч\.\s*\d+\s*ст\.\s*93/i.test(value)
    || /^закупка\s+у\s+единственного\s+поставщика\b/i.test(value)
}

function stripRegNumberFromTitle(text: string, regNumber: string): string {
  const escaped = regNumber.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  return text
    .replace(new RegExp(`[№#]?\\s*${escaped}\\s*$`, 'i'), '')
    .replace(new RegExp(`[№#]\\s*${escaped}`, 'i'), '')
    .replace(/[–—-]\s*$/u, '')
    .trim()
}

function resolveHeadline(
  objectInfo: string | null | undefined,
  title: string | null | undefined,
  regNumber: string,
): string {
  const candidates = [objectInfo, title].filter((value): value is string => Boolean(value?.trim()))
  const meaningful = candidates.find((value) => !isGenericHeadline(value))
  const raw = meaningful ?? candidates[0] ?? regNumber
  const cleaned = stripRegNumberFromTitle(raw, regNumber)
  return cleaned || regNumber
}

function looksLikeRegNumber(value: string): boolean {
  const digits = value.replace(/\D/g, '')
  if (digits.length < 10) {
    return false
  }
  const significant = value.replace(/[\s№#\-–]/g, '').length
  return digits.length >= significant * 0.8
}

function AppNav({ view, onChange }: { view: AppView; onChange: (next: AppView) => void }) {
  return (
    <nav className="app-nav" aria-label="Разделы">
      <button
        type="button"
        className={view === 'search' ? 'nav-link is-active' : 'nav-link'}
        onClick={() => onChange('search')}
      >
        Поиск
      </button>
      <button
        type="button"
        className={view === 'admin' ? 'nav-link is-active' : 'nav-link'}
        onClick={() => onChange('admin')}
      >
        Админка
      </button>
    </nav>
  )
}

function formatEventTime(ts: string): string {
  const date = new Date(ts)
  if (Number.isNaN(date.getTime())) {
    return ts
  }
  return date.toLocaleString('ru-RU', {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  })
}

function describeEvent(event: AnalyticsEventRow): string {
  switch (event.type) {
    case 'search':
      return `поиск «${event.q ?? ''}» → ${event.total ?? 0}`
    case 'suggest_select':
      return `подсказка «${event.suggestion ?? ''}»`
    case 'order_open':
      return `карточка ${event.regNumber ?? ''}`
    default:
      return event.type
  }
}

function AdminPanel() {
  const [summary, setSummary] = useState<AnalyticsSummary | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    setLoading(true)
    setError(null)
    fetch('/api/admin/analytics/summary')
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(`Не удалось загрузить аналитику (${response.status})`)
        }
        return (await response.json()) as AnalyticsSummary
      })
      .then((data) => setSummary(data))
      .catch((err: unknown) => {
        setSummary(null)
        setError(err instanceof Error ? err.message : 'Ошибка загрузки аналитики')
      })
      .finally(() => setLoading(false))
  }, [])

  useEffect(() => {
    load()
  }, [load])

  return (
    <section className="admin-panel">
      <header className="header">
        <p className="eyebrow">Zakupki MVP</p>
        <h1>Админка</h1>
        <p className="subtitle">Логи поиска без авторизации (MVP)</p>
      </header>

      <div className="admin-toolbar">
        <p className="meta">Событий: {summary?.totalEvents ?? 0}</p>
        <button type="button" className="ghost-button" onClick={load} disabled={loading}>
          Обновить
        </button>
      </div>

      {loading && <p className="meta">Загрузка…</p>}
      {error && <p className="error">{error}</p>}

      {!loading && !error && summary && (
        <div className="admin-grid">
          <AdminCountBlock title="Популярные запросы" items={summary.topQueries} empty="Пока нет успешных поисков" />
          <AdminCountBlock title="Пустые выдачи" items={summary.zeroResultQueries} empty="Пустых запросов нет" />
          <AdminCountBlock title="Открытые закупки" items={summary.topOpenedOrders} empty="Пока никто не открывал карточки" />
          <section className="admin-block">
            <h2>Последние события</h2>
            {summary.recent.length === 0 ? (
              <p className="meta">Лента пуста</p>
            ) : (
              <ul className="admin-events">
                {summary.recent.map((event, index) => (
                  <li key={`${event.ts}-${event.type}-${index}`}>
                    <span className="admin-event-time">{formatEventTime(event.ts)}</span>
                    <span className="admin-event-type">{event.type}</span>
                    <span className="admin-event-body">{describeEvent(event)}</span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>
      )}
    </section>
  )
}

function AdminCountBlock({
  title,
  items,
  empty,
}: {
  title: string
  items: AnalyticsCountItem[]
  empty: string
}) {
  return (
    <section className="admin-block">
      <h2>{title}</h2>
      {items.length === 0 ? (
        <p className="meta">{empty}</p>
      ) : (
        <ol className="admin-counts">
          {items.map((item) => (
            <li key={item.key}>
              <span className="admin-count-key">{item.key}</span>
              <span className="admin-count-value">{item.count}</span>
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}

export default App
