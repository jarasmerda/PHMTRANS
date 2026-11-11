// Pohled řidiče: nahoře výběr řidiče, pod ním jeho kalendář na týden a karty zakázek
// jen ke čtení (co, kdy, kam). Ceny ani nic dalšího pro řidiče se tu neukazuje.
(() => {
  const storageKey = "phm-ridic";
  const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  }[char]));
  const dayName = new Intl.DateTimeFormat("cs-CZ", { weekday: "long", day: "numeric", month: "numeric", timeZone: "UTC" });
  const shortDay = new Intl.DateTimeFormat("cs-CZ", { weekday: "short", day: "numeric", month: "numeric", timeZone: "UTC" });
  const liters = new Intl.NumberFormat("cs-CZ", { maximumFractionDigits: 0 });
  const pad = (value) => String(value).padStart(2, "0");
  const select = document.querySelector("#driver-pick");
  const message = document.querySelector("#driver-message");
  let drivers = [];
  let weekStart = mondayOf(todayIso());
  let loading = 0;

  function todayIso() {
    const now = new Date();
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

  const asDate = (iso) => new Date(`${iso}T00:00:00Z`);
  const timeOf = (value) => String(value || "").slice(11, 16);
  const dateOf = (value) => String(value || "").slice(0, 10);

  function show(text) {
    message.hidden = !text;
    message.textContent = text || "";
  }

  function remembered() {
    try {
      return localStorage.getItem(storageKey) || "";
    } catch {
      return "";
    }
  }

  function remember(id) {
    try {
      localStorage.setItem(storageKey, id);
    } catch {
      // Bez úložiště se výběr jen nezapamatuje.
    }
  }

  async function getJson(url) {
    let response;
    try {
      response = await fetch(url, { cache: "no-store" });
    } catch {
      return { ok: false, data: { message: "Spojení se nepodařilo." } };
    }
    if (response.status === 401) {
      location.href = "/login.html";
      return { ok: false, data: {} };
    }
    return { ok: response.ok, data: await response.json().catch(() => ({})) };
  }

  async function loadDrivers() {
    const result = await getJson("/api/dispatch/drivers");
    drivers = Array.isArray(result.data) ? result.data : [];
    const chosen = select.value || remembered();
    select.innerHTML = `<option value="">— vyberte řidiče —</option>` + drivers.map((driver) =>
      `<option value="${esc(driver.id)}"${driver.id === chosen ? " selected" : ""}>${esc(driver.name)}</option>`).join("");
  }

  function placesText(order) {
    const stops = order.stops?.length ? order.stops.map((stop) => stop.place) : [order.unloadingPlace];
    return `${order.loadingPlace} → ${stops.filter(Boolean).join(", ")}`;
  }

  function loadsHtml(order) {
    const loads = order.loads?.length ? order.loads : [{ fuelType: order.fuelType, liters: order.liters }];
    return loads.map((load) => `<li><strong>${esc(load.fuelType || "palivo")}</strong> ${esc(liters.format(Number(load.liters || 0)))} l${
      load.compartments?.length ? ` <span class="dv-chip">přepážky ${esc(load.compartments.join(", "))}</span>` : ""}</li>`).join("");
  }

  function stopsHtml(order) {
    const stops = order.stops?.length ? order.stops : [{ place: order.unloadingPlace, liters: order.liters }];
    return stops.map((stop, index) => {
      const arrival = stop.arrivalAt ? new Date(stop.arrivalAt) : null;
      const arrivalText = arrival && !Number.isNaN(arrival.getTime())
        ? ` · příjezd ${arrival.toLocaleString("cs-CZ", { day: "numeric", month: "numeric", hour: "2-digit", minute: "2-digit" })}`
        : "";
      return `<li class="dv-stop ${index % 2 ? "b" : "a"}">
        <span class="dv-badge">Vykládka ${index + 1}</span>
        <strong>${esc(stop.place || "—")}</strong>
        <span>${esc(stop.fuelType || order.loads?.[0]?.fuelType || order.fuelType || "")} ${esc(liters.format(Number(stop.liters || 0)))} l${esc(arrivalText)}</span>
        ${stop.note ? `<span class="note">${esc(stop.note)}</span>` : ""}
      </li>`;
    }).join("");
  }

  function card(event, order) {
    const loadingTime = order.loadingTime || "";
    const unloadingTime = order.unloadingTime || "";
    return `<article class="dv-order" id="dv-order-${esc(order.id)}">
      <header>
        <strong>${esc(order.code)}</strong>
        <span class="status-pill" data-status="${esc(order.status)}">${esc(order.status)}</span>
        ${event.vehicle ? `<span class="dv-chip">${esc(event.vehicle)}</span>` : ""}
      </header>
      <section class="dv-when">
        <h4>Kdy</h4>
        <p>Nakládka: <strong>${esc(shortDay.format(asDate(order.loadingDate)))} ${esc(loadingTime || "")}</strong>${loadingTime ? "" : " <span class=\"note\">(čas neuveden)</span>"}</p>
        <p>Vykládka: <strong>${esc(shortDay.format(asDate(order.unloadingDate)))} ${esc(unloadingTime || "")}</strong>${unloadingTime ? "" : " <span class=\"note\">(čas neuveden)</span>"}</p>
      </section>
      <section class="dv-what">
        <h4>Co</h4>
        <ul>${loadsHtml(order)}</ul>
      </section>
      <section class="dv-where">
        <h4>Kam</h4>
        <p class="dv-load"><span class="dv-badge load">Nakládka</span> <strong>${esc(order.loadingPlace || "—")}</strong></p>
        <ol class="dv-stops">${stopsHtml(order)}</ol>
      </section>
      ${order.customerName ? `<p class="note">Zákazník: ${esc(order.customerName)}</p>` : ""}
      ${order.note ? `<p class="dv-note">Poznámka: ${esc(order.note)}</p>` : ""}
    </article>`;
  }

  function agenda(events, orders) {
    const days = Array.from({ length: 7 }, (_, index) => shiftIso(weekStart, index));
    const today = todayIso();
    return days.map((day) => {
      const items = events.filter((event) => dateOf(event.start) <= day && dateOf(event.end) >= day);
      const rows = items.map((event) => {
        const order = orders.get(event.id);
        const from = dateOf(event.start) === day ? timeOf(event.start) : "…";
        const to = dateOf(event.end) === day ? timeOf(event.end) : "…";
        return `<button type="button" class="dv-slot" data-dv-order="${esc(event.id)}">
          <span class="dv-time">${esc(from)}–${esc(to)}</span>
          <span><strong>${esc(event.code)}</strong> · ${esc(order ? placesText(order) : `${event.loadingPlace} → ${event.unloadingPlace}`)}</span>
        </button>`;
      }).join("");
      return `<div class="dv-day${day === today ? " today" : ""}${items.length ? "" : " free"}">
        <div class="dv-day-name">${esc(dayName.format(asDate(day)))}</div>
        <div class="dv-day-items">${rows || `<span class="note">volno</span>`}</div>
      </div>`;
    }).join("");
  }

  async function load() {
    const current = ++loading;
    const driverId = select.value;
    const end = shiftIso(weekStart, 6);
    document.querySelector("#driver-range-title").textContent =
      `${shortDay.format(asDate(weekStart))} – ${shortDay.format(asDate(end))}`;
    const agendaBox = document.querySelector("#driver-agenda");
    const ordersBox = document.querySelector("#driver-orders");
    if (!driverId) {
      agendaBox.innerHTML = "";
      ordersBox.innerHTML = "";
      show("Vyberte nahoře řidiče.");
      return;
    }
    show("Načítám…");
    const result = await getJson(`/api/dispatch/calendar/events?from=${weekStart}&days=7`);
    if (current !== loading) return;
    if (!result.ok) {
      show(result.data?.message || "Kalendář se nepodařilo načíst.");
      return;
    }
    const events = (result.data.events ?? []).filter((event) => event.driverId === driverId);
    const details = await Promise.all(events.map((event) => getJson(`/api/dispatch/orders/${encodeURIComponent(event.id)}`)));
    if (current !== loading) return;
    const orders = new Map();
    details.forEach((detail, index) => {
      if (detail.ok && detail.data?.order) orders.set(events[index].id, detail.data.order);
    });
    show(events.length ? "" : "V tomto týdnu nemáte žádnou zakázku.");
    agendaBox.innerHTML = agenda(events, orders);
    ordersBox.innerHTML = events.map((event) => orders.has(event.id) ? card(event, orders.get(event.id)) : "").join("");
  }

  document.querySelector("#tab-driver")?.addEventListener("click", () => {
    if (typeof showView === "function") showView("driver");
  });

  select.addEventListener("change", () => {
    remember(select.value);
    load();
  });

  document.querySelector("#driver-view").addEventListener("click", (event) => {
    const shift = event.target.closest("[data-dv-shift]");
    if (shift) {
      weekStart = shiftIso(weekStart, Number(shift.dataset.dvShift));
      load();
      return;
    }
    if (event.target.closest("[data-dv-today]")) {
      weekStart = mondayOf(todayIso());
      load();
      return;
    }
    const slot = event.target.closest("[data-dv-order]");
    if (slot) document.querySelector(`#dv-order-${CSS.escape(slot.dataset.dvOrder)}`)?.scrollIntoView({ behavior: "smooth", block: "start" });
  });

  // Volá ho showView("driver") z dispecink-orders.js.
  window.loadDriverView = async () => {
    if (!drivers.length) await loadDrivers();
    await load();
  };
})();
