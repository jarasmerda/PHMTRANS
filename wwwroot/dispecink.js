const text = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
}[char]));
const dateTime = new Intl.DateTimeFormat("cs-CZ", { dateStyle: "short", timeStyle: "short" });
const message = document.querySelector("#message");
const rows = document.querySelector("#rows");
const empty = document.querySelector("#empty");
let requestSeq = 0;
let saving = false;
let vehicles = [];
let drivers = [];
let orders = [];
let board = null;
let openTripId = null;
let expandedTripId = null;
let tripStatusFilter = null;
let rates = null;

const moneyLabel = new Intl.NumberFormat("cs-CZ", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

function showMessage(value) {
  message.hidden = !value;
  message.textContent = value ?? "";
  const local = document.querySelector("#trip-create-message");
  const dialog = document.querySelector("#trip-create-dialog");
  if (local && dialog?.open) {
    local.hidden = !value;
    local.textContent = value ?? "";
  }
}

function formatDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : dateTime.format(date);
}

function pad(value) {
  return String(value).padStart(2, "0");
}

function defaultPlanned() {
  const date = new Date(Date.now() + 60 * 60 * 1000);
  date.setMinutes(0, 0, 0);
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function toLocalInput(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function plannedIso(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

function options(items, selected, emptyLabel, labelOf, titleOf) {
  const head = `<option value="">${text(emptyLabel)}</option>`;
  const known = items.some((item) => item.id === selected);
  const extra = selected && !known
    ? `<option value="${text(selected)}" selected>${text(selected)}</option>`
    : "";
  const body = items.map((item) => {
    const title = titleOf ? titleOf(item) : "";
    const selectedAttr = item.id === selected ? " selected" : "";
    const titleAttr = title ? ` title="${text(title)}"` : "";
    return `<option value="${text(item.id)}"${selectedAttr}${titleAttr}>${text(labelOf(item))}</option>`;
  }).join("");
  return head + extra + body;
}

function statusOptions(selected) {
  return ["Nová", "Přiřazená", "Na cestě", "Vyložená", "Zrušená"].map((status) =>
    `<option${status === selected ? " selected" : ""}>${text(status)}</option>`).join("");
}

function vehicleLabel(item) {
  return `${item.plate} · ${item.label}`;
}

function vehicleText(id) {
  const item = vehicles.find((vehicle) => vehicle.id === id);
  return item ? vehicleLabel(item) : (id || "Nepřiřazeno");
}

function driverText(id) {
  const item = drivers.find((driver) => driver.id === id);
  return item ? item.name : (id || "Nepřiřazeno");
}

function fillTripForm(vehicleList, driverList) {
  const vehicleSelect = document.querySelector("#vehicle");
  const driverSelect = document.querySelector("#driver");
  vehicleSelect.innerHTML = options(vehicleList, vehicleSelect.value, "— nepřiřazeno —", vehicleLabel);
  driverSelect.innerHTML = options(driverList, driverSelect.value, "— nepřiřazeno —", (item) => item.name, (item) => item.phone || "");
}

function count(data, status) {
  const found = (data.counts ?? []).find((item) => item.status === status);
  return found ? found.count : 0;
}

function applyFilter() {
  let visible = 0;
  for (const row of rows.querySelectorAll("tr[data-id]")) {
    row.hidden = false;
    const extra = rows.querySelector(`tr[data-for="${CSS.escape(row.dataset.id)}"]`);
    if (extra) extra.hidden = extra.dataset.open !== "1";
    visible += 1;
  }
  empty.hidden = visible > 0;
  const filtered = tripStatusFilter && !tripStatusFilter.isAll();
  empty.textContent = filtered ? "V tomto stavu není žádná jízda." : "Zatím není žádná jízda.";
}

window.keepQuery = (hash) => `${location.pathname}${location.search}${hash || ""}`;

function statusQuery(filter) {
  const params = new URLSearchParams();
  for (const value of filter?.values() ?? []) params.append("status", value);
  const query = params.toString();
  return query ? `?${query}` : "";
}

function tripOrders(tripId) {
  return orders.filter((order) => order.tripId === tripId);
}

function countsInTotals(order) {
  return order.status !== "Stornováno";
}

function sumLiters(list) {
  return list.filter(countsInTotals).reduce((sum, order) => sum + Number(order.liters || 0), 0);
}

function uniqueTexts(values) {
  const seen = [];
  for (const value of values) {
    const textValue = String(value || "").trim();
    if (textValue && !seen.includes(textValue)) seen.push(textValue);
  }
  return seen;
}

function routeText(order) {
  const stops = (order.stops || []).map((stop) => stop.place);
  const tail = uniqueTexts(stops.length ? stops : [order.unloadingPlace]).join(" → ");
  if (!order.loadingPlace) return tail || "—";
  return tail ? `${order.loadingPlace} → ${tail}` : order.loadingPlace;
}

function orderKm(order) {
  return (order.legs || []).reduce((sum, leg) => sum + Number(leg.kilometers || 0), 0);
}

function orderSale(order) {
  return Number(order.agreedPrice || 0) + Number(order.surcharges || 0);
}

function tripTotals(tripId) {
  const assigned = tripOrders(tripId).filter(countsInTotals);
  return {
    count: assigned.length,
    liters: sumLiters(assigned),
    km: assigned.reduce((sum, order) => sum + orderKm(order), 0),
    price: assigned.reduce((sum, order) => sum + orderSale(order), 0)
  };
}

function totalsText(totals) {
  return `${litersLabel(totals.km)} km · ${litersLabel(totals.liters)} l · ${moneyLabel.format(totals.price)} Kč`;
}

function tripFace(trip) {
  const assigned = tripOrders(trip.id).filter(countsInTotals);
  if (!assigned.length) {
    return {
      customer: trip.customer,
      loading: trip.loadingPlace,
      unloading: trip.unloadingPlace,
      cargo: trip.cargo || ""
    };
  }
  const stops = assigned.flatMap((order) => (order.stops || []).map((stop) => stop.place));
  const ends = uniqueTexts(stops.length ? stops : assigned.map((order) => order.unloadingPlace));
  return {
    customer: uniqueTexts(assigned.map((order) => order.customerName)).join(", ") || trip.customer,
    loading: uniqueTexts(assigned.map((order) => order.loadingPlace)).join(", ") || trip.loadingPlace,
    unloading: ends.join(", ") || trip.unloadingPlace,
    cargo: uniqueTexts(assigned.map((order) => order.fuelType)).join(", ") || trip.cargo || ""
  };
}

function orderLine(order, tripId) {
  const remove = tripId
    ? `<button type="button" class="ghost" data-remove-order="${text(order.id)}" data-from-trip="${text(tripId)}">Odebrat</button>`
    : "";
  return `<tr>
      <td><button type="button" class="linkish" data-open-order="${text(order.id)}">${text(order.code)}</button></td>
      <td>${text(order.customerName)}</td>
      <td>${text(routeText(order))}</td>
      <td class="num">${litersLabel(order.liters)} l</td>
      <td class="num">${moneyLabel.format(orderSale(order))} Kč</td>
      <td>${remove}</td>
    </tr>`;
}

function orderTable(tripId) {
  const assigned = tripOrders(tripId);
  const totals = tripTotals(tripId);
  const free = orders.filter((order) => !order.tripId && countsInTotals(order));
  const add = `<form data-add-order="${text(tripId)}">
      <label>Přidat zakázku
        <select data-free-order>
          <option value="">— zakázka bez jízdy —</option>
          ${free.map((order) => `<option value="${text(order.id)}">${text(order.code)} · ${text(order.customerName)} · ${litersLabel(order.liters)} l</option>`).join("")}
        </select>
      </label>
      <button type="submit">Přidat na jízdu</button>
    </form>`;
  return `<p>Součet: <strong>${text(totalsText(totals))}</strong> <span class="route-to">Ze zakázek. Stornované se nepočítají.</span></p>
    <table>
      <thead><tr><th>Zakázka</th><th>Zákazník</th><th>Trasa</th><th>Litry</th><th>Prodej</th><th></th></tr></thead>
      <tbody>${assigned.map((order) => orderLine(order, tripId)).join("") || `<tr><td colspan="6">Na jízdě zatím není zakázka.</td></tr>`}</tbody>
    </table>
    ${add}`;
}

function litersLabel(value) {
  return new Intl.NumberFormat("cs-CZ", { maximumFractionDigits: 3 }).format(Number(value || 0));
}

function displayRow(trip) {
  const note = trip.note ? `<br><span class="route-to">${text(trip.note)}</span>` : "";
  const assigned = tripOrders(trip.id);
  const face = tripFace(trip);
  const totals = tripTotals(trip.id);
  const summary = assigned.length
    ? `${assigned.length} · ${totalsText(totals)}`
    : "—";
  const fromOrders = assigned.length ? `<br><span class="route-to">Ze zakázek</span>` : "";
  return `<tr data-id="${text(trip.id)}" data-status="${text(trip.status)}">
      <td>${text(trip.code)}</td>
      <td><button type="button" data-expand="${text(trip.id)}">${text(summary)}</button></td>
      <td>${formatDate(trip.plannedStart)}</td>
      <td>${text(face.customer)}${note}${fromOrders}</td>
      <td>${text(face.loading)}<br><span class="route-to">→ ${text(face.unloading)}</span></td>
      <td>${text(face.cargo)}</td>
      <td>${text(vehicleText(trip.vehicleId))}</td>
      <td>${text(driverText(trip.driverId))}</td>
      <td>${text(trip.status)}</td>
      <td>
        <button type="button" data-orders="${text(trip.id)}">Zakázky</button>
        ${trip.status === "Stornováno" ? "" : `<button type="button" data-edit="${text(trip.id)}">Upravit</button>`}
        ${trip.status === "Stornováno" ? "" : `<button type="button" class="ghost" data-storno="${text(trip.id)}">Stornovat</button>`}
      </td>
    </tr>
    <tr class="trip-orders" data-for="${text(trip.id)}" data-status="${text(trip.status)}" data-open="${expandedTripId === trip.id ? "1" : "0"}"${expandedTripId === trip.id ? "" : " hidden"}>
      <td colspan="10">${orderTable(trip.id)}</td>
    </tr>`;
}

function editCells(trip) {
  const assigned = tripOrders(trip.id);
  const face = tripFace(trip);
  const identity = assigned.length
    ? `<div class="stack"><strong>${text(face.customer)}</strong><span class="route-to">Ze zakázek</span><input data-field="note" maxlength="500" placeholder="Poznámka" aria-label="Poznámka ${text(trip.code)}" value="${text(trip.note || "")}"></div>`
    : `<div class="stack">
        <input data-field="customer" required maxlength="160" aria-label="Zákazník ${text(trip.code)}" value="${text(trip.customer)}">
        <input data-field="note" maxlength="500" placeholder="Poznámka" aria-label="Poznámka ${text(trip.code)}" value="${text(trip.note || "")}">
      </div>`;
  const route = assigned.length
    ? `<div class="stack"><span>${text(face.loading)}</span><span class="route-to">→ ${text(face.unloading)}</span></div>`
    : `<div class="stack">
        <input data-field="loading" required maxlength="160" placeholder="Nakládka" aria-label="Nakládka ${text(trip.code)}" value="${text(trip.loadingPlace)}">
        <input data-field="unloading" required maxlength="160" placeholder="Vykládka" aria-label="Vykládka ${text(trip.code)}" value="${text(trip.unloadingPlace)}">
      </div>`;
  const cargo = assigned.length
    ? text(face.cargo)
    : `<input data-field="cargo" maxlength="80" aria-label="Náklad ${text(trip.code)}" value="${text(trip.cargo || "")}">`;
  return `<td>${text(trip.code)}</td>
      <td>${orderTable(trip.id)}</td>
      <td><input data-field="planned" type="datetime-local" required aria-label="Termín ${text(trip.code)}" value="${text(toLocalInput(trip.plannedStart))}"></td>
      <td>${identity}</td>
      <td>${route}</td>
      <td>${cargo}</td>
      <td><select data-field="vehicle" aria-label="Vozidlo ${text(trip.code)}">${options(vehicles, trip.vehicleId, "Nepřiřazeno", vehicleLabel)}</select></td>
      <td><select data-field="driver" aria-label="Řidič ${text(trip.code)}">${options(drivers, trip.driverId, "Nepřiřazeno", (item) => item.name, (item) => item.phone || "")}</select></td>
      <td><select data-field="status" aria-label="Stav ${text(trip.code)}">${statusOptions(trip.status)}</select></td>
      <td><div class="edit-actions">
        <button type="button" data-save="${text(trip.id)}">Uložit změny</button>
        <button type="button" class="ghost" data-cancel="${text(trip.id)}">Zrušit</button>
      </div></td>`;
}

function render(data) {
  vehicles = data.vehicles ?? [];
  drivers = data.drivers ?? [];
  board = data;
  document.querySelector("#storage").hidden = false;
  document.querySelector("#storage").textContent = data.storageNote || "";
  document.querySelector("#count-nova").textContent = count(data, "Nová");
  document.querySelector("#count-prirazena").textContent = count(data, "Přiřazená");
  document.querySelector("#count-naceste").textContent = count(data, "Na cestě");
  document.querySelector("#count-vylozena").textContent = count(data, "Vyložená");
  document.querySelector("#count-zrusena").textContent = count(data, "Zrušená");
  document.querySelector("#count-stornovano").textContent = count(data, "Stornováno");
  document.querySelector("#cards").hidden = false;

  rows.innerHTML = (data.trips ?? []).map(displayRow).join("");
  document.querySelector("#table-wrap").hidden = false;
  applyFilter();
}

function beginEdit(id) {
  if (saving || !board) return;
  const trip = (board.trips ?? []).find((item) => item.id === id);
  if (!trip) return;
  render(board);
  const row = rows.querySelector(`tr[data-id="${CSS.escape(id)}"]`);
  if (!row) return;
  row.hidden = false;
  row.innerHTML = editCells(trip);
  row.querySelector("[data-field=customer]")?.focus();
}

function field(row, name) {
  return row.querySelector(`[data-field="${name}"]`).value;
}

function freeOrders(exceptTripId) {
  return orders.filter((order) => !order.tripId || order.tripId === exceptTripId);
}

function renderCreateOrders() {
  const list = document.querySelector("#create-order-list");
  const box = document.querySelector("#create-orders");
  const empty = document.querySelector("#create-order-empty");
  const selected = new Set([...list.selectedOptions].map((option) => option.value));
  const available = orders.filter((order) => !order.tripId && countsInTotals(order));
  if (!available.length) {
    box.hidden = true;
    list.innerHTML = "";
    empty.hidden = false;
    return;
  }
  box.hidden = false;
  empty.hidden = true;
  list.innerHTML = available.map((order) => {
    const line = `${order.code} · ${order.customerName} · ${order.loadingPlace} → ${order.unloadingPlace} · ${litersLabel(order.liters)} l`;
    return `<option value="${text(order.id)}"${selected.has(order.id) ? " selected" : ""}>${text(line)}</option>`;
  }).join("");
}

function prefillFromFirstOrder() {
  const selected = [...document.querySelector("#create-order-list").selectedOptions];
  if (!selected.length) return;
  const order = orders.find((item) => item.id === selected[0].value);
  if (!order) return;
  document.querySelector("#customer").value = order.customerName || "";
  document.querySelector("#loading").value = order.loadingPlace || "";
  document.querySelector("#unloading").value = order.unloadingPlace || "";
  document.querySelector("#cargo").value = order.fuelType || "";
}

function showTrip(id) {
  openTripId = id;
  const trip = (board?.trips ?? []).find((item) => item.id === id);
  const detail = document.querySelector("#trip-detail");
  if (!trip) {
    detail.hidden = true;
    return;
  }
  const assigned = tripOrders(id);
  const face = tripFace(trip);
  document.querySelector("#trip-detail-title").textContent = `${trip.code} · ${face.customer}`;
  document.querySelector("#trip-detail-meta").textContent =
    `${face.loading} → ${face.unloading} · ${vehicleText(trip.vehicleId)} · ${driverText(trip.driverId)}`;
  document.querySelector("#trip-order-rows").innerHTML = assigned.map((order) => orderLine(order, id)).join("")
    || `<tr><td colspan="6">Na jízdě zatím není zakázka.</td></tr>`;
  document.querySelector("#trip-totals").textContent = totalsText(tripTotals(id));
  const cancelledTrip = trip.status === "Stornováno";
  document.querySelector("#trip-add-order label").hidden = cancelledTrip;
  document.querySelector("#trip-add-order button[type=submit]").hidden = cancelledTrip;
  document.querySelector("#trip-storno").hidden = cancelledTrip;
  const free = orders.filter((order) => !order.tripId && countsInTotals(order));
  document.querySelector("#trip-free-orders").innerHTML =
    `<option value="">— zakázka bez jízdy —</option>` +
    free.map((order) => `<option value="${text(order.id)}">${text(order.code)} · ${text(order.customerName)} · ${litersLabel(order.liters)} l</option>`).join("");
  detail.hidden = false;
    if (location.hash !== `#jizda-${id}`)
    history.replaceState(null, "", window.keepQuery(`#jizda-${id}`));
}

function compartmentsText(vehicle) {
  const list = vehicle.compartments ?? [];
  if (!list.length) return "přepážky nezadané";
  const total = list.reduce((sum, item) => sum + Number(item.capacityLiters || 0), 0);
  const word = list.length === 1 ? "přepážka" : list.length < 5 ? "přepážky" : "přepážek";
  return `${list.length} ${word} · ${litersLabel(total)} l (${list.map((item) => litersLabel(item.capacityLiters)).join(" / ")})`;
}

function renderRates() {
  if (!rates) return;
  document.querySelector("#default-cost").value = rates.defaultCostPerKm ?? 0;
  document.querySelector("#vehicle-rates").innerHTML = (rates.vehicles ?? []).map((vehicle) =>
    `<label>${text(vehicle.plate)} · ${text(vehicle.label)}
      <small class="note">${text(compartmentsText(vehicle))}</small>
      <input data-vehicle-cost="${text(vehicle.id)}" type="number" min="0" step="0.0001" placeholder="výchozí" value="${vehicle.costPerKm ?? ""}">
    </label>`).join("");
}

async function loadTripOrders() {
  const response = await fetch("/api/dispatch/orders", { cache: "no-store" });
  if (response.status === 401) {
    location.href = "/login.html";
    return;
  }
  const data = await response.json().catch(() => ({}));
  orders = response.ok && Array.isArray(data.orders) ? data.orders : [];
  renderCreateOrders();
  if (openTripId) showTrip(openTripId);
}

async function loadRates() {
  const response = await fetch("/api/dispatch/rates", { cache: "no-store" });
  if (!response.ok) return;
  rates = await response.json().catch(() => null);
  renderRates();
}

async function load() {
  const current = ++requestSeq;
  showMessage("Načítám jízdy…");
  let boardResponse;
  let vehiclesResponse;
  let driversResponse;
  try {
    [boardResponse, vehiclesResponse, driversResponse] = await Promise.all([
      fetch(`/api/dispatch/board${statusQuery(tripStatusFilter)}`, { cache: "no-store" }),
      fetch("/api/dispatch/vehicles", { cache: "no-store" }),
      fetch("/api/dispatch/drivers", { cache: "no-store" })
    ]);
  } catch {
    if (current !== requestSeq) return;
    showMessage("Jízdy se nepodařilo načíst.");
    return;
  }
  if (current !== requestSeq) return;
  if (boardResponse.status === 401 || vehiclesResponse.status === 401 || driversResponse.status === 401) {
    location.href = "/login.html";
    return;
  }
  const data = await boardResponse.json().catch(() => ({}));
  const vehicleData = await vehiclesResponse.json().catch(() => null);
  const driverData = await driversResponse.json().catch(() => null);
  if (current !== requestSeq) return;
  if (!boardResponse.ok) {
    showMessage(data.message || "Jízdy se nepodařilo načíst.");
    return;
  }
  const catalogsOk = vehiclesResponse.ok && driversResponse.ok && Array.isArray(vehicleData) && Array.isArray(driverData);
  if (catalogsOk) {
    data.vehicles = vehicleData;
    data.drivers = driverData;
  }
  showMessage(catalogsOk ? "" : ((vehicleData && vehicleData.message) || (driverData && driverData.message) || "Vozidla a řidiče se nepodařilo načíst."));
  render(data);
  fillTripForm(data.vehicles ?? [], data.drivers ?? []);
  await loadTripOrders();
  await loadRates();
  if (current === requestSeq && board) render(board);
  const hashTrip = location.hash.startsWith("#jizda-") ? location.hash.slice("#jizda-".length) : "";
  if (hashTrip) showTrip(decodeURIComponent(hashTrip));
}

window.openTrip = async (id) => {
  document.querySelector("#tab-trips")?.click();
  if (!(board?.trips ?? []).some((item) => item.id === id))
    await load();
  showTrip(id);
  const detail = document.querySelector("#trip-detail");
  if (detail && !detail.hidden) {
    showMessage("");
    detail.scrollIntoView({ behavior: "smooth", block: "start" });
  } else {
    showMessage("Jízdu se nepodařilo otevřít.");
  }
};

async function send(url, body, method = "POST") {
  let response;
  try {
    response = await fetch(url, {
      method,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
  } catch {
    showMessage("Uložení se nepodařilo.");
    return null;
  }
  if (response.status === 401) {
    location.href = "/login.html";
    return null;
  }
  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    showMessage(data.message || "Uložení se nepodařilo.");
    return null;
  }
  return data;
}

async function saveRow(id) {
  if (saving) return;
  const row = rows.querySelector(`tr[data-id="${CSS.escape(id)}"]`);
  if (!row) return;
  const trip = (board?.trips ?? []).find((item) => item.id === id);
  const fromOrders = tripOrders(id).length > 0;
  const customer = fromOrders ? (trip?.customer || "") : field(row, "customer");
  const loadingPlace = fromOrders ? (trip?.loadingPlace || "") : field(row, "loading");
  const unloadingPlace = fromOrders ? (trip?.unloadingPlace || "") : field(row, "unloading");
  const cargo = fromOrders ? (trip?.cargo || "") : field(row, "cargo");
  const status = field(row, "status");
  if (status === "Zrušená" && row.dataset.status !== "Zrušená" && !window.confirm("Označit jízdu jako zrušenou?"))
    return;
  if (!fromOrders && !customer.trim()) {
    showMessage("Vyplňte zákazníka.");
    return;
  }
  if (!fromOrders && !loadingPlace.trim()) {
    showMessage("Vyplňte místo nakládky.");
    return;
  }
  if (!fromOrders && !unloadingPlace.trim()) {
    showMessage("Vyplňte místo vykládky.");
    return;
  }
  const plannedStart = plannedIso(field(row, "planned"));
  if (!plannedStart) {
    showMessage("Vyplňte termín jízdy.");
    return;
  }

  saving = true;
  const updated = await send(`/api/dispatch/trips/${encodeURIComponent(id)}`, {
    customer,
    loadingPlace,
    unloadingPlace,
    cargo,
    plannedStart,
    note: field(row, "note"),
    vehicleId: field(row, "vehicle"),
    driverId: field(row, "driver"),
    status
  }, "PUT");
  saving = false;
  if (!updated) return;
  showMessage("");
  await load();
}

document.querySelector("#logout").addEventListener("click", async () => {
  await fetch("/api/logout", { method: "POST" });
  location.href = "/login.html";
});

document.querySelector("#reload").addEventListener("click", () => load());
rows.addEventListener("submit", (event) => {
  const form = event.target.closest("[data-add-order]");
  if (!form) return;
  event.preventDefault();
  const orderId = form.querySelector("[data-free-order]")?.value;
  if (!orderId) {
    showMessage("Vyberte zakázku.");
    return;
  }
  addOrderToTrip(form.dataset.addOrder, orderId);
});

rows.addEventListener("click", (event) => {
  const expand = event.target.closest("[data-expand]");
  if (expand) {
    const extra = rows.querySelector(`tr[data-for="${CSS.escape(expand.dataset.expand)}"]`);
    if (!extra) return;
    const open = extra.dataset.open !== "1";
    extra.dataset.open = open ? "1" : "0";
    extra.hidden = !open;
    expandedTripId = open ? expand.dataset.expand : null;
    return;
  }
  const openOrder = event.target.closest("[data-open-order]");
  if (openOrder && window.showOrder) {
    window.showOrder(openOrder.dataset.openOrder);
    return;
  }
  const remove = event.target.closest("[data-remove-order]");
  if (remove) {
    removeOrderFromTrip(remove.dataset.fromTrip || openTripId, remove.dataset.removeOrder);
    return;
  }
  const save = event.target.closest("[data-save]");
  if (save) {
    saveRow(save.dataset.save);
    return;
  }
  const ordersButton = event.target.closest("[data-orders]");
  if (ordersButton) {
    showTrip(ordersButton.dataset.orders);
    return;
  }
  const edit = event.target.closest("[data-edit]");
  if (edit) {
    beginEdit(edit.dataset.edit);
    return;
  }
  const storno = event.target.closest("[data-storno]");
  if (storno) {
    cancelTrip(storno.dataset.storno);
    return;
  }
  const cancel = event.target.closest("[data-cancel]");
  if (cancel && board) render(board);
});

document.querySelector("#planned").value = defaultPlanned();
document.querySelector("#create").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (saving) return;
  const plannedStart = plannedIso(document.querySelector("#planned").value);
  if (!plannedStart) {
    showMessage("Vyplňte termín jízdy.");
    return;
  }
  saving = true;
  const orderIds = [...document.querySelector("#create-order-list").selectedOptions].map((option) => option.value);
  const created = await send("/api/dispatch/trips", {
    customer: document.querySelector("#customer").value,
    loadingPlace: document.querySelector("#loading").value,
    unloadingPlace: document.querySelector("#unloading").value,
    cargo: document.querySelector("#cargo").value,
    plannedStart,
    note: document.querySelector("#note").value,
    vehicleId: document.querySelector("#vehicle").value,
    driverId: document.querySelector("#driver").value,
    orderIds
  });
  saving = false;
  if (!created) return;
  event.target.reset();
  document.querySelector("#vehicle").value = "";
  document.querySelector("#driver").value = "";
  document.querySelector("#planned").value = defaultPlanned();
  document.querySelector("#trip-create-dialog")?.close();
  showMessage("");
  await load();
});

document.querySelector("#create-order-list").addEventListener("change", prefillFromFirstOrder);
function canHardDeleteTrip(trip) {
  return trip
    && trip.status === "Nová"
    && !trip.vehicleId
    && !trip.driverId
    && tripOrders(trip.id).length === 0;
}

async function cancelTrip(id) {
  if (saving || !board) return;
  const trip = (board.trips ?? []).find((item) => item.id === id);
  if (!trip || trip.status === "Stornováno") return;
  const affected = tripOrders(id);
  if (canHardDeleteTrip(trip)) {
    if (!window.confirm(`Jízda ${trip.code} nemá zakázky a nebyla použitá. Smazat ji natrvalo?`)) return;
    saving = true;
    const removed = await send(`/api/dispatch/trips/${encodeURIComponent(id)}`, {}, "DELETE");
    saving = false;
    if (!removed) return;
    if (openTripId === id) {
      openTripId = null;
      document.querySelector("#trip-detail").hidden = true;
    }
    showMessage("Jízda je smazaná.");
    await load();
    return;
  }
  const lines = affected.length
    ? affected.map((order) => `${order.code} · ${order.customerName}`).join("\n")
    : "žádné zakázky";
  if (!window.confirm(`Stornovat jízdu ${trip.code}? Zakázky zůstanou a odeberou se z jízdy:\n${lines}`)) return;
  saving = true;
  const cancelled = await send(`/api/dispatch/trips/${encodeURIComponent(id)}/cancel`, {}, "POST");
  saving = false;
  if (!cancelled) return;
  showMessage("Jízda je stornovaná. Zakázky zůstaly bez jízdy.");
  await load();
}

document.querySelector("#trip-storno").addEventListener("click", () => {
  if (openTripId) cancelTrip(openTripId);
});

document.querySelector("#trip-detail-close").addEventListener("click", () => {
  openTripId = null;
  document.querySelector("#trip-detail").hidden = true;
  if (location.hash.startsWith("#jizda-")) history.replaceState(null, "", window.keepQuery(""));
});
async function addOrderToTrip(tripId, orderId) {
  if (!tripId || !orderId || saving) return;
  saving = true;
  const added = await send(`/api/dispatch/trips/${encodeURIComponent(tripId)}/orders`, { orderId });
  saving = false;
  if (!added) return;
  showMessage("");
  await load();
  if (openTripId === tripId) showTrip(tripId);
}

async function removeOrderFromTrip(tripId, orderId) {
  if (!tripId || !orderId || saving) return;
  saving = true;
  const removed = await send(
    `/api/dispatch/trips/${encodeURIComponent(tripId)}/orders/${encodeURIComponent(orderId)}`,
    {},
    "DELETE");
  saving = false;
  if (!removed) return;
  showMessage("");
  await load();
  if (openTripId === tripId) showTrip(tripId);
}

document.querySelector("#trip-add-order").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (!openTripId || saving) return;
  const orderId = document.querySelector("#trip-free-orders").value;
  if (!orderId) {
    showMessage("Vyberte zakázku.");
    return;
  }
  await addOrderToTrip(openTripId, orderId);
});
document.querySelector("#trip-order-rows").addEventListener("click", (event) => {
  const openOrder = event.target.closest("[data-open-order]");
  if (openOrder && window.showOrder) {
    window.showOrder(openOrder.dataset.openOrder);
    return;
  }
  const button = event.target.closest("[data-remove-order]");
  if (!button || saving) return;
  removeOrderFromTrip(button.dataset.fromTrip || openTripId, button.dataset.removeOrder);
});
document.querySelector("#open-trip-create")?.addEventListener("click", () => {
  document.querySelector("#trip-create-message").hidden = true;
  document.querySelector("#trip-create-dialog")?.showModal();
});
for (const button of document.querySelectorAll("[data-close-dialog]")) {
  button.addEventListener("click", () => button.closest("dialog")?.close());
}
document.querySelector("#rates").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (saving) return;
  const vehicles = [...document.querySelectorAll("[data-vehicle-cost]")].map((input) => ({
    id: input.dataset.vehicleCost,
    costPerKm: input.value.trim() === "" ? null : Number(input.value)
  }));
  saving = true;
  const saved = await send("/api/dispatch/rates", {
    defaultCostPerKm: Number(document.querySelector("#default-cost").value),
    vehicles
  }, "PUT");
  saving = false;
  if (!saved) return;
  rates = saved;
  renderRates();
  showMessage("Sazby jsou uložené.");
});

tripStatusFilter = window.bindStatusFilter(document.querySelector("#trip-status-filter"), {
  param: "jizdy",
  known: ["Nová", "Přiřazená", "Na cestě", "Vyložená", "Zrušená", "Stornováno"],
  excludeByDefault: ["Stornováno"],
  onChange() { load(); }
});

load();
