const orderText = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
}[char]));
const orderMoney = new Intl.NumberFormat("cs-CZ", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const orderLiters = new Intl.NumberFormat("cs-CZ", { maximumFractionDigits: 3 });
const orderTime = new Intl.DateTimeFormat("cs-CZ", { dateStyle: "short", timeStyle: "short" });
const orderStatuses = ["Nová", "Naplánovaná", "Na cestě", "Doručeno", "K fakturaci", "Vyfakturováno"];
const orderMessage = document.querySelector("#order-message");
const queueMessage = document.querySelector("#queue-message");
let orderCatalog = { vehicles: [], drivers: [], trips: [] };
let orderList = [];
let orderDetail = null;
let orderSaving = false;
let queueItems = [];

function orderShow(target, value) {
  target.hidden = !value;
  target.textContent = value ?? "";
}

function orderDay(value) {
  if (!value) return "";
  const parts = String(value).slice(0, 10).split("-");
  if (parts.length !== 3) return value;
  return `${parts[2]}. ${parts[1]}. ${parts[0]}`;
}

function orderWhen(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : orderTime.format(date);
}

function orderToday() {
  const date = new Date();
  const pad = (part) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function orderOptions(items, selected, emptyLabel, labelOf) {
  const head = `<option value="">${orderText(emptyLabel)}</option>`;
  const body = items.map((item) => {
    const selectedAttr = item.id === selected ? " selected" : "";
    return `<option value="${orderText(item.id)}"${selectedAttr}>${orderText(labelOf(item))}</option>`;
  }).join("");
  return head + body;
}

function orderStatusOptions(selected) {
  return orderStatuses.map((status) =>
    `<option${status === selected ? " selected" : ""}>${orderText(status)}</option>`).join("");
}

function syncLabel(status) {
  return {
    Pending: "Čeká",
    Sent: "Odesláno",
    Failed: "Chyba",
    DryRun: "Zkouška"
  }[status] || (status || "—");
}

function actionLabel(action) {
  return { Create: "Založení", Update: "Aktualizace", Correction: "Oprava" }[action] || action;
}

function syncPill(status) {
  if (!status) return "—";
  const kind = status === "Failed" ? "warn" : status === "Sent" ? "ok" : "";
  return `<span class="pill ${kind}">${orderText(syncLabel(status))}</span>`;
}

function priceText(order) {
  const total = Number(order.agreedPrice || 0) + Number(order.surcharges || 0);
  return `${orderMoney.format(total)} Kč`;
}

function formFields(prefix, order) {
  const vehicle = order?.vehicleId || "";
  const driver = order?.driverId || "";
  const trip = order?.tripId || "";
  const status = order?.status || "Nová";
  const mode = order?.pricingMode === "Liters" ? "Liters" : "Km";
  const stops = order?.stops?.length
    ? order.stops
    : [{ place: order?.unloadingPlace || "", liters: order?.liters ?? "", note: "", arrivalAt: "" }];
  const legs = order?.legs || [];
  return `
    <label class="wide">Zákazník
      <input id="${prefix}-customer" required maxlength="160" value="${orderText(order?.customerName || "")}">
    </label>
    <label>Id v Pohodě
      <input id="${prefix}-customer-id" inputmode="numeric" value="${orderText(order?.customerPohodaId ?? "")}">
    </label>
    <div class="wide">
      <button type="button" class="ghost" data-search="${prefix}">Hledat v adresáři Pohody</button>
      <div class="picker" id="${prefix}-results"></div>
    </div>
    <label>Nakládka <input id="${prefix}-loading" required maxlength="160" value="${orderText(order?.loadingPlace || "")}"></label>
    <label>Datum nakládky <input id="${prefix}-loading-date" type="date" required value="${orderText(order?.loadingDate || orderToday())}"></label>
    <label>Datum vykládky <input id="${prefix}-unloading-date" type="date" required value="${orderText(order?.unloadingDate || orderToday())}"></label>
    <label>Druh paliva <input id="${prefix}-fuel" required maxlength="80" value="${orderText(order?.fuelType || "")}" placeholder="motorová nafta"></label>
    <label>Naložené litry <input id="${prefix}-liters" type="number" min="0" step="0.001" required value="${orderText(order?.liters ?? "")}"></label>
    <label>Příplatky (Kč) <input id="${prefix}-surcharges" type="number" step="0.01" value="${orderText(order?.surcharges ?? 0)}"></label>
    <label>Vozidlo <select id="${prefix}-vehicle">${orderOptions(orderCatalog.vehicles, vehicle, "— nepřiřazeno —", (item) => `${item.plate} · ${item.label}`)}</select></label>
    <label>Řidič <select id="${prefix}-driver">${orderOptions(orderCatalog.drivers, driver, "— nepřiřazeno —", (item) => item.name)}</select></label>
    <label>Jízda <select id="${prefix}-trip">${orderOptions(orderCatalog.trips, trip, "— bez jízdy —", (item) => `${item.code} · ${item.customer}`)}</select></label>
    <div><button type="button" class="ghost" data-open-trip="${prefix}">Otevřít jízdu</button></div>
    <label>Režim prodeje
      <select id="${prefix}-mode">
        <option value="Km"${mode === "Km" ? " selected" : ""}>Za kilometr</option>
        <option value="Liters"${mode === "Liters" ? " selected" : ""}>Za litr</option>
      </select>
    </label>
    <label>Prodej Kč/km <input id="${prefix}-sale-km" type="number" min="0" step="0.0001" value="${orderText(order?.salePerKm ?? 0)}"></label>
    <label>Prodej Kč/l <input id="${prefix}-sale-liter" type="number" min="0" step="0.0001" value="${orderText(order?.salePerLiter ?? 0)}"></label>
    <label>Náklad Kč/km <input id="${prefix}-cost-km" type="number" min="0" step="0.0001" value="${orderText(order?.costPerKm ?? "")}"></label>
    <label>Stav <select id="${prefix}-status">${orderStatusOptions(status)}</select></label>
    <label>Číslo CMR <input id="${prefix}-cmr" maxlength="40" value="${orderText(order?.cmrNumber || "")}"></label>
    <label>Číslo faktury <input id="${prefix}-invoice" maxlength="40" value="${orderText(order?.pohodaInvoiceNumber || "")}"></label>
    <label class="wide">Poznámka <input id="${prefix}-note" maxlength="500" value="${orderText(order?.note || "")}"></label>
    <div class="wide" id="${prefix}-route" data-route="${prefix}">
      <h3>Vykládky a úseky</h3>
      <div id="${prefix}-stops">${stops.map((stop, index) => stopRow(prefix, stop, legs[index])).join("")}</div>
      <button type="button" data-add-stop="${prefix}">Přidat vykládku</button>
      <div id="${prefix}-totals" class="totals"></div>
      <p id="${prefix}-liter-warn" class="route-warn" hidden>Součet litrů na vykládkách je vyšší než naložené litry.</p>
    </div>
    ${prefix === "create" ? `<div class="wide"><button type="submit">Založit zakázku</button></div>` : ""}
  `;
}

function stopRow(prefix, stop, leg) {
  const arrival = stop?.arrivalAt ? toOrderLocal(stop.arrivalAt) : "";
  return `<div class="stop-row" data-stop-row>
      <label>Místo <input data-stop-place maxlength="160" value="${orderText(stop?.place || "")}"></label>
      <label>Litry <input data-stop-liters type="number" min="0" step="0.001" value="${orderText(stop?.liters ?? "")}"></label>
      <label>Km <input data-leg-km type="number" min="0" step="0.001" value="${orderText(leg?.kilometers ?? "")}"></label>
      <label>Prodej Kč <input data-leg-sale type="number" min="0" step="0.01" value="${orderText(leg?.salePrice ?? "")}"${leg?.saleOverridden ? "" : " readonly"}></label>
      <label class="stop-override"><input data-leg-override type="checkbox"${leg?.saleOverridden ? " checked" : ""}> Ruční cena</label>
      <label class="stop-note">Poznámka <input data-stop-note maxlength="500" value="${orderText(stop?.note || "")}"></label>
      <label class="stop-arrival">Příjezd <input data-stop-arrival type="datetime-local" value="${orderText(arrival)}"></label>
      <div class="stop-actions">
        <button type="button" data-stop-up>Nahoru</button>
        <button type="button" data-stop-down>Dolů</button>
        <button type="button" class="ghost" data-stop-remove>Odebrat</button>
      </div>
    </div>`;
}

function toOrderLocal(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  const pad = (part) => String(part).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function roundMoney(value) {
  return Math.round((Number(value) + Number.EPSILON) * 100) / 100;
}

function routeState(prefix) {
  const rows = [...document.querySelectorAll(`#${prefix}-stops [data-stop-row]`)];
  const loading = document.querySelector(`#${prefix}-loading`)?.value.trim() || "";
  const mode = document.querySelector(`#${prefix}-mode`)?.value || "Km";
  const salePerKm = Number(document.querySelector(`#${prefix}-sale-km`)?.value || 0);
  const salePerLiter = Number(document.querySelector(`#${prefix}-sale-liter`)?.value || 0);
  const costPerKm = Number(document.querySelector(`#${prefix}-cost-km`)?.value || 0);
  const stops = [];
  const legs = [];
  rows.forEach((row, index) => {
    const place = row.querySelector("[data-stop-place]").value.trim();
    const liters = Number(row.querySelector("[data-stop-liters]").value);
    const kmText = row.querySelector("[data-leg-km]").value.trim();
    const kilometers = kmText === "" ? null : Number(kmText);
    const overridden = row.querySelector("[data-leg-override]").checked;
    const overridePrice = Number(row.querySelector("[data-leg-sale]").value || 0);
    const from = index === 0 ? loading : stops[index - 1].place;
    const safeLiters = Number.isFinite(liters) ? liters : 0;
    const safeKm = kilometers === null || !Number.isFinite(kilometers) ? 0 : kilometers;
    const sale = overridden
      ? roundMoney(overridePrice)
      : mode === "Liters"
        ? roundMoney(safeLiters * (Number.isFinite(salePerLiter) ? salePerLiter : 0))
        : roundMoney(safeKm * (Number.isFinite(salePerKm) ? salePerKm : 0));
    const cost = roundMoney(safeKm * (Number.isFinite(costPerKm) ? costPerKm : 0));
    stops.push({
      place,
      liters: Number.isFinite(liters) ? liters : null,
      note: row.querySelector("[data-stop-note]").value,
      arrivalAt: row.querySelector("[data-stop-arrival]").value
        ? new Date(row.querySelector("[data-stop-arrival]").value).toISOString()
        : null
    });
    legs.push({
      from,
      to: place,
      kilometers,
      salePrice: sale,
      saleOverridden: overridden,
      cost
    });
    row.querySelector("[data-leg-sale]").readOnly = !overridden;
    if (!overridden) row.querySelector("[data-leg-sale]").value = sale;
  });
  return { stops, legs, mode, salePerKm, salePerLiter, costPerKm };
}

function refreshRoute(prefix) {
  const state = routeState(prefix);
  const loaded = Number(document.querySelector(`#${prefix}-liters`)?.value || 0);
  const surcharges = Number(document.querySelector(`#${prefix}-surcharges`)?.value || 0);
  const delivered = state.stops.reduce((sum, stop) => sum + Number(stop.liters || 0), 0);
  const totalKm = state.legs.reduce((sum, leg) => sum + Number(leg.kilometers || 0), 0);
  const legSale = roundMoney(state.legs.reduce((sum, leg) => sum + leg.salePrice, 0));
  const totalCost = roundMoney(state.legs.reduce((sum, leg) => sum + leg.cost, 0));
  const totalSale = roundMoney(legSale + (Number.isFinite(surcharges) ? surcharges : 0));
  const margin = roundMoney(totalSale - totalCost);
  const marginPerKm = totalKm === 0 ? 0 : roundMoney(margin / totalKm);
  const marginPercent = totalSale === 0 ? 0 : Math.round((margin / totalSale) * 10000) / 100;
  const box = document.querySelector(`#${prefix}-totals`);
  if (box) {
    const cards = [
      ["Km", totalKm],
      ["Naloženo l", loaded],
      ["Vyloženo l", delivered],
      ["Náklad Kč", totalCost],
      ["Prodej Kč", totalSale],
      ["Marže Kč", margin],
      ["Marže Kč/km", marginPerKm],
      ["Marže %", marginPercent]
    ];
    box.innerHTML = cards.map(([label, value]) =>
      `<article><span>${orderText(label)}</span><strong>${orderText(orderMoney.format(Number(value) || 0))}</strong></article>`).join("");
  }
  const warn = document.querySelector(`#${prefix}-liter-warn`);
  if (warn) warn.hidden = !(delivered > loaded);
  return state;
}

function readForm(prefix) {
  const value = (name) => document.querySelector(`#${prefix}-${name}`).value;
  const idText = value("customer-id").trim();
  const liters = Number(value("liters"));
  const surcharges = value("surcharges").trim() === "" ? 0 : Number(value("surcharges"));
  const route = refreshRoute(prefix);
  const agreed = roundMoney(route.legs.reduce((sum, leg) => sum + leg.salePrice, 0));
  const last = route.stops.length ? route.stops[route.stops.length - 1].place : "";
  return {
    customerPohodaId: idText === "" ? null : Number(idText),
    customerName: value("customer"),
    loadingPlace: value("loading"),
    unloadingPlace: last,
    loadingDate: value("loading-date"),
    unloadingDate: value("unloading-date"),
    fuelType: value("fuel"),
    liters: Number.isFinite(liters) ? liters : null,
    agreedPrice: agreed,
    surcharges: Number.isFinite(surcharges) ? surcharges : null,
    vehicleId: value("vehicle"),
    driverId: value("driver"),
    tripId: value("trip"),
    status: value("status"),
    cmrNumber: value("cmr"),
    pohodaInvoiceNumber: value("invoice"),
    note: value("note"),
    pricingMode: value("mode"),
    salePerKm: Number(value("sale-km")),
    salePerLiter: Number(value("sale-liter")),
    costPerKm: value("cost-km").trim() === "" ? null : Number(value("cost-km")),
    stops: route.stops,
    legs: route.legs.map((leg) => ({
      kilometers: leg.kilometers,
      salePrice: leg.salePrice,
      saleOverridden: leg.saleOverridden
    }))
  };
}

function applyTripCrew(prefix) {
  const tripId = document.querySelector(`#${prefix}-trip`)?.value || "";
  const trip = orderCatalog.trips.find((item) => item.id === tripId);
  const vehicle = document.querySelector(`#${prefix}-vehicle`);
  const driver = document.querySelector(`#${prefix}-driver`);
  if (!vehicle || !driver) return;
  if (!trip) {
    vehicle.disabled = false;
    driver.disabled = false;
    return;
  }
  vehicle.value = trip.vehicleId || "";
  driver.value = trip.driverId || "";
  vehicle.disabled = true;
  driver.disabled = true;
  const cost = document.querySelector(`#${prefix}-cost-km`);
  if (cost && cost.dataset.touched !== "1") {
    const vehicleRate = orderCatalog.vehicles.find((item) => item.id === trip.vehicleId)?.costPerKm;
    if (vehicleRate !== undefined && vehicleRate !== null && cost.value.trim() === "")
      cost.value = vehicleRate;
  }
}

function orderCount(data, status) {
  const found = (data.counts ?? []).find((item) => item.status === status);
  return found ? found.count : 0;
}

function renderOrders(data) {
  orderList = data.orders ?? [];
  document.querySelector("#order-sync-note").textContent = data.syncNote || "";
  document.querySelector("#ocount-nova").textContent = orderCount(data, "Nová");
  document.querySelector("#ocount-naplanovana").textContent = orderCount(data, "Naplánovaná");
  document.querySelector("#ocount-naceste").textContent = orderCount(data, "Na cestě");
  document.querySelector("#ocount-doruceno").textContent = orderCount(data, "Doručeno");
  document.querySelector("#ocount-kfakturaci").textContent = orderCount(data, "K fakturaci");
  document.querySelector("#ocount-vyfakturovano").textContent = orderCount(data, "Vyfakturováno");
  document.querySelector("#order-cards").hidden = false;
  document.querySelector("#order-rows").innerHTML = orderList.map((order) => `<tr>
      <td>${orderText(order.code)}<br><span class="route-to">v${orderText(order.currentVersion)}</span></td>
      <td>${orderText(order.customerName)}</td>
      <td>${orderText(order.loadingPlace)}<br><span class="route-to">→ ${orderText(order.unloadingPlace)}</span></td>
      <td>${orderDay(order.loadingDate)}</td>
      <td>${orderText(order.fuelType)}<br><span class="route-to">${orderLiters.format(Number(order.liters || 0))} l</span></td>
      <td class="num">${orderText(priceText(order))}</td>
      <td>${orderText(order.status)}${order.isLocked ? `<br><span class="pill">Zámek</span>` : ""}</td>
      <td>${orderText(order.pohodaOrderNumber || "—")}<br>${syncPill(order.latestSyncStatus)}</td>
      <td><button type="button" data-open="${orderText(order.id)}">Otevřít</button></td>
    </tr>`).join("");
  document.querySelector("#order-table-wrap").hidden = false;
  const empty = document.querySelector("#orders-empty");
  empty.hidden = orderList.length > 0;
  empty.textContent = document.querySelector("#order-status").value
    ? "V tomto stavu není žádná zakázka."
    : "Zatím není žádná zakázka.";
}

function renderDetail(data) {
  orderDetail = data;
  const order = data.order;
  document.querySelector("#order-detail").hidden = false;
  document.querySelector("#order-detail-title").textContent = `${order.code} · verze ${order.currentVersion}`;
  document.querySelector("#order-lock-note").hidden = !order.isLocked;
  document.querySelector("#order-confirm").disabled = !!order.isLocked;
  document.querySelector("#order-edit").innerHTML = formFields("edit", order);
  applyTripCrew("edit");
  refreshRoute("edit");
  document.querySelector("#version-rows").innerHTML = (data.versions ?? []).map((version) => `<tr>
      <td>${orderText(version.currentVersion)}</td>
      <td>${orderWhen(version.updatedAt)}</td>
      <td>${orderText(version.customerName)}</td>
      <td>${orderText(version.loadingPlace)} → ${orderText(version.unloadingPlace)}</td>
      <td>${orderDay(version.loadingDate)} – ${orderDay(version.unloadingDate)}</td>
      <td>${orderText(version.fuelType)}, ${orderLiters.format(Number(version.liters || 0))} l</td>
      <td class="num">${orderMoney.format(Number(version.agreedPrice || 0))}</td>
      <td class="num">${orderMoney.format(Number(version.surcharges || 0))}</td>
      <td>${orderText(version.status)}</td>
    </tr>`).join("");
  document.querySelector("#order-sync").innerHTML = (data.sync ?? []).length === 0
    ? `<p class="note">Do Pohody se zatím nic neposílalo. Poznámka, řidič a CMR se do fronty nedávají.</p>`
    : (data.sync ?? []).map((item) => `<article>
        <p><strong>${orderText(actionLabel(item.action))}</strong> verze ${orderText(item.versionFrom ?? "—")} → ${orderText(item.versionTo)}
        ${syncPill(item.status)} pokus ${orderText(item.attempts)}${item.pohodaDocNumber ? `, doklad ${orderText(item.pohodaDocNumber)}` : ""}</p>
        ${item.lastError ? `<p class="note">${orderText(item.lastError)}</p>` : ""}
        <pre class="xml">${orderText(item.payloadXml)}</pre>
      </article>`).join("");
}

function renderQueue(data) {
  queueItems = data.items ?? [];
  document.querySelector("#queue-sync-note").textContent = data.syncNote || "";
  document.querySelector("#queue-rows").innerHTML = queueItems.map((item) => `<tr>
      <td>${orderWhen(item.createdUtc)}</td>
      <td>${orderText(item.orderCode)}</td>
      <td>${orderText(actionLabel(item.action))}</td>
      <td>${orderText(item.versionFrom ?? "—")} → ${orderText(item.versionTo)}</td>
      <td>${syncPill(item.status)}</td>
      <td class="num">${orderText(item.attempts)}</td>
      <td>${orderText(item.pohodaDocNumber || "—")}</td>
      <td>${orderText(item.lastError || "")}</td>
      <td><button type="button" class="ghost" data-xml="${orderText(item.id)}">XML</button></td>
    </tr>`).join("");
  document.querySelector("#queue-empty").hidden = queueItems.length > 0;
}

function showView(name) {
  document.querySelector("#trips-view").hidden = name !== "trips";
  document.querySelector("#orders-view").hidden = name !== "orders";
  document.querySelector("#queue-view").hidden = name !== "queue";
  for (const button of document.querySelectorAll(".view-tabs button"))
    button.removeAttribute("aria-current");
  document.querySelector(`#tab-${name}`)?.setAttribute("aria-current", "page");
  if (name === "orders") loadOrders();
  if (name === "queue") loadQueue();
  if (name === "trips") history.replaceState(null, "", location.pathname);
  if (name === "orders") history.replaceState(null, "", "#zakazky");
  if (name === "queue") history.replaceState(null, "", "#fronta");
}

async function orderFetch(url, options) {
  let response;
  try {
    response = await fetch(url, { cache: "no-store", ...options });
  } catch {
    return { ok: false, status: 0, data: { message: "Spojení se nepodařilo." } };
  }
  if (response.status === 401) {
    location.href = "/login.html";
    return { ok: false, status: 401, data: {} };
  }
  const data = await response.json().catch(() => ({}));
  return { ok: response.ok, status: response.status, data };
}

async function loadCatalog() {
  const [vehicles, drivers, trips] = await Promise.all([
    orderFetch("/api/dispatch/vehicles"),
    orderFetch("/api/dispatch/drivers"),
    orderFetch("/api/dispatch/trips")
  ]);
  orderCatalog = {
    vehicles: Array.isArray(vehicles.data) ? vehicles.data : [],
    drivers: Array.isArray(drivers.data) ? drivers.data : [],
    trips: Array.isArray(trips.data) ? trips.data : []
  };
  document.querySelector("#order-create").innerHTML = formFields("create");
  applyTripCrew("create");
  refreshRoute("create");
}

async function loadOrders() {
  orderShow(orderMessage, "Načítám zakázky…");
  const status = document.querySelector("#order-status").value;
  const query = status ? `?status=${encodeURIComponent(status)}` : "";
  const result = await orderFetch(`/api/dispatch/orders${query}`);
  if (!result.ok) {
    orderShow(orderMessage, result.data.message || "Zakázky se nepodařilo načíst.");
    return;
  }
  orderShow(orderMessage, "");
  renderOrders(result.data);
  if (orderDetail?.order?.id)
    await openOrder(orderDetail.order.id, false);
}

async function openOrder(id, scroll) {
  const result = await orderFetch(`/api/dispatch/orders/${encodeURIComponent(id)}`);
  if (!result.ok) {
    orderShow(orderMessage, result.data.message || "Zakázku se nepodařilo načíst.");
    return;
  }
  renderDetail(result.data);
  if (scroll)
    document.querySelector("#order-detail").scrollIntoView({ behavior: "smooth", block: "start" });
}

async function loadQueue() {
  orderShow(queueMessage, "Načítám frontu…");
  const result = await orderFetch("/api/dispatch/sync");
  if (!result.ok) {
    orderShow(queueMessage, result.data.message || "Frontu se nepodařilo načíst.");
    return;
  }
  orderShow(queueMessage, "");
  renderQueue(result.data);
}

async function searchCustomers(prefix) {
  const name = document.querySelector(`#${prefix}-customer`).value.trim();
  const results = document.querySelector(`#${prefix}-results`);
  if (name.length < 2) {
    orderShow(orderMessage, "Pro hledání v adresáři napište aspoň dva znaky názvu.");
    return;
  }
  orderShow(orderMessage, "Hledám v adresáři Pohody…");
  const result = await orderFetch(`/api/dispatch/customers?q=${encodeURIComponent(name)}`);
  if (!result.ok) {
    orderShow(orderMessage, result.data.message || "Adresář Pohody se nepodařilo načíst.");
    results.innerHTML = "";
    return;
  }
  const items = Array.isArray(result.data) ? result.data : [];
  orderShow(orderMessage, items.length ? "" : "V adresáři Pohody nic není. Název můžete nechat ručně.");
  results.innerHTML = items.map((item) =>
    `<button type="button" data-pick="${prefix}" data-id="${orderText(item.id)}" data-name="${orderText(item.name)}">${orderText(item.name)}${item.city ? ` · ${orderText(item.city)}` : ""}</button>`).join("");
}

async function saveOrder(url, body, method) {
  if (orderSaving) return null;
  orderSaving = true;
  const result = await orderFetch(url, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
  orderSaving = false;
  if (!result.ok) {
    orderShow(orderMessage, result.data.message || "Uložení se nepodařilo.");
    return null;
  }
  return result.data;
}

document.querySelector("#tab-trips").addEventListener("click", () => showView("trips"));
document.querySelector("#tab-orders").addEventListener("click", () => showView("orders"));
document.querySelector("#tab-queue").addEventListener("click", () => showView("queue"));
document.querySelector("#order-reload").addEventListener("click", () => loadOrders());
document.querySelector("#order-status").addEventListener("change", () => loadOrders());
document.querySelector("#queue-reload").addEventListener("click", () => loadQueue());

document.querySelector("#orders-view").addEventListener("click", (event) => {
  const open = event.target.closest("[data-open]");
  if (open) {
    openOrder(open.dataset.open, true);
    return;
  }
  const search = event.target.closest("[data-search]");
  if (search) {
    searchCustomers(search.dataset.search);
    return;
  }
  const pick = event.target.closest("[data-pick]");
  if (pick) {
    document.querySelector(`#${pick.dataset.pick}-customer`).value = pick.dataset.name;
    document.querySelector(`#${pick.dataset.pick}-customer-id`).value = pick.dataset.id;
    document.querySelector(`#${pick.dataset.pick}-results`).innerHTML = "";
    return;
  }
  const openTrip = event.target.closest("[data-open-trip]");
  if (openTrip) {
    const tripId = document.querySelector(`#${openTrip.dataset.openTrip}-trip`)?.value;
    if (tripId && window.openTrip) window.openTrip(tripId);
    else orderShow(orderMessage, "Nejdřív vyberte jízdu.");
    return;
  }
  const route = event.target.closest("[data-route]");
  if (!route) return;
  const prefix = route.dataset.route;
  const row = event.target.closest("[data-stop-row]");
  if (event.target.closest("[data-add-stop]")) {
    document.querySelector(`#${prefix}-stops`).insertAdjacentHTML("beforeend", stopRow(prefix, {}, null));
    refreshRoute(prefix);
    return;
  }
  if (!row) {
    refreshRoute(prefix);
    return;
  }
  if (event.target.closest("[data-stop-remove]")) {
    if (document.querySelectorAll(`#${prefix}-stops [data-stop-row]`).length > 1)
      row.remove();
    refreshRoute(prefix);
    return;
  }
  const move = (direction) => {
    const sibling = direction < 0 ? row.previousElementSibling : row.nextElementSibling;
    if (!sibling) return;
    if (direction < 0) row.parentElement.insertBefore(row, sibling);
    else row.parentElement.insertBefore(sibling, row);
  };
  if (event.target.closest("[data-stop-up]")) move(-1);
  if (event.target.closest("[data-stop-down]")) move(1);
  if (event.target.matches("[data-leg-override]")) {
    const sale = row.querySelector("[data-leg-sale]");
    sale.readOnly = !event.target.checked;
  }
  refreshRoute(prefix);
});

document.querySelector("#orders-view").addEventListener("input", (event) => {
  if (event.target.matches("[id$='-cost-km']")) event.target.dataset.touched = "1";
  const route = event.target.closest("[data-route]") || event.target.closest("form");
  const prefix = event.target.id?.startsWith("edit-") ? "edit" : event.target.id?.startsWith("create-") ? "create" : route?.dataset?.route;
  if (prefix) refreshRoute(prefix);
});

document.querySelector("#orders-view").addEventListener("change", (event) => {
  if (event.target.matches("[id$='-trip']")) {
    const prefix = event.target.id.startsWith("edit-") ? "edit" : "create";
    applyTripCrew(prefix);
  }
});

document.querySelector("#queue-rows").addEventListener("click", (event) => {
  const button = event.target.closest("[data-xml]");
  if (!button) return;
  const item = queueItems.find((entry) => String(entry.id) === button.dataset.xml);
  const preview = document.querySelector("#queue-xml");
  preview.hidden = false;
  preview.textContent = item?.payloadXml || "";
});

document.querySelector("#order-create").addEventListener("submit", async (event) => {
  event.preventDefault();
  const created = await saveOrder("/api/dispatch/orders", readForm("create"), "POST");
  if (!created) return;
  document.querySelector("#order-create").innerHTML = formFields("create");
  orderShow(orderMessage, `Zakázka ${created.code} je založená.`);
  await loadOrders();
  await openOrder(created.id, true);
});

document.querySelector("#order-save").addEventListener("click", async () => {
  if (!orderDetail?.order) return;
  const saved = await saveOrder(`/api/dispatch/orders/${encodeURIComponent(orderDetail.order.id)}`, readForm("edit"), "PUT");
  if (!saved) return;
  orderShow(orderMessage, "Změny jsou uložené.");
  await loadOrders();
  await openOrder(saved.id, false);
});

document.querySelector("#order-confirm").addEventListener("click", async () => {
  if (!orderDetail?.order || !window.confirm("Potvrdit zakázku k fakturaci a zařadit ji do Pohody?")) return;
  const saved = await saveOrder(`/api/dispatch/orders/${encodeURIComponent(orderDetail.order.id)}/confirm`, {}, "POST");
  if (!saved) return;
  orderShow(orderMessage, "Zakázka je potvrzená k fakturaci.");
  await loadOrders();
  await openOrder(saved.id, false);
});

document.querySelector("#order-invoice").addEventListener("click", async () => {
  if (!orderDetail?.order || !window.confirm("Označit zakázku jako vyfakturovanou a uzamknout ji?")) return;
  const invoiceNumber = document.querySelector("#edit-invoice").value;
  const saved = await saveOrder(`/api/dispatch/orders/${encodeURIComponent(orderDetail.order.id)}/invoiced`, { invoiceNumber }, "POST");
  if (!saved) return;
  orderShow(orderMessage, "Zakázka je vyfakturovaná a uzamčená.");
  await loadOrders();
  await openOrder(saved.id, false);
});

document.querySelector("#order-resend").addEventListener("click", async () => {
  if (!orderDetail?.order || !window.confirm("Zařadit zakázku znovu do fronty?")) return;
  const saved = await saveOrder(`/api/dispatch/orders/${encodeURIComponent(orderDetail.order.id)}/resend`, {}, "POST");
  if (!saved) return;
  orderShow(orderMessage, "Zakázka je znovu ve frontě.");
  await loadOrders();
  await openOrder(saved.id, false);
});

document.querySelector("#order-close").addEventListener("click", () => {
  orderDetail = null;
  document.querySelector("#order-detail").hidden = true;
});

loadCatalog().then(() => {
  if (location.hash === "#zakazky") showView("orders");
  if (location.hash === "#fronta") showView("queue");
});
