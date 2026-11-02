
import * as L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import './style.css'
import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr'

interface FlightObservation {
  icao24: string
  callsign: string | null
  latitude: number
  longitude: number
  positionUpdatedAtUtc: string | null
  receivedAtUtc?: string
  barometricAltitudeMeters?: number | null
  groundSpeedMetersPerSecond?: number | null
  trackDegrees?: number | null
  onGround?: boolean | null
}

interface FlightSession {
  icao24: string
  callsign: string | null
  startTimeUtc: string
  endTimeUtc: string
  observationCount: number
  observations: FlightObservation[]
}

type ViewMode = 'live' | 'archive'

type AircraftItem = {
  flight: FlightObservation
  marker: L.Marker
  stale: boolean
}

const FRESH_MS = 10 * 60 * 1000
const PRUNE_MS = 30 * 60 * 1000
const MAX_ARCHIVE_MARKERS = 100

// =====================================================
// HTML
// =====================================================

document.querySelector<HTMLDivElement>('#app')!.innerHTML = `
  <header>
    <div class="search-bar">
      <label for="flight-search">Uçak ara</label>
      <input id="flight-search" type="search"
        placeholder="Çağrı kodu veya ICAO24"
        autocomplete="off" />
      <button id="clear-search" type="button">
        Temizle
      </button>
    </div>

    <div class="app-heading">
      <h1>FlightTracker</h1>
      <p>Gerçek zamanlı uçuşlar ve kayıtlı geçmiş konumlar</p>
    </div>

    <button id="refresh" type="button">Yenile</button>
  </header>

  <nav class="view-tabs" aria-label="Harita görünümü">
    <button id="tab-live" class="active"
      type="button" aria-pressed="true">
      Canlı Harita
    </button>

    <button id="tab-archive" type="button"
      aria-pressed="false">
      Geçmiş Kayıtlar
    </button>
  </nav>

  <section id="archive-controls" hidden>
    <label>
      Başlangıç (Türkiye saati)
      <input id="archive-from" type="datetime-local" />
    </label>

    <label>
      Bitiş (Türkiye saati)
      <input id="archive-to" type="datetime-local" />
    </label>

    <button id="archive-load" type="button">
      Kayıtları getir
    </button>

    <span class="archive-help">
      En fazla 7 günlük aralık · Haritada en fazla 100 uçak
    </span>
  </section>

  <div id="status" role="status">
    Konumlar yükleniyor...
  </div>

  <div id="live-status" role="status">
    Canlı bağlantı hazırlanıyor...
  </div>

  <div class="map-area">
    <div id="map"></div>

    <aside id="flight-panel" hidden aria-label="Seçilen uçak">
      <div class="panel-header">
        <h2 id="panel-title">Uçak bilgileri</h2>
        <button id="close-panel" type="button">
          Kapat
        </button>
      </div>

      <div id="panel-content"></div>
      <button id="follow-aircraft" type="button" hidden>
  Canlı takip et
</button>
      <div id="flight-sessions" hidden></div>
    </aside>
  </div>

  <footer id="details" role="status">
    Bilgilerini ve geçmişini görmek için bir uçağa tıkla.
  </footer>
`

// =====================================================
// HTML ELEMANLARI
// =====================================================

const $ = <T extends HTMLElement>(selector: string): T => {
  const element = document.querySelector<T>(selector)

  if (!element) {
    throw new Error(`HTML elemanı bulunamadı: ${selector}`)
  }

  return element
}

const status = $<HTMLDivElement>('#status')
const liveStatus = $<HTMLDivElement>('#live-status')
const details = $<HTMLElement>('#details')
const refresh = $<HTMLButtonElement>('#refresh')

const flightSearch = $<HTMLInputElement>('#flight-search')
const clearSearch = $<HTMLButtonElement>('#clear-search')

const flightPanel = $<HTMLElement>('#flight-panel')
const panelTitle = $<HTMLElement>('#panel-title')
const panelContent = $<HTMLDivElement>('#panel-content')
const flightSessions = $<HTMLDivElement>('#flight-sessions')
const closePanel = $<HTMLButtonElement>('#close-panel')
const followAircraft =
  $<HTMLButtonElement>('#follow-aircraft')

const tabLive = $<HTMLButtonElement>('#tab-live')
const tabArchive = $<HTMLButtonElement>('#tab-archive')

const archiveControls = $<HTMLElement>('#archive-controls')
const archiveFrom = $<HTMLInputElement>('#archive-from')
const archiveTo = $<HTMLInputElement>('#archive-to')
const archiveLoad = $<HTMLButtonElement>('#archive-load')

// =====================================================
// HARİTA
// =====================================================

const map = L.map('map').setView([41.1, 29], 9)

L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
  maxZoom: 19,
  attribution: '&copy; OpenStreetMap contributors',
}).addTo(map)

const aircraftLayer = L.layerGroup().addTo(map)
const historyLayer = L.layerGroup().addTo(map)
const liveTrailLayer = L.layerGroup().addTo(map)

const aircraft = new Map<string, AircraftItem>()

// =====================================================
// UYGULAMA DURUMU
// =====================================================

let mode: ViewMode = 'live'
let selectedIcao24: string | null = null
let followingSelected = false
let selectionVersion = 0
let requestVersion = 0
let liveMessageCount = 0
let fittedLiveOnce = false
let archiveLoaded = false
let archiveTruncated = false

// Yalnızca seçili uçağın gerçek gözlem noktaları tutulur.
type TrailPoint = { time: number; lat: number; lon: number }
let activeSessions: FlightSession[] = []
let liveTrailPoints: TrailPoint[] = []
let liveTrailCallsign: string | null = null
const MAX_LIVE_TRAIL_POINTS = 100
const MAX_TRAIL_GAP_MS = 15 * 60 * 1000

// =====================================================
// TARİH VE ZAMAN
// =====================================================

function formatTime(value: string | null): string {
  return value
    ? new Date(value).toLocaleString('tr-TR')
    : 'Bilinmiyor'
}

function toLocalInput(value: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')

  return `${value.getFullYear()}-${pad(value.getMonth() + 1)}-${pad(value.getDate())}`
    + `T${pad(value.getHours())}:${pad(value.getMinutes())}`
}

const today = new Date()
today.setHours(0, 0, 0, 0)

archiveFrom.value = toLocalInput(today)

const tomorrow = new Date(today)
tomorrow.setDate(tomorrow.getDate() + 1)

archiveTo.value = toLocalInput(tomorrow)

// =====================================================
// KONUM DOĞRULAMA
// =====================================================

function positionTime(flight: FlightObservation): number {
  const time = flight.positionUpdatedAtUtc
    ? Date.parse(flight.positionUpdatedAtUtc)
    : NaN

  return Number.isFinite(time) ? time : -Infinity
}

function isFresh(flight: FlightObservation): boolean {
  const age = Date.now() - positionTime(flight)

  return Number.isFinite(age)
    && age >= -60_000
    && age <= FRESH_MS
}

function hasPosition(flight: FlightObservation): boolean {
  return Number.isFinite(flight.latitude)
    && Number.isFinite(flight.longitude)
    && Math.abs(flight.latitude) <= 90
    && Math.abs(flight.longitude) <= 180
}

// =====================================================
// UÇUŞ BİLGİLERİ
// =====================================================

function flightInfo(flight: FlightObservation): string {
  const a = flight.barometricAltitudeMeters
  const s = flight.groundSpeedMetersPerSecond
  const t = flight.trackDegrees

  const altitude =
    typeof a === 'number' && Number.isFinite(a)
      ? `${Math.round(a).toLocaleString('tr-TR')} m`
      : 'Bilinmiyor'

  const speed =
    typeof s === 'number' && Number.isFinite(s)
      ? `${Math.round(s * 3.6)} km/sa`
      : 'Bilinmiyor'

  const direction =
    typeof t === 'number' && Number.isFinite(t)
      ? `${Math.round(((t % 360) + 360) % 360)}°`
      : 'Bilinmiyor'

  const ground =
    flight.onGround === true
      ? 'Yerde'
      : flight.onGround === false
        ? 'Havada'
        : 'Bilinmiyor'

  return `Barometrik yükseklik: ${altitude} · Yer hızı: ${speed}`
    + ` · Hareket yönü: ${direction} · Durum: ${ground}`
}

// =====================================================
// UÇAK İKONLARI
// =====================================================

function createAircraftIcon(
  flight: FlightObservation,
  stale: boolean,
): L.DivIcon {
  const color = stale ? '#94a3b8' : '#2563eb'
  const heading = flight.trackDegrees

  const hasHeading =
    typeof heading === 'number'
    && Number.isFinite(heading)

  const rotation = hasHeading
    ? ((heading % 360) + 360) % 360
    : 0

  const shape = hasHeading
    ? `<path d="M12 2 L14 10 L22 15 L22 18 L14 15
        L14 20 L17 22 L17 23 L12 21 L7 23 L7 22
        L10 20 L10 15 L2 18 L2 15 L10 10 Z" />`
    : '<circle cx="12" cy="12" r="6" />'

  return L.divIcon({
    className: '',
    html: `
      <svg xmlns="http://www.w3.org/2000/svg"
        width="30" height="30" viewBox="0 0 24 24"
        aria-hidden="true"
        style="
          display:block;
          transform:rotate(${rotation}deg);
          transform-origin:center;
          fill:${color};
          stroke:white;
          stroke-width:1;
          stroke-linejoin:round;
        ">
        ${shape}
      </svg>
    `,
    iconSize: [30, 30],
    iconAnchor: [15, 15],
    tooltipAnchor: [0, -15],
  })
}

// =====================================================
// CANLI ROTA İZİ
// =====================================================

function clearLiveTrail(): void {
  liveTrailLayer.clearLayers()
  liveTrailPoints = []
  liveTrailCallsign = null
}

function renderLiveTrail(): void {
  liveTrailLayer.clearLayers()

  if (liveTrailPoints.length === 0) return

  const coordinates: L.LatLngTuple[] = liveTrailPoints.map(p => [
    p.lat,
    p.lon,
  ])

  // Tek noktayı da görünür kıl.
  L.circleMarker(coordinates[0], {
    radius: 5,
    color: '#22d3ee',
    fillColor: '#22d3ee',
    fillOpacity: 1,
  }).addTo(liveTrailLayer)

  if (coordinates.length >= 2) {
    L.polyline(coordinates, {
      color: '#22d3ee',
      weight: 4,
      opacity: 0.95,
    }).addTo(liveTrailLayer)
  }
}

function addLiveTrailPoint(flight: FlightObservation): void {
  const time = positionTime(flight)
  if (!hasPosition(flight) || !Number.isFinite(time)) return

  const callsign = flight.callsign?.trim().toUpperCase() || null
  const previous = liveTrailPoints[liveTrailPoints.length - 1]

  // ICAO24 farklı uçuşlarda da kullanılabilir; uçuş değiştiğinde izi sıfırla.
  if (
    previous
    && (
      time - previous.time > MAX_TRAIL_GAP_MS
      || (liveTrailCallsign && callsign && liveTrailCallsign !== callsign)
    )
  ) {
    clearLiveTrail()
  }

  const last = liveTrailPoints[liveTrailPoints.length - 1]

  // Aynı veya daha eski gözlemi ikinci kez çizme.
  if (last && time <= last.time) return

  // Konum değişmemişse yeni çizgi noktası üretme.
  if (last && last.lat === flight.latitude && last.lon === flight.longitude) {
    last.time = time
    return
  }

  liveTrailPoints.push({
    time,
    lat: flight.latitude,
    lon: flight.longitude,
  })

  if (liveTrailPoints.length > MAX_LIVE_TRAIL_POINTS) {
    liveTrailPoints.shift()
  }

  liveTrailCallsign = callsign || liveTrailCallsign
  renderLiveTrail()
}

function seedLiveTrail(flight: FlightObservation): void {
  clearLiveTrail()
  const selectedTime = positionTime(flight)

  // Zaten yüklenmiş oturumlardan yalnızca BU uçağın ve BU uçuşun
  // son 30 dakikadaki gerçek konumlarını başlangıç izi olarak al.
  const session = activeSessions.find(s => {
    const end = Date.parse(s.endTimeUtc)
    const callsign = s.callsign?.trim().toUpperCase() || null
    const currentCallsign = flight.callsign?.trim().toUpperCase() || null

    return Number.isFinite(end)
      && selectedTime >= end
      && selectedTime - end <= MAX_TRAIL_GAP_MS
      && (!callsign || !currentCallsign || callsign === currentCallsign)
  })

  if (session) {
    const recent = session.observations
      .filter(hasPosition)
      .filter(p => positionTime(p) >= selectedTime - 30 * 60 * 1000)
      .filter(p => positionTime(p) <= selectedTime)
      .sort((a, b) => positionTime(a) - positionTime(b))

    for (const point of recent) {
      addLiveTrailPoint(point)
    }
  }

  addLiveTrailPoint(flight)
}

// =====================================================
// SEÇİM VE BELLEK TEMİZLİĞİ
// =====================================================


function clearSelection(): void {
  selectedIcao24 = null
  followingSelected = false
  selectionVersion++

  historyLayer.clearLayers()
  clearLiveTrail()
  activeSessions = []

  flightSessions.replaceChildren()
  flightSessions.hidden = true

  followAircraft.hidden = true
  flightPanel.hidden = true

  details.textContent =
    'Bilgilerini ve geçmişini görmek için bir uçağa tıkla.'
}


function clearAircraft(): void {
  aircraftLayer.clearLayers()
  clearLiveTrail()
  activeSessions = []

  for (const item of aircraft.values()) {
    item.marker.off()
    item.marker.unbindTooltip()
  }

  aircraft.clear()
}

function pruneLiveAircraft(): void {
  if (mode !== 'live') return

  const cutoff = Date.now() - PRUNE_MS

  for (const [icao, item] of aircraft) {
    if (positionTime(item.flight) < cutoff) {
      aircraftLayer.removeLayer(item.marker)
      item.marker.off()
      item.marker.unbindTooltip()

      aircraft.delete(icao)

      if (selectedIcao24 === icao) {
        clearSelection()
      }
    }
  }
}

// =====================================================
// UÇAK ARAMA VE GÖRÜNÜRLÜK
// =====================================================

function matchesSearch(flight: FlightObservation): boolean {
  const q = flightSearch.value.trim().toLowerCase()

  return !q
    || flight.icao24.toLowerCase().includes(q)
    || (flight.callsign ?? '').toLowerCase().includes(q)
}

function refreshVisibility(): void {
  if (mode === 'live') {
    pruneLiveAircraft()
  }

  const matched = Array.from(aircraft.values())
    .filter(item =>
      matchesSearch(item.flight)
      && (mode === 'archive' || isFresh(item.flight))
    )
    .sort(
      (a, b) =>
        positionTime(b.flight) - positionTime(a.flight),
    )

  const visible = mode === 'archive'
    ? matched.slice(0, MAX_ARCHIVE_MARKERS)
    : matched

  const visibleSet = new Set(visible)

  for (const item of aircraft.values()) {
    const stale =
      mode === 'archive' || !isFresh(item.flight)

    if (item.stale !== stale) {
      item.marker.setIcon(
        createAircraftIcon(item.flight, stale),
      )

      item.stale = stale
    }

    if (visibleSet.has(item)) {
      if (!aircraftLayer.hasLayer(item.marker)) {
        aircraftLayer.addLayer(item.marker)
      }
    } else if (aircraftLayer.hasLayer(item.marker)) {
      aircraftLayer.removeLayer(item.marker)
    }
  }

  const count = visible.length

  if (mode === 'live') {
    status.textContent =
      `${count} güncel uçak gösteriliyor · Son 10 dakika`
  } else if (!archiveLoaded) {
    status.textContent =
      'Geçmiş kayıtları görmek için tarih aralığı seçip Kayıtları getir.'
  } else {
    status.textContent =
      `${count} uçak gösteriliyor · Eşleşen: ${matched.length}`
      + (
        matched.length > MAX_ARCHIVE_MARKERS
          ? ' · İlk 100 gösteriliyor, aramayla daralt.'
          : ''
      )
      + (
        archiveTruncated
          ? ' · API sonucu 1000 kayıtla sınırlı.'
          : ''
      )
  }

  if (selectedIcao24) {
    const selected = aircraft.get(selectedIcao24)

    if (
      !selected
      || !aircraftLayer.hasLayer(selected.marker)
    ) {
      clearSelection()
    } else {
      renderFlightPanel(selected.flight)
    }
  }
}

// =====================================================
// UÇAK EKLEME / GÜNCELLEME
// =====================================================

function upsertFlight(flight: FlightObservation): void {
  if (!flight.icao24 || !hasPosition(flight)) {
    return
  }

  if (mode === 'live' && !isFresh(flight)) {
    return
  }

  const previous = aircraft.get(flight.icao24)

  if (
    previous
    && positionTime(previous.flight) > positionTime(flight)
  ) {
    return
  }

  const marker = previous?.marker ?? L.marker(
    [flight.latitude, flight.longitude],
    {
      icon: createAircraftIcon(
        flight,
        mode === 'archive',
      ),
    },
  )

  if (!previous) {
    marker.on('click', () => {
      const selected = aircraft.get(flight.icao24)

      if (selected) {
        void showHistory(selected.flight)
      }
    })
  }

  marker.setLatLng([
    flight.latitude,
    flight.longitude,
  ])

  marker.setIcon(
    createAircraftIcon(flight, mode === 'archive'),
  )

  const label = document.createElement('span')
  label.style.whiteSpace = 'pre-line'

  label.textContent =
    `${flight.callsign || 'Bilinmiyor'} · ${flight.icao24}\n`
    + flightInfo(flight)
    + `\nSon gözlem: ${formatTime(flight.positionUpdatedAtUtc)}`

  marker.unbindTooltip()
  marker.bindTooltip(label)

  aircraft.set(flight.icao24, {
    flight,
    marker,
    stale: mode === 'archive',
  })

 
if (selectedIcao24 === flight.icao24) {
  renderFlightPanel(flight)

  if (mode === 'live' && followingSelected) {
    map.panTo(
      [flight.latitude, flight.longitude],
      {
        animate: true,
        duration: 0.5,
      },
    )

    addLiveTrailPoint(flight)

    details.textContent =
      `Canlı takip: ${flight.callsign || flight.icao24}`
      + ` · Son konum: ${formatTime(flight.positionUpdatedAtUtc)}`
      + ` · İz: ${liveTrailPoints.length} gerçek konum`
  }
}

  }


// =====================================================
// API
// =====================================================

async function fetchFlights(
  url: string,
): Promise<FlightObservation[]> {
  const response = await fetch(url, {
    signal: AbortSignal.timeout(15000),
  })

  if (!response.ok) {
    throw new Error(
      `API hatası: HTTP ${response.status}`,
    )
  }

  return await response.json() as FlightObservation[]
}

// =====================================================
// UÇAK DETAY PANELİ
// =====================================================

function renderFlightPanel(
  flight: FlightObservation,
): void {
  flightPanel.hidden = false
  panelTitle.textContent = flight.callsign || flight.icao24

  panelContent.replaceChildren()

  const rows: [string, string][] = [
    ['ICAO24', flight.icao24],
    ['Çağrı kodu', flight.callsign || 'Bilinmiyor'],
    ['Konum zamanı', formatTime(flight.positionUpdatedAtUtc)],
    [
      'Veri türü',
      mode === 'archive'
        ? 'Arşiv gözlemi'
        : 'Canlı takip',
    ],
    ['Enlem', flight.latitude.toFixed(5)],
    ['Boylam', flight.longitude.toFixed(5)],
  ]

  for (const [name, value] of rows) {
    const row = document.createElement('div')
    row.className = 'panel-row'

    const label = document.createElement('span')
    label.textContent = name

    const text = document.createElement('strong')
    text.textContent = value

    row.append(label, text)
    panelContent.append(row)
  }

  const metrics = document.createElement('p')
  metrics.className = 'panel-metrics'
  metrics.textContent = flightInfo(flight)

  panelContent.append(metrics)
  
followAircraft.hidden = mode !== 'live'

followAircraft.disabled =
  mode !== 'live' || !isFresh(flight)

followAircraft.textContent = followingSelected
  ? 'Takibi durdur'
  : 'Canlı takip et'

followAircraft.setAttribute(
  'aria-pressed',
  String(followingSelected),
)

}

// =====================================================
// GEÇMİŞ UÇUŞ ROTASINI ÇİZ
// =====================================================

function drawFlightSession(
  session: FlightSession,
): void {
  historyLayer.clearLayers()

  const observations = session.observations
    .filter(hasPosition)
    .slice()
    .sort(
      (a, b) =>
        positionTime(a) - positionTime(b),
    )

  if (observations.length === 0) {
    details.textContent =
      'Bu oturumda geçerli konum yok.'
    return
  }

  const points: L.LatLngTuple[] =
    observations.map(f => [
      f.latitude,
      f.longitude,
    ])

  let segment: L.LatLngTuple[] = []
  let previousTime: number | null = null
  let parts = 0

  const finish = () => {
    if (segment.length >= 2) {
      L.polyline(segment, {
        color: '#f97316',
        weight: 4,
      }).addTo(historyLayer)

      parts++
    }

    segment = []
  }

  for (const observation of observations) {
    const time = positionTime(observation)

    if (
      previousTime !== null
      && (
        !Number.isFinite(time)
        || time < previousTime
        || time - previousTime > 15 * 60 * 1000
      )
    ) {
      finish()
    }

    segment.push([
      observation.latitude,
      observation.longitude,
    ])

    previousTime = Number.isFinite(time)
      ? time
      : null
  }

  finish()

  for (const point of points) {
    L.circleMarker(point, {
      radius: 4,
      color: '#f97316',
      fillOpacity: 1,
    }).addTo(historyLayer)
  }

  if (points.length === 1) {
    map.setView(points[0], 11)
  } else {
    map.fitBounds(
      L.latLngBounds(points),
      {
        padding: [40, 40],
        maxZoom: 12,
      },
    )
  }

  details.textContent =
    `${session.callsign || session.icao24} · `
    + `${formatTime(session.startTimeUtc)} – ${formatTime(session.endTimeUtc)}`
    + ` · ${observations.length} konum`
    + ` · ${parts} rota parçası`
    + (
      points.length === 1
        ? ' · Tek konum, çizgi yok.'
        : ''
    )
}

// =====================================================
// UÇUŞ OTURUMU SEÇME MENÜSİ
// =====================================================

function renderFlightSessions(
  sessions: FlightSession[],
  version: number,
  selectedFlight: FlightObservation,
): void {
  flightSessions.replaceChildren()

  const label = document.createElement('label')
  label.htmlFor = 'session-select'
  label.textContent =
    `Geçmiş uçuş oturumu (${sessions.length})`

  const select = document.createElement('select')
  select.id = 'session-select'

  sessions.forEach((session, i) => {
    const option = document.createElement('option')
    option.value = String(i)

    option.textContent =
      `${formatTime(session.startTimeUtc)} · `
      + `${session.callsign || 'Bilinmiyor'}`
      + ` · ${session.observationCount} konum`

    select.append(option)
  })

  // Arşivden seçilen kaydın tarihine ait oturumu seç.
  const selectedTime = positionTime(selectedFlight)

  const matchingIndex = sessions.findIndex(
    session =>
      selectedTime >= Date.parse(session.startTimeUtc)
      && selectedTime <= Date.parse(session.endTimeUtc)
      && (
        !selectedFlight.callsign
        || !session.callsign
        || selectedFlight.callsign.trim().toLowerCase()
          === session.callsign.trim().toLowerCase()
      ),
  )

  const initialIndex =
    matchingIndex >= 0 ? matchingIndex : 0

  select.value = String(initialIndex)

  select.addEventListener('change', () => {
    if (version !== selectionVersion) return

    const chosen = sessions[Number(select.value)]

    if (chosen) {
      drawFlightSession(chosen)
    }
  })

  flightSessions.append(label, select)
  flightSessions.hidden = false

  drawFlightSession(sessions[initialIndex])
}

// =====================================================
// GEÇMİŞ OTURUMLARI API'DEN AL
// =====================================================

async function showHistory(
  flight: FlightObservation,
): Promise<void> {
  selectedIcao24 = flight.icao24
  followingSelected = false
  clearLiveTrail()
  activeSessions = []
  renderFlightPanel(flight)

  const version = ++selectionVersion

  historyLayer.clearLayers()
  flightSessions.replaceChildren()
  flightSessions.hidden = true

  details.textContent =
    `${flight.callsign || flight.icao24}: geçmiş oturumlar yükleniyor...`

  try {
    const response = await fetch(
      `/api/flights/${encodeURIComponent(flight.icao24)}/sessions?limit=500`,
      {
        signal: AbortSignal.timeout(15000),
      },
    )

    if (!response.ok) {
      throw new Error(
        `Oturum API hatası: HTTP ${response.status}`,
      )
    }

    const sessions =
      await response.json() as FlightSession[]

    if (version !== selectionVersion) return

    if (sessions.length === 0) {
      details.textContent =
        'Bu uçağa ait geçmiş oturum bulunamadı.'
      return
    }

    activeSessions = sessions

    renderFlightSessions(
      sessions,
      version,
      flight,
    )
  } catch (error) {
    if (version !== selectionVersion) return

    details.textContent =
      error instanceof Error
        ? error.message
        : 'Geçmiş yüklenemedi.'
  }
}

// =====================================================
// HARİTA YAKINLAŞTIRMA
// =====================================================

function fitVisibleAircraft(): void {
  const points = aircraftLayer.getLayers()
    .filter(
      (layer): layer is L.Marker =>
        layer instanceof L.Marker,
    )
    .map(marker => marker.getLatLng())

  if (points.length === 0) return

  if (points.length === 1) {
    map.setView(points[0], 10)
  } else {
    map.fitBounds(
      L.latLngBounds(points),
      {
        padding: [30, 30],
        maxZoom: 10,
      },
    )
  }
}

// =====================================================
// CANLI UÇUŞLARI YÜKLE
// =====================================================

async function loadLive(): Promise<void> {
  if (mode !== 'live') return

  const version = ++requestVersion

  refresh.disabled = true
  status.textContent = 'Güncel uçaklar yükleniyor...'

  try {
    const flights = await fetchFlights(
      '/api/flights/latest?onlyRecent=true',
    )

    if (
      version !== requestVersion
      || mode !== 'live'
    ) {
      return
    }

    for (const flight of flights) {
      upsertFlight(flight)
    }

    refreshVisibility()

    if (
      !fittedLiveOnce
      && aircraftLayer.getLayers().length > 0
    ) {
      fitVisibleAircraft()
      fittedLiveOnce = true
    }
  } catch (error) {
    if (version !== requestVersion) return

    status.textContent = error instanceof Error
      ? error.message
      : 'Güncel uçuşlar yüklenemedi.'
  } finally {
    if (version === requestVersion) {
      refresh.disabled = false
    }
  }
}

// =====================================================
// GEÇMİŞ KAYITLARI YÜKLE
// =====================================================

async function loadArchive(): Promise<void> {
  if (mode !== 'archive') return

  const from = new Date(archiveFrom.value)
  const to = new Date(archiveTo.value)

  if (
    !Number.isFinite(from.getTime())
    || !Number.isFinite(to.getTime())
    || from >= to
    || to.getTime() - from.getTime()
      > 7 * 24 * 60 * 60 * 1000
  ) {
    status.textContent =
      'Geçerli bir başlangıç/bitiş seç; aralık en fazla 7 gün olmalı.'
    return
  }

  const version = ++requestVersion

  archiveLoad.disabled = true
  status.textContent = 'Geçmiş kayıtlar yükleniyor...'

  const params = new URLSearchParams({
    fromUtc: from.toISOString(),
    toUtc: to.toISOString(),
    limit: '1000',
  })

  try {
    const flights = await fetchFlights(
      `/api/flights/archive?${params}`,
    )

    if (
      version !== requestVersion
      || mode !== 'archive'
    ) {
      return
    }

    clearSelection()
    clearAircraft()

    for (const flight of flights) {
      upsertFlight(flight)
    }

    archiveLoaded = true
    archiveTruncated = flights.length >= 1000

    refreshVisibility()
    fitVisibleAircraft()

    if (flights.length === 0) {
      details.textContent =
        'Seçilen zaman aralığında kayıtlı uçak bulunamadı.'
    }
  } catch (error) {
    if (version !== requestVersion) return

    status.textContent = error instanceof Error
      ? error.message
      : 'Arşiv yüklenemedi.'
  } finally {
    if (version === requestVersion) {
      archiveLoad.disabled = false
    }
  }
}

// =====================================================
// CANLI / ARŞİV GÖRÜNÜMÜ DEĞİŞTİR
// =====================================================

function changeView(next: ViewMode): void {
  if (mode === next) return

  requestVersion++

  refresh.disabled = false
  archiveLoad.disabled = false

  clearSelection()
  clearAircraft()

  mode = next

  tabLive.classList.toggle(
    'active',
    next === 'live',
  )

  tabArchive.classList.toggle(
    'active',
    next === 'archive',
  )

  tabLive.setAttribute(
    'aria-pressed',
    String(next === 'live'),
  )

  tabArchive.setAttribute(
    'aria-pressed',
    String(next === 'archive'),
  )

  archiveControls.hidden =
    next !== 'archive'

  archiveLoaded = false
  archiveTruncated = false

  flightSearch.value = ''

  if (next === 'live') {
    fittedLiveOnce = false
    void loadLive()
  } else {
    status.textContent =
      'Tarih ve saat seç, ardından Kayıtları getir.'

    details.textContent =
      'Arşiv gözlemleri canlı konum değildir.'
  }

  map.invalidateSize()
}

// =====================================================
// SIGNALR
// =====================================================

const connection = new HubConnectionBuilder()
  .withUrl('/hubs/flights')
  .withAutomaticReconnect()
  .configureLogging(LogLevel.Warning)
  .build()

connection.on(
  'FlightUpdated',
  (flight: FlightObservation) => {
    liveMessageCount++

    liveStatus.textContent =
      `Canlı bağlantı açık · ${liveMessageCount} mesaj alındı`
      + ` · Son: ${new Date().toLocaleTimeString('tr-TR')}`

    // Arşiv görünümündeyken yeni uçakları ekleme.
    if (mode !== 'live') return

    upsertFlight(flight)
    refreshVisibility()
  },
)

connection.onreconnecting(() => {
  liveStatus.textContent =
    'Canlı bağlantı kesildi; yeniden bağlanılıyor...'
})

connection.onreconnected(() => {
  liveStatus.textContent =
    'Canlı bağlantı yeniden kuruldu.'

  if (mode === 'live') {
    void loadLive()
  }
})

connection.onclose(() => {
  liveStatus.textContent =
    'Canlı bağlantı kapandı; yeniden denenecek.'

  window.setTimeout(
    () => void startLive(),
    5000,
  )
})

// =====================================================
// CANLI BAĞLANTIYI BAŞLAT
// =====================================================

async function startLive(): Promise<void> {
  if (
    connection.state
    !== HubConnectionState.Disconnected
  ) {
    return
  }

  try {
    await connection.start()

    liveStatus.textContent =
      'Canlı bağlantı açık; yeni Kafka mesajı bekleniyor.'
  } catch (error) {
    console.error(
      'SignalR bağlantısı kurulamadı:',
      error,
    )

    liveStatus.textContent =
      'Canlı bağlantı kurulamadı; yeniden denenecek.'

    window.setTimeout(
      () => void startLive(),
      5000,
    )
  }
}

// =====================================================
// BUTONLAR VE ETKİLEŞİMLER
// =====================================================

tabLive.addEventListener('click', () => {
  changeView('live')
})

tabArchive.addEventListener('click', () => {
  changeView('archive')
})

archiveLoad.addEventListener('click', () => {
  void loadArchive()
})

refresh.addEventListener('click', () => {
  if (mode === 'live') {
    void loadLive()
  } else {
    void loadArchive()
  }
})

flightSearch.addEventListener('input', () => {
  clearSelection()
  refreshVisibility()
})

clearSearch.addEventListener('click', () => {
  flightSearch.value = ''

  clearSelection()
  refreshVisibility()

  flightSearch.focus()
})

followAircraft.addEventListener('click', () => {
  if (mode !== 'live' || !selectedIcao24) {
    return
  }

  const selected = aircraft.get(selectedIcao24)

  if (!selected || !isFresh(selected.flight)) {
    return
  }

  if (followingSelected) {
    followingSelected = false

    // Normal uçuş geçmişi ve oturum seçicisine dön.
    void showHistory(selected.flight)
    return
  }

  followingSelected = true

  // Bekleyen geçmiş isteklerini geçersiz kıl.
  selectionVersion++

  // Canlı takibe geçerken geçmiş rota çizimini gizle.
  historyLayer.clearLayers()
  seedLiveTrail(selected.flight)

  flightSessions.replaceChildren()
  flightSessions.hidden = true

  renderFlightPanel(selected.flight)

  map.panTo(
    [
      selected.flight.latitude,
      selected.flight.longitude,
    ],
    {
      animate: true,
      duration: 0.5,
    },
  )

  details.textContent =
    `Canlı takip açık: `
    + `${selected.flight.callsign || selected.flight.icao24}`
    + ` · İz: ${liveTrailPoints.length} gerçek konum. Yeni veriler bekleniyor.`
})

closePanel.addEventListener('click', clearSelection)

// Her 30 saniyede görünür uçakları yenile.
// Bellekteki 30 dakikadan eski canlı kayıtları temizle.
window.setInterval(() => {
  if (mode === 'live') {
    refreshVisibility()
  }
}, 30000)

// =====================================================
// UYGULAMAYI BAŞLAT
// =====================================================

void loadLive()
void startLive()
