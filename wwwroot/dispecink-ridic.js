// Pohled řidiče: nahoře výběr řidiče, pod ním jeho týden (každý den jako časová osa 0–24 h)
// a karty zakázek: co, kdy, kam (s adresou, GPS, kontaktem a navigací), přepážky,
// doklady k nafocení a Potvrdit k fakturaci. Ceny se tu neukazují.
(() => {
  const storageKey = "phm-ridic";
  const closed = ["K fakturaci", "Vyfakturováno", "Stornováno"];
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
  let points = new Map();
  let orders = new Map();
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
  const minutes = (time) => {
    const [hours, mins] = String(time || "0:0").split(":").map(Number);
    return (hours || 0) * 60 + (mins || 0);
  };

  function show(text) {
    message.hidden = !text;
    message.textContent = text || "";
  }

  // Hláška po potvrzení k fakturaci (volá dispecink-orders.js).
  window.driverViewNotice = (text) => {
    show(text);
    message.scrollIntoView({ behavior: "smooth", block: "center" });
  };

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
    const [driverResult, pointResult] = await Promise.all([getJson("/api/dispatch/drivers"), getJson("/api/dispatch/points")]);
    drivers = Array.isArray(driverResult.data) ? driverResult.data : [];
    points = new Map((Array.isArray(pointResult.data) ? pointResult.data : [])
      .map((point) => [String(point.name || "").trim().toLowerCase(), point]));
    const chosen = select.value || remembered();
    select.innerHTML = `<option value="">— vyberte řidiče —</option>` + drivers.map((driver) =>
      `<option value="${esc(driver.id)}"${driver.id === chosen ? " selected" : ""}>${esc(driver.name)}</option>`).join("");
  }

  function placesText(order) {
    const stops = order.stops?.length ? order.stops.map((stop) => stop.place) : [order.unloadingPlace];
    return `${order.loadingPlace} → ${stops.filter(Boolean).join(", ")}`;
  }

  function loadsOf(order) {
    return order.loads?.length ? order.loads : [{ fuelTypeId: order.fuelTypeId, fuelType: order.fuelType, liters: order.liters }];
  }

  function compartmentsText(item) {
    const numbers = item.compartments?.length ? item.compartments : item.compartment ? [item.compartment] : [];
    if (!numbers.length) return "";
    return ` <span class="dv-chip comp">${numbers.length > 1 ? "z přepážek" : "z přepážky"} ${esc(numbers.join(", "))}</span>`;
  }

  // Co se na vykládce vykládá: palivo, litry a přepážka (u starších zakázek přepážky paliva z nakládky).
  function stopItemsHtml(order, stop) {
    if (stop.items?.length) {
      return `<ul class="dv-stop-items">${stop.items.map((item) => `<li><strong>${esc(item.fuelType || "palivo")}</strong> ${esc(liters.format(Number(item.liters || 0)))} l${
        compartmentsText(item)}</li>`).join("")}</ul>`;
    }
    const compartments = stopCompartments(order, stop);
    return `<span>${esc(stop.fuelType || loadsOf(order)[0]?.fuelType || "")} ${esc(liters.format(Number(stop.liters || 0)))} l${
      compartments.length ? ` <span class="dv-chip comp">z přepážek ${esc(compartments.join(", "))}</span>` : ""}</span>`;
  }

  // Přepážky, ze kterých se vykládá palivo této vykládky.
  function stopCompartments(order, stop) {
    const loads = loadsOf(order);
    const fuelId = stop.fuelTypeId ?? (stop.fuelType ? null : loads[0]?.fuelTypeId);
    const fuelName = String(stop.fuelType || (fuelId == null ? loads[0]?.fuelType : "") || "").trim().toLowerCase();
    const load = loads.find((item) => (fuelId != null && item.fuelTypeId === fuelId)
      || (fuelName && String(item.fuelType || "").trim().toLowerCase() === fuelName));
    return load?.compartments ?? [];
  }

  // Adresa, GPS, kontakt, poznámka a navigace k místu (z dbo.Disp_Point).
  function placeInfo(name) {
    const point = points.get(String(name || "").trim().toLowerCase());
    const hasGps = point && point.latitude != null && point.longitude != null;
    const target = hasGps ? `${point.latitude},${point.longitude}` : (point?.address || name || "");
    const lines = [];
    if (point?.address) lines.push(`<span>📍 ${esc(point.address)}</span>`);
    if (hasGps) lines.push(`<span class="dv-gps">GPS ${esc(point.latitude)}, ${esc(point.longitude)}</span>`);
    if (point?.contact) lines.push(`<span>☎ ${esc(point.contact)}</span>`);
    if (point?.note) lines.push(`<span class="dv-place-note">${esc(point.note)}</span>`);
    const links = target
      ? `<span class="dv-nav">
          <a href="https://www.google.com/maps/dir/?api=1&destination=${encodeURIComponent(target)}" target="_blank" rel="noopener">Navigovat (Google)</a>
          <a href="https://mapy.cz/zakladni?q=${encodeURIComponent(target)}" target="_blank" rel="noopener">Mapy.cz</a>
        </span>`
      : "";
    return `<div class="dv-place">${lines.join("")}${links}${point ? "" : `<span class="note">Adresa místa není v systému zadaná.</span>`}</div>`;
  }

  function loadsHtml(order) {
    return loadsOf(order).map((load) => `<li><strong>${esc(load.fuelType || "palivo")}</strong> ${esc(liters.format(Number(load.liters || 0)))} l${
      load.compartments?.length ? ` <span class="dv-chip comp">přepážky ${esc(load.compartments.join(", "))}</span>` : ""}</li>`).join("");
  }

  function stopsHtml(order) {
    const stops = order.stops?.length ? order.stops : [{ place: order.unloadingPlace, liters: order.liters }];
    return stops.map((stop, index) => {
      const arrival = stop.arrivalAt ? new Date(stop.arrivalAt) : null;
      const arrivalText = arrival && !Number.isNaN(arrival.getTime())
        ? ` · příjezd ${arrival.toLocaleString("cs-CZ", { day: "numeric", month: "numeric", hour: "2-digit", minute: "2-digit" })}`
        : "";
      return `<li class="dv-stop ${index % 2 ? "b" : "a"}">
        <div class="dv-stop-head">
          <span class="dv-badge">Vykládka ${index + 1}</span>
          <strong>${esc(stop.place || "—")}</strong>${arrivalText ? `<span>${esc(arrivalText.replace(/^ · /, ""))}</span>` : ""}
        </div>
        ${stop.note ? `<p class="dv-stop-note">📝 Poznámka k vykládce: ${esc(stop.note)}</p>` : ""}
        ${stopItemsHtml(order, stop)}
        ${placeInfo(stop.place)}
      </li>`;
    }).join("");
  }

  function card(event, order) {
    const loadingTime = order.loadingTime || "";
    const unloadingTime = order.unloadingTime || "";
    const canConfirm = !closed.includes(order.status) && !order.isLocked;
    return `<article class="dv-order" id="dv-order-${esc(order.id)}">
      <header>
        <strong>${esc(order.code)}</strong>
        <span class="status-pill" data-status="${esc(order.status)}">${esc(order.status)}</span>
        ${event.vehicle ? `<span class="dv-chip">${esc(event.vehicle)}</span>` : ""}
      </header>
      <section class="dv-when">
        <h4>Kdy</h4>
        <p>Nakládka: <strong>${esc(shortDay.format(asDate(order.loadingDate)))} ${esc(loadingTime)}</strong>${loadingTime ? "" : " <span class=\"note\">(čas neuveden)</span>"}</p>
        <p>Vykládka: <strong>${esc(shortDay.format(asDate(order.unloadingDate)))} ${esc(unloadingTime)}</strong>${unloadingTime ? "" : " <span class=\"note\">(čas neuveden)</span>"}</p>
      </section>
      <section class="dv-what">
        <h4>Co</h4>
        <ul>${loadsHtml(order)}</ul>
      </section>
      <section class="dv-where">
        <h4>Kam</h4>
        <div class="dv-load">
          <div class="dv-stop-head"><span class="dv-badge load">Nakládka</span> <strong>${esc(order.loadingPlace || "—")}</strong></div>
          ${placeInfo(order.loadingPlace)}
        </div>
        <ol class="dv-stops">${stopsHtml(order)}</ol>
      </section>
      ${order.customerName ? `<p class="note">Zákazník: ${esc(order.customerName)}</p>` : ""}
      ${order.note ? `<p class="dv-note">📝 Poznámka k zakázce: ${esc(order.note)}</p>` : ""}
      ${order.missingDocumentsReason ? `<p class="dv-note">Chybějící doklady: ${esc(order.missingDocumentsReason)}</p>` : ""}
      <div class="dv-actions">
        <button type="button" class="dv-docs" data-dv-docs="${esc(order.id)}">📷 Doklady</button>
        ${canConfirm ? `<button type="button" class="ghost" data-dv-confirm="${esc(order.id)}">Potvrdit k fakturaci</button>` : ""}
      </div>
    </article>`;
  }

  // Den jako časová osa 0–24 h: na první pohled je vidět, kdy je zakázka naplánovaná.
  function dayBar(day, items, today) {
    const blocks = items.map((event, index) => {
      const from = dateOf(event.start) < day ? 0 : minutes(timeOf(event.start));
      const to = dateOf(event.end) > day ? 1440 : minutes(timeOf(event.end));
      const left = from / 1440 * 100;
      const width = Math.max((to - from) / 1440 * 100, 2);
      return `<button type="button" class="dv-block ${index % 2 ? "b" : "a"}${event.estimated ? " estimated" : ""}" data-dv-order="${esc(event.id)}"
        style="left:${left}%;width:${width}%" title="${esc(`${event.code} ${timeOf(event.start)}–${timeOf(event.end)}`)}">${esc(event.code)}</button>`;
    }).join("");
    const now = day === today ? (() => {
      const date = new Date();
      return `<span class="dv-now" style="left:${(date.getHours() * 60 + date.getMinutes()) / 1440 * 100}%"></span>`;
    })() : "";
    return `<div class="dv-bar">${blocks}${now}</div>
      <div class="dv-scale"><span>0</span><span>6</span><span>12</span><span>18</span><span>24</span></div>`;
  }

  function agenda(events) {
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
        <div class="dv-day-name">${esc(dayName.format(asDate(day)))}${items.length ? "" : ` <span class="note">volno</span>`}</div>
        ${dayBar(day, items, today)}
        ${rows ? `<div class="dv-day-items">${rows}</div>` : ""}
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
    orders = new Map();
    details.forEach((detail, index) => {
      if (detail.ok && detail.data?.order) orders.set(events[index].id, detail.data.order);
    });
    show(events.length ? "" : "V tomto týdnu nemáte žádnou zakázku.");
    agendaBox.innerHTML = agenda(events);
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
    const docs = event.target.closest("[data-dv-docs]");
    if (docs) {
      const order = orders.get(docs.dataset.dvDocs);
      if (order && typeof window.openOrderDocuments === "function") window.openOrderDocuments(order);
      return;
    }
    const confirm = event.target.closest("[data-dv-confirm]");
    if (confirm) {
      const order = orders.get(confirm.dataset.dvConfirm);
      if (!order) return;
      // Doklady se načtou, aby šlo poznat, které chybí, a pak se potvrdí.
      Promise.resolve(window.loadOrderDocuments?.(order)).then(() => window.confirmOrderToInvoice?.(order.id));
      return;
    }
    const slot = event.target.closest("[data-dv-order]");
    if (slot) document.querySelector(`#dv-order-${CSS.escape(slot.dataset.dvOrder)}`)?.scrollIntoView({ behavior: "smooth", block: "start" });
  });

  // Po zavření okna dokladů se karty obnoví (stav, důvod).
  document.querySelector("#docs-dialog")?.addEventListener("close", () => {
    const view = document.querySelector("#driver-view");
    if (view && !view.hidden && select.value) load();
  });

  // Volá ho showView("driver") z dispecink-orders.js.
  window.loadDriverView = async () => {
    if (!drivers.length) await loadDrivers();
    await load();
  };
})();
