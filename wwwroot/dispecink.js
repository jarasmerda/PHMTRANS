const text = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
}[char]));
const dateTime = new Intl.DateTimeFormat("cs-CZ", { dateStyle: "short", timeStyle: "short" });
const message = document.querySelector("#message");
const statusInput = document.querySelector("#status");
const rows = document.querySelector("#rows");
const empty = document.querySelector("#empty");
let requestSeq = 0;
let saving = false;
let vehicles = [];
let drivers = [];
let orders = [];
let board = null;
let openTripId = null;
let rates = null;

function showMessage(value) {
  message.hidden = !value;
  message.textContent = value ?? "";
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
  const selected = statusInput.value;
  let visible = 0;
  for (const row of rows.querySelectorAll("tr[data-status]")) {
    const hide = selected.length > 0 && row.dataset.status !== selected;
    row.hidden = hide;
    if (!hide) visible += 1;
  }
  empty.hidden = visible > 0;
  empty.textContent = selected ? "V tomto stavu není žádná jízda." : "Zatím není žádná jízda.";
}

function tripOrders(tripId) {
  return orders.filter((order) => order.tripId === tripId);
}

function sumLiters(list) {
  return list.reduce((sum, order) => sum + Number(order.liters || 0), 0);
}

function litersLabel(value) {
  return new Intl.NumberFormat("cs-CZ", { maximumFractionDigits: 3 }).format(Number(value || 0));
}

function displayRow(trip) {
  const note = trip.note ? `<br><span class="route-to">${text(trip.note)}</span>` : "";
  const assigned = tripOrders(trip.id);
  const summary = assigned.length
    ? `${assigned.length} · ${litersLabel(sumLiters(assigned))} l`
    : "—";
  return `<tr data-id="${text(trip.id)}" data-status="${text(trip.status)}">
      <td>${text(trip.code)}</td>
      <td>${text(summary)}</td>
      <td>${formatDate(trip.plannedStart)}</td>
      <td>${text(trip.customer)}${note}</td>
      <td>${text(trip.loadingPlace)}<br><span class="route-to">→ ${text(trip.unloadingPlace)}</span></td>
      <td>${text(trip.cargo)}</td>
      <td>${text(vehicleText(trip.vehicleId))}</td>
      <td>${text(driverText(trip.driverId))}</td>
      <td>${text(trip.status)}</td>
      <td>
        <button type="button" data-orders="${text(trip.id)}">Zakázky</button>
        <button type="button" data-edit="${text(trip.id)}">Upravit</button>
      </td>
    </tr>`;
}

function editCells(trip) {
  return `<td>${text(trip.code)}</td>
      <td>${text(tripOrders(trip.id).map((order) => order.code).join(", ") || "—")}</td>
      <td><input data-field="planned" type="datetime-local" required aria-label="Termín ${text(trip.code)}" value="${text(toLocalInput(trip.plannedStart))}"></td>
      <td><div class="stack">
        <input data-field="customer" required maxlength="160" aria-label="Zákazník ${text(trip.code)}" value="${text(trip.customer)}">
        <input data-field="note" maxlength="500" placeholder="Poznámka" aria-label="Poznámka ${text(trip.code)}" value="${text(trip.note || "")}">
      </div></td>
      <td><div class="stack">
        <input data-field="loading" required maxlength="160" placeholder="Nakládka" aria-label="Nakládka ${text(trip.code)}" value="${text(trip.loadingPlace)}">
        <input data-field="unloading" required maxlength="160" placeholder="Vykládka" aria-label="Vykládka ${text(trip.code)}" value="${text(trip.unloadingPlace)}">
      </div></td>
      <td><input data-field="cargo" maxlength="80" aria-label="Náklad ${text(trip.code)}" value="${text(trip.cargo || "")}"></td>
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
  const box = document.querySelector("#create-order-list");
  const selected = new Set([...box.querySelectorAll("input:checked")].map((input) => input.value));
  const available = orders.filter((order) => !order.tripId);
  box.innerHTML = available.length
    ? available.map((order) => `<label><input type="checkbox" value="${text(order.id)}"${selected.has(order.id) ? " checked" : ""}> ${text(order.code)} · ${text(order.customerName)} · ${litersLabel(order.liters)} l</label>`).join("")
    : `<span class="route-to">Žádná volná zakázka.</span>`;
}

function prefillFromFirstOrder() {
  const checked = [...document.querySelectorAll("#create-order-list input:checked")];
  if (!checked.length) return;
  const order = orders.find((item) => item.id === checked[0].value);
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
  document.querySelector("#trip-detail-title").textContent = `${trip.code} · ${trip.customer}`;
  document.querySelector("#trip-detail-meta").textContent =
    `${trip.loadingPlace} → ${trip.unloadingPlace} · ${vehicleText(trip.vehicleId)} · ${driverText(trip.driverId)}`;
  document.querySelector("#trip-order-rows").innerHTML = assigned.map((order) => `<tr>
      <td>${text(order.code)}</td>
      <td>${text(order.customerName)}</td>
      <td>${text(order.loadingPlace)} → ${text(order.unloadingPlace)}</td>
      <td class="num">${litersLabel(order.liters)} l</td>
      <td><button type="button" class="ghost" data-remove-order="${text(order.id)}">Odebrat</button></td>
    </tr>`).join("") || `<tr><td colspan="5">Na jízdě zatím není zakázka.</td></tr>`;
  document.querySelector("#trip-liters").textContent = `${litersLabel(sumLiters(assigned))} l`;
  const free = orders.filter((order) => !order.tripId);
  document.querySelector("#trip-free-orders").innerHTML =
    `<option value="">— zakázka bez jízdy —</option>` +
    free.map((order) => `<option value="${text(order.id)}">${text(order.code)} · ${text(order.customerName)} · ${litersLabel(order.liters)} l</option>`).join("");
  detail.hidden = false;
  if (location.hash !== `#jizda-${id}`)
    history.replaceState(null, "", `#jizda-${id}`);
}

function renderRates() {
  if (!rates) return;
  document.querySelector("#default-cost").value = rates.defaultCostPerKm ?? 0;
  document.querySelector("#vehicle-rates").innerHTML = (rates.vehicles ?? []).map((vehicle) =>
    `<label>${text(vehicle.plate)} · ${text(vehicle.label)}
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
      fetch("/api/dispatch/board", { cache: "no-store" }),
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

window.openTrip = (id) => {
  document.querySelector("#tab-trips")?.click();
  showTrip(id);
  document.querySelector("#trip-detail")?.scrollIntoView({ behavior: "smooth", block: "start" });
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
  const status = field(row, "status");
  if (status === "Zrušená" && row.dataset.status !== "Zrušená" && !window.confirm("Označit jízdu jako zrušenou?"))
    return;
  if (!field(row, "customer").trim()) {
    showMessage("Vyplňte zákazníka.");
    return;
  }
  if (!field(row, "loading").trim()) {
    showMessage("Vyplňte místo nakládky.");
    return;
  }
  if (!field(row, "unloading").trim()) {
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
    customer: field(row, "customer"),
    loadingPlace: field(row, "loading"),
    unloadingPlace: field(row, "unloading"),
    cargo: field(row, "cargo"),
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
statusInput.addEventListener("change", applyFilter);
rows.addEventListener("click", (event) => {
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
  const orderIds = [...document.querySelectorAll("#create-order-list input:checked")].map((input) => input.value);
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
  showMessage("");
  await load();
});

document.querySelector("#create-order-list").addEventListener("change", prefillFromFirstOrder);
document.querySelector("#trip-detail-close").addEventListener("click", () => {
  openTripId = null;
  document.querySelector("#trip-detail").hidden = true;
  if (location.hash.startsWith("#jizda-")) history.replaceState(null, "", location.pathname);
});
document.querySelector("#trip-add-order").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (!openTripId || saving) return;
  const orderId = document.querySelector("#trip-free-orders").value;
  if (!orderId) {
    showMessage("Vyberte zakázku.");
    return;
  }
  saving = true;
  const added = await send(`/api/dispatch/trips/${encodeURIComponent(openTripId)}/orders`, { orderId });
  saving = false;
  if (!added) return;
  showMessage("");
  await load();
});
document.querySelector("#trip-order-rows").addEventListener("click", async (event) => {
  const button = event.target.closest("[data-remove-order]");
  if (!button || !openTripId || saving) return;
  saving = true;
  const removed = await send(
    `/api/dispatch/trips/${encodeURIComponent(openTripId)}/orders/${encodeURIComponent(button.dataset.removeOrder)}`,
    {},
    "DELETE");
  saving = false;
  if (!removed) return;
  showMessage("");
  await load();
});
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

load();
