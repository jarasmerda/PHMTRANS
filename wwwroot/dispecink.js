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

function plannedIso(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

function options(items, selected, emptyLabel, labelOf, titleOf) {
  const head = `<option value="">${text(emptyLabel)}</option>`;
  const body = items.map((item) => {
    const title = titleOf ? titleOf(item) : "";
    const selectedAttr = item.id === selected ? " selected" : "";
    const titleAttr = title ? ` title="${text(title)}"` : "";
    return `<option value="${text(item.id)}"${selectedAttr}${titleAttr}>${text(labelOf(item))}</option>`;
  }).join("");
  return head + body;
}

function statusOptions(selected) {
  return ["Nová", "Přiřazená", "Na cestě", "Vyložená", "Zrušená"].map((status) =>
    `<option${status === selected ? " selected" : ""}>${text(status)}</option>`).join("");
}

function vehicleLabel(item) {
  return `${item.plate} · ${item.label}`;
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

function render(data) {
  vehicles = data.vehicles ?? [];
  drivers = data.drivers ?? [];
  document.querySelector("#storage").hidden = false;
  document.querySelector("#storage").textContent = data.storageNote || "";
  document.querySelector("#count-nova").textContent = count(data, "Nová");
  document.querySelector("#count-prirazena").textContent = count(data, "Přiřazená");
  document.querySelector("#count-naceste").textContent = count(data, "Na cestě");
  document.querySelector("#count-vylozena").textContent = count(data, "Vyložená");
  document.querySelector("#count-zrusena").textContent = count(data, "Zrušená");
  document.querySelector("#cards").hidden = false;

  rows.innerHTML = (data.trips ?? []).map((trip) => `<tr data-id="${text(trip.id)}" data-status="${text(trip.status)}">
      <td>${text(trip.code)}</td>
      <td>${text(trip.orderCode || "—")}</td>
      <td>${formatDate(trip.plannedStart)}</td>
      <td>${text(trip.customer)}${trip.note ? `<br><span class="route-to">${text(trip.note)}</span>` : ""}</td>
      <td>${text(trip.loadingPlace)}<br><span class="route-to">→ ${text(trip.unloadingPlace)}</span></td>
      <td>${text(trip.cargo)}</td>
      <td><select data-field="vehicle" aria-label="Vozidlo ${text(trip.code)}">${options(vehicles, trip.vehicleId, "Nepřiřazeno", vehicleLabel)}</select></td>
      <td><select data-field="driver" aria-label="Řidič ${text(trip.code)}">${options(drivers, trip.driverId, "Nepřiřazeno", (item) => item.name, (item) => item.phone || "")}</select></td>
      <td><select data-field="status" aria-label="Stav ${text(trip.code)}">${statusOptions(trip.status)}</select></td>
      <td><button type="button" data-save="${text(trip.id)}">Uložit</button></td>
    </tr>`).join("");
  document.querySelector("#table-wrap").hidden = false;
  applyFilter();
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
}

async function send(url, body) {
  let response;
  try {
    response = await fetch(url, {
      method: "POST",
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
  const status = row.querySelector("[data-field=status]").value;
  if (status === "Zrušená" && row.dataset.status !== "Zrušená" && !window.confirm("Označit jízdu jako zrušenou?"))
    return;

  saving = true;
  const vehicleId = row.querySelector("[data-field=vehicle]").value;
  const driverId = row.querySelector("[data-field=driver]").value;
  const userChangedStatus = status !== row.dataset.status;
  const assigned = await send(`/api/dispatch/trips/${encodeURIComponent(id)}/assign`, { vehicleId, driverId });
  if (!assigned) {
    saving = false;
    return;
  }
  if (userChangedStatus && status !== assigned.status) {
    const changed = await send(`/api/dispatch/trips/${encodeURIComponent(id)}/status`, { status });
    if (!changed) {
      saving = false;
      await load();
      return;
    }
  }
  saving = false;
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
  const button = event.target.closest("[data-save]");
  if (button) saveRow(button.dataset.save);
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
  const created = await send("/api/dispatch/trips", {
    customer: document.querySelector("#customer").value,
    orderCode: document.querySelector("#order-code").value,
    loadingPlace: document.querySelector("#loading").value,
    unloadingPlace: document.querySelector("#unloading").value,
    cargo: document.querySelector("#cargo").value,
    plannedStart,
    note: document.querySelector("#note").value,
    vehicleId: document.querySelector("#vehicle").value,
    driverId: document.querySelector("#driver").value
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

load();
