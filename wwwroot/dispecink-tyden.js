// Týdenní vytíženost vozidel po hodinách (jako v Outlooku). Běží ve vlastním okně
// na adrese dispečinku s #tyden. Zakázky a nové zakázky se otevírají v hlavním okně.
(() => {
  const weekMode = location.hash.startsWith("#tyden");
  const mainWindow = "phm-dispecink";
  const weekWindow = "phm-tyden";

  // Tlačítko v seznamu zakázek i ve formuláři zakázky (tam otevře týden data nakládky).
  document.addEventListener("click", (event) => {
    const button = event.target.closest("#open-week, [data-open-week]");
    if (!button) return;
    const date = button.dataset.openWeek
      ? document.querySelector(`#${button.dataset.openWeek}-loading-date`)?.value || ""
      : "";
    const hash = /^\d{4}-\d{2}-\d{2}$/.test(date) ? `#tyden=${date}` : "#tyden";
    const target = window.open(`${location.pathname}${location.search}${hash}`, weekWindow);
    target?.focus();
  });

  if (!weekMode) {
    window.name = mainWindow;
    return;
  }

  const HOUR = 44;
  const palette = ["#2f6fdf", "#d9822b", "#2e9e5b", "#b0479a", "#c43c3c", "#1a9aa8", "#7a5cd6", "#8c6d1f", "#d1497b", "#4d7c0f"];
  const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  }[char]));
  const dayHead = new Intl.DateTimeFormat("cs-CZ", { weekday: "short", day: "numeric", month: "numeric", timeZone: "UTC" });
  const longDay = new Intl.DateTimeFormat("cs-CZ", { day: "numeric", month: "numeric", year: "numeric", timeZone: "UTC" });
  const liters = new Intl.NumberFormat("cs-CZ", { maximumFractionDigits: 0 });
  const storageKey = "phm-tyden-vozidla-skryta";
  const grid = document.querySelector("#week-grid");
  const message = document.querySelector("#week-message");
  const asked = location.hash.match(/^#tyden=(\d{4}-\d{2}-\d{2})/)?.[1];
  let weekStart = mondayOf(asked || todayIso());
  let data = null;
  let hidden = readHidden();
  let loading = 0;
  let scrolled = false;

  document.body.classList.add("week-mode");
  document.title = "Vytíženost vozidel – PHM Trans";
  document.querySelector("#week-view").hidden = false;

  function todayIso() {
    const now = new Date();
    const pad = (value) => String(value).padStart(2, "0");
    return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
  }

  function shiftIso(iso, days) {
    const date = new Date(`${iso}T00:00:00Z`);
    date.setUTCDate(date.getUTCDate() + days);
    return date.toISOString().slice(0, 10);
  }

  function mondayOf(iso) {
    const day = new Date(`${iso}T00:00:00Z`).getUTCDay();
    return shiftIso(iso, -((day + 6) % 7));
  }

  // "2026-11-06T06:00:00" -> minuty od začátku dne iso.
  function minutesFrom(iso, value) {
    const [date, time = "00:00"] = String(value).split("T");
    const [hours, minutes] = time.split(":").map(Number);
    const days = Math.round((Date.parse(`${date}T00:00:00Z`) - Date.parse(`${iso}T00:00:00Z`)) / 86400000);
    return days * 1440 + hours * 60 + minutes;
  }

  function clock(minutes) {
    const value = ((minutes % 1440) + 1440) % 1440;
    return `${Math.floor(value / 60)}:${String(value % 60).padStart(2, "0")}`;
  }

  function readHidden() {
    try {
      return new Set(JSON.parse(localStorage.getItem(storageKey) || "[]"));
    } catch {
      return new Set();
    }
  }

  function saveHidden() {
    try {
      localStorage.setItem(storageKey, JSON.stringify([...hidden]));
    } catch {
      // Bez úložiště se výběr jen nezapamatuje.
    }
  }

  function show(text) {
    message.hidden = !text;
    message.textContent = text || "";
  }

  // Barvy a filtr podle vozidel.
  function vehicleColor(id) {
    const index = (data?.vehicles ?? []).findIndex((vehicle) => vehicle.id === id);
    return index < 0 ? "#7d8696" : palette[index % palette.length];
  }

  function vehicleName(id) {
    const vehicle = (data?.vehicles ?? []).find((item) => item.id === id);
    return vehicle ? vehicle.plate : "Bez vozidla";
  }

  function openMain(hash) {
    const target = window.open(`${location.pathname}${location.search}${hash}`, mainWindow);
    target?.focus();
  }

  async function load() {
    const current = ++loading;
    show("Načítám kalendář…");
    let response;
    try {
      response = await fetch(`/api/dispatch/calendar/events?from=${weekStart}&days=7`, { cache: "no-store" });
    } catch {
      if (current === loading) show("Kalendář se nepodařilo načíst.");
      return;
    }
    if (response.status === 401) {
      location.href = "/login.html";
      return;
    }
    const body = await response.json().catch(() => ({}));
    if (current !== loading) return;
    if (!response.ok) {
      show(body.message || "Kalendář se nepodařilo načíst.");
      return;
    }
    data = body;
    show("");
    render();
  }

  function renderVehicles() {
    const rows = [...(data?.vehicles ?? []).map((vehicle) => ({ id: vehicle.id, name: vehicle.label ? `${vehicle.plate} · ${vehicle.label}` : vehicle.plate })), { id: "", name: "Bez vozidla" }];
    document.querySelector("#week-vehicles").innerHTML = rows.map((row) => `
      <label class="week-vehicle" style="--vehicle:${vehicleColor(row.id || null)}">
        <input type="checkbox" data-week-vehicle="${esc(row.id)}"${hidden.has(row.id) ? "" : " checked"}>
        <span>${esc(row.name)}</span>
      </label>`).join("");
  }

  // Rozložení překrývajících se zakázek vedle sebe v jednom dni.
  function layout(segments) {
    segments.sort((a, b) => a.top - b.top || b.bottom - a.bottom);
    let cluster = [];
    let columns = [];
    let clusterEnd = -1;
    const finish = () => {
      for (const item of cluster) item.columns = columns.length;
      cluster = [];
      columns = [];
    };
    for (const item of segments) {
      if (item.top >= clusterEnd) {
        finish();
        clusterEnd = -1;
      }
      let column = columns.findIndex((end) => end <= item.top);
      if (column < 0) {
        column = columns.length;
        columns.push(item.bottom);
      } else {
        columns[column] = item.bottom;
      }
      item.column = column;
      cluster.push(item);
      clusterEnd = Math.max(clusterEnd, item.bottom);
    }
    finish();
    return segments;
  }

  function render() {
    renderVehicles();
    const days = Array.from({ length: 7 }, (_, index) => shiftIso(weekStart, index));
    const today = todayIso();
    document.querySelector("#week-title").textContent = `${longDay.format(new Date(`${days[0]}T00:00:00Z`))} – ${longDay.format(new Date(`${days[6]}T00:00:00Z`))}`;
    const events = (data?.events ?? []).filter((event) => !hidden.has(event.vehicleId || ""));
    const hours = Array.from({ length: 24 }, (_, hour) => `<div class="wk-hour" style="height:${HOUR}px">${hour}:00</div>`).join("");
    const columns = days.map((day) => {
      const segments = [];
      for (const event of events) {
        const start = minutesFrom(day, event.start);
        const end = minutesFrom(day, event.end);
        if (end <= 0 || start >= 1440) continue;
        segments.push({ event, top: Math.max(start, 0), bottom: Math.min(end, 1440), before: start < 0, after: end > 1440, start, end });
      }
      const blocks = layout(segments).map((item) => {
        const { event } = item;
        const width = 100 / item.columns;
        const height = Math.max((item.bottom - item.top) / 60 * HOUR, 20);
        const time = `${item.before ? "↑ " : ""}${clock(item.start)}–${clock(item.end)}${item.after ? " ↓" : ""}`;
        const title = `${event.code} · ${vehicleName(event.vehicleId)}\n${event.customer}\n${event.loadingPlace} → ${event.unloadingPlace}\n${liters.format(event.liters)} l · ${event.status}${event.estimated ? "\nČas není zadaný, bere se 6:00–14:00." : ""}`;
        return `<button type="button" class="wk-event${event.estimated ? " estimated" : ""}" data-week-order="${esc(event.id)}"
          style="top:${item.top / 60 * HOUR}px;height:${height}px;left:calc(${item.column * width}% + 1px);width:calc(${width}% - 3px);--vehicle:${vehicleColor(event.vehicleId)}"
          title="${esc(title)}">
          <b>${esc(event.code)}</b> <small>${esc(time)}</small>
          <span>${esc(vehicleName(event.vehicleId))}</span>
          <span>${esc(event.customer)}</span>
        </button>`;
      }).join("");
      const now = day === today ? (() => {
        const date = new Date();
        return `<div class="wk-now" style="top:${(date.getHours() * 60 + date.getMinutes()) / 60 * HOUR}px"></div>`;
      })() : "";
      return `<div class="wk-col${day === today ? " today" : ""}" data-week-day="${day}" style="height:${24 * HOUR}px;--hour:${HOUR}px">${blocks}${now}</div>`;
    }).join("");
    grid.innerHTML = `
      <div class="wk-head"><div></div>${days.map((day) =>
        `<div class="wk-day${day === today ? " today" : ""}">${esc(dayHead.format(new Date(`${day}T00:00:00Z`)))}</div>`).join("")}</div>
      <div class="wk-body"><div class="wk-times">${hours}</div>${columns}</div>`;
    if (!scrolled) {
      scrolled = true;
      document.querySelector("#week-scroll").scrollTop = 6 * HOUR - 12;
    }
    if (!events.length && data?.events?.length) show("Všechna vozidla se zakázkami jsou skrytá.");
  }

  document.querySelector("#week-view").addEventListener("click", (event) => {
    const shift = event.target.closest("[data-week-shift]");
    if (shift) {
      weekStart = shiftIso(weekStart, Number(shift.dataset.weekShift));
      load();
      return;
    }
    if (event.target.closest("[data-week-today]")) {
      weekStart = mondayOf(todayIso());
      load();
      return;
    }
    if (event.target.closest("#week-refresh")) {
      load();
      return;
    }
    if (event.target.closest("#week-new")) {
      openMain("#nova");
      return;
    }
    const order = event.target.closest("[data-week-order]");
    if (order) {
      openMain(`#zakazka=${encodeURIComponent(order.dataset.weekOrder)}`);
      return;
    }
    const column = event.target.closest("[data-week-day]");
    if (column) {
      const rect = column.getBoundingClientRect();
      const minutes = Math.min(Math.max(Math.floor((event.clientY - rect.top) / HOUR * 2) * 30, 0), 1410);
      const visible = (data?.vehicles ?? []).filter((vehicle) => !hidden.has(vehicle.id));
      const vehicle = visible.length === 1 ? `&vozidlo=${encodeURIComponent(visible[0].id)}` : "";
      const time = `${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}`;
      openMain(`#nova=${column.dataset.weekDay}T${time}${vehicle}`);
    }
  });

  document.querySelector("#week-vehicles").addEventListener("change", (event) => {
    const box = event.target.closest("[data-week-vehicle]");
    if (!box) return;
    if (box.checked) hidden.delete(box.dataset.weekVehicle);
    else hidden.add(box.dataset.weekVehicle);
    saveHidden();
    show("");
    render();
  });

  window.addEventListener("hashchange", () => {
    const date = location.hash.match(/^#tyden=(\d{4}-\d{2}-\d{2})/)?.[1];
    if (!date) return;
    weekStart = mondayOf(date);
    load();
  });
  window.addEventListener("focus", load);
  setInterval(() => {
    if (document.visibilityState === "visible") load();
  }, 60000);

  load();
})();
