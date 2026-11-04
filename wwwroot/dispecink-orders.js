const orderText = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
}[char]));
const orderMoney = new Intl.NumberFormat("cs-CZ", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const orderLiters = new Intl.NumberFormat("cs-CZ", { maximumFractionDigits: 3 });
const orderTime = new Intl.DateTimeFormat("cs-CZ", { dateStyle: "short", timeStyle: "short" });
const orderStatuses = ["Nová", "Naplánovaná", "Na cestě", "Doručeno", "K fakturaci", "Vyfakturováno"];
const orderMessage = document.querySelector("#order-message");
const queueMessage = document.querySelector("#queue-message");
let orderCatalog = { vehicles: [], drivers: [], trips: [], fuelTypes: [] };
const distanceCache = new Map();
const distanceTimers = {};
const distanceGeneration = {};
let applyingMatrixKm = false;
let orderList = [];
let orderDetail = null;
let orderSaving = false;
let queueItems = [];

function orderShow(target, value) {
  target.hidden = !value;
  target.textContent = value ?? "";
  const local = document.querySelector("#order-create-message");
  const dialog = document.querySelector("#order-create-dialog");
  if (local && dialog?.open && target === orderMessage) {
    local.hidden = !value;
    local.textContent = value ?? "";
  }
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
  const known = items.some((item) => item.id === selected);
  const extra = selected && !known
    ? `<option value="${orderText(selected)}" selected>${orderText(selected)}</option>`
    : "";
  const body = items.map((item) => {
    const selectedAttr = item.id === selected ? " selected" : "";
    return `<option value="${orderText(item.id)}"${selectedAttr}>${orderText(labelOf(item))}</option>`;
  }).join("");
  return head + extra + body;
}

function orderStatusOptions(selected) {
  if (selected === "Stornováno")
    return `<option selected>Stornováno</option>`;
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
  return { Create: "Založení", Update: "Aktualizace", Correction: "Oprava", Delete: "Smazání v Pohodě", Cancel: "Opravné storno" }[action] || action;
}

function versionSync(data, version) {
  return (data.sync ?? []).find((item) => item.versionTo === version.currentVersion) || null;
}

function versionStatus(data, version) {
  const sync = versionSync(data, version);
  return sync ? syncLabel(sync.status) : "neodesláno";
}

function versionWarning(data, version) {
  const text = (data.sync ?? []).find((item) => item.versionTo === version.currentVersion && item.warning)?.warning;
  return text ? `<div class="note warn">${orderText(text)}</div>` : "";
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
  const legacy = !!order && !order.customerPohodaId;
  return `
    <label class="wide">Zákazník z adresáře Pohody
      <input id="${prefix}-customer" required maxlength="160" autocomplete="off" placeholder="Název nebo IČO" value="${orderText(order?.customerName || "")}">
    </label>
    <p class="note warn wide" id="${prefix}-customer-warn"${legacy ? "" : " hidden"}>Zakázka nemá vazbu na adresář Pohody (AD). Vyberte zákazníka z našeptávače.</p>
    <p class="note wide" id="${prefix}-customer-snapshot">${orderText(customerSnapshot(order))}</p>
    <div class="picker customer-results wide" id="${prefix}-results"></div>
    <input type="hidden" id="${prefix}-customer-id" value="${orderText(order?.customerPohodaId ?? "")}">
    <input type="hidden" id="${prefix}-customer-ico" value="${orderText(order?.customerIco || "")}">
    <input type="hidden" id="${prefix}-customer-dic" value="${orderText(order?.customerDic || "")}">
    <input type="hidden" id="${prefix}-customer-street" value="${orderText(order?.customerStreet || "")}">
    <input type="hidden" id="${prefix}-customer-city" value="${orderText(order?.customerCity || "")}">
    <input type="hidden" id="${prefix}-customer-zip" value="${orderText(order?.customerZip || "")}">
    <input type="hidden" id="${prefix}-customer-email" value="${orderText(order?.customerEmail || "")}">
    <input type="hidden" id="${prefix}-customer-phone" value="${orderText(order?.customerPhone || "")}">
    <label>Nakládka <input id="${prefix}-loading" required maxlength="160" value="${orderText(order?.loadingPlace || "")}"></label>
    <label>Datum nakládky <input id="${prefix}-loading-date" type="date" required value="${orderText(order?.loadingDate || orderToday())}"></label>
    <label>Datum vykládky <input id="${prefix}-unloading-date" type="date" required value="${orderText(order?.unloadingDate || orderToday())}"></label>
    <label>Druh paliva <select id="${prefix}-fuel" required>${fuelOptions(order?.fuelTypeId, order?.fuelType, !order)}</select></label>
    <label>Naložené litry <input id="${prefix}-liters" type="number" min="0" step="0.001" required value="${orderText(order?.liters ?? "")}"></label>
    <label>Příplatky (Kč) <input id="${prefix}-surcharges" type="number" step="0.01" value="${orderText(order?.surcharges ?? 0)}"></label>
    <label>Vozidlo <select id="${prefix}-vehicle">${orderOptions(orderCatalog.vehicles, vehicle, "— nepřiřazeno —", (item) => `${item.plate} · ${item.label}`)}</select></label>
    <label>Řidič <select id="${prefix}-driver">${orderOptions(orderCatalog.drivers, driver, "— nepřiřazeno —", (item) => item.name)}</select></label>
    <label>Jízda <select id="${prefix}-trip">${orderOptions(orderCatalog.trips.filter((item) => item.status !== "Stornováno" || item.id === trip), trip, "— bez jízdy —", (item) => `${item.code} · ${item.customer}`)}</select></label>
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
      <div id="${prefix}-stops">${stops.map((stop, index) => stopRow(prefix, stop, legs[index], order?.fuelTypeId, order?.fuelType)).join("")}</div>
      <button type="button" data-add-stop="${prefix}">Přidat vykládku</button>
      <div id="${prefix}-totals" class="totals"></div>
      <p id="${prefix}-liter-warn" class="route-warn" hidden>Součet litrů na vykládkách je vyšší než naložené litry.</p>
    </div>
    ${prefix === "create" ? `<div class="wide"><button type="submit">Založit zakázku</button></div>` : ""}
  `;
}

function fuelOptions(selectedId, fallbackName, preferFirst) {
  const items = orderCatalog.fuelTypes || [];
  const fallback = String(fallbackName || "").trim();
  const byId = selectedId ? items.find((item) => item.id === selectedId) : null;
  const byName = !byId && fallback
    ? items.find((item) => item.name.localeCompare(fallback, "cs", { sensitivity: "accent" }) === 0)
    : null;
  const match = byId || byName || (preferFirst ? items[0] : null);
  const selected = match ? match.id : "";
  const placeholder = match
    ? `<option value="">— vyberte —</option>`
    : `<option value="" selected>${orderText(fallback || "— vyberte —")}</option>`;
  const body = items.map((item) =>
    `<option value="${item.id}"${item.id === selected ? " selected" : ""}>${orderText(item.name)}</option>`).join("");
  return placeholder + body;
}

function stopFuelId(stop, headerFuelId, headerFuelName) {
  if (stop?.fuelTypeId) return stop.fuelTypeId;
  const items = orderCatalog.fuelTypes || [];
  const named = headerFuelName
    ? items.find((item) => item.name.localeCompare(String(headerFuelName).trim(), "cs", { sensitivity: "accent" }) === 0)
    : null;
  return headerFuelId || named?.id || items[0]?.id || "";
}

function stopRow(prefix, stop, leg, headerFuelId, headerFuelName) {
  const arrival = stop?.arrivalAt ? toOrderLocal(stop.arrivalAt) : "";
  const headerDefault = stopFuelId(null, headerFuelId, headerFuelName);
  const fuelId = stop?.fuelTypeId || headerDefault;
  const linked = !stop?.fuelTypeId || String(stop.fuelTypeId) === String(headerDefault) ? "1" : "0";
  const source = leg?.kmSource === "Matrix" || leg?.kmSource === "Manual" ? leg.kmSource : "";
  return `<div class="stop-row" data-stop-row data-km-source="${orderText(source)}">
      <label>Místo <input data-stop-place maxlength="160" value="${orderText(stop?.place || "")}"></label>
      <label>Palivo <select data-stop-fuel data-fuel-linked="${linked}">${fuelOptions(fuelId, stop?.fuelType || headerFuelName, false)}</select></label>
      <label>Litry <input data-stop-liters type="number" min="0" step="0.001" value="${orderText(stop?.liters ?? "")}"></label>
      <label>Km <input data-leg-km type="number" min="0" step="0.001" value="${orderText(leg?.kilometers ?? "")}"><span class="km-source" data-km-label></span></label>
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

function normalizePlace(value) {
  return String(value || "").trim().replace(/\s+/g, " ").toLowerCase();
}

function canonicalKey(from, to) {
  const left = normalizePlace(from);
  const right = normalizePlace(to);
  if (!left || !right) return "";
  return left <= right ? `${left}\n${right}` : `${right}\n${left}`;
}

function legPairs(prefix) {
  const rows = [...document.querySelectorAll(`#${prefix}-stops [data-stop-row]`)];
  const loading = document.querySelector(`#${prefix}-loading`)?.value.trim() || "";
  return rows.map((row, index) => {
    const to = row.querySelector("[data-stop-place]")?.value.trim() || "";
    const from = index === 0 ? loading : (rows[index - 1].querySelector("[data-stop-place]")?.value.trim() || "");
    return { row, from, to, key: canonicalKey(from, to) };
  });
}

function paintKmSource(row) {
  const label = row.querySelector("[data-km-label]");
  if (!label) return;
  const source = row.dataset.kmSource || "";
  const km = row.querySelector("[data-leg-km]")?.value.trim() ?? "";
  const place = row.querySelector("[data-stop-place]")?.value.trim() ?? "";
  label.classList.toggle("manual", source === "Manual");
  if (source === "Matrix") label.textContent = "z matice";
  else if (source === "Manual") label.textContent = "ručně";
  else if (place && km === "") label.textContent = "není v matici";
  else label.textContent = "";
}

function applyLookedUpKm(row, km) {
  const input = row.querySelector("[data-leg-km]");
  if (!input) return;
  applyingMatrixKm = true;
  if (km === null || km === undefined) {
    input.value = "";
    row.dataset.kmSource = "";
  } else {
    input.value = String(km);
    row.dataset.kmSource = "Matrix";
  }
  applyingMatrixKm = false;
  paintKmSource(row);
}

function stampPairs(prefix) {
  for (const item of legPairs(prefix)) {
    if (item.key) item.row.dataset.kmPair = item.key;
  }
}

function scheduleDistanceFill(prefix) {
  clearTimeout(distanceTimers[prefix]);
  distanceTimers[prefix] = setTimeout(() => { fillDistances(prefix); }, 250);
}

async function fillDistances(prefix) {
  const generation = (distanceGeneration[prefix] || 0) + 1;
  distanceGeneration[prefix] = generation;
  const items = legPairs(prefix);
  const pending = [];
  for (const item of items) {
    if (!item.key) {
      paintKmSource(item.row);
      continue;
    }
    if (item.row.dataset.kmPair === item.key) continue;
    item.row.dataset.kmPair = item.key;
    if (distanceCache.has(item.key)) {
      applyLookedUpKm(item.row, distanceCache.get(item.key));
      continue;
    }
    pending.push(item);
  }
  if (pending.length) {
    const result = await orderFetch("/api/dispatch/distances/lookup", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pairs: pending.map((item) => ({ from: item.from, to: item.to })) })
    });
    if (distanceGeneration[prefix] !== generation) return;
    const hits = Array.isArray(result.data?.pairs) ? result.data.pairs : [];
    pending.forEach((item, index) => {
      const hit = hits[index];
      const km = result.ok && hit && hit.km !== null && hit.km !== undefined ? hit.km : null;
      distanceCache.set(item.key, km);
      if (item.row.dataset.kmPair === item.key) applyLookedUpKm(item.row, km);
    });
  }
  if (distanceGeneration[prefix] !== generation) return;
  refreshRoute(prefix);
}

async function labelKnownKm(prefix) {
  const items = legPairs(prefix).filter((item) => item.key && !item.row.dataset.kmSource);
  if (!items.length) return;
  const pending = items.filter((item) => !distanceCache.has(item.key));
  if (pending.length) {
    const result = await orderFetch("/api/dispatch/distances/lookup", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pairs: pending.map((item) => ({ from: item.from, to: item.to })) })
    });
    const hits = Array.isArray(result.data?.pairs) ? result.data.pairs : [];
    pending.forEach((item, index) => {
      const hit = hits[index];
      const km = result.ok && hit && hit.km !== null && hit.km !== undefined ? hit.km : null;
      distanceCache.set(item.key, km);
    });
  }
  for (const item of items) {
    if (item.row.dataset.kmSource) continue;
    const known = distanceCache.has(item.key) ? distanceCache.get(item.key) : undefined;
    const current = item.row.querySelector("[data-leg-km]").value.trim();
    if (current === "") {
      if (known !== null && known !== undefined) applyLookedUpKm(item.row, known);
      else paintKmSource(item.row);
      continue;
    }
    item.row.dataset.kmSource = known !== null && known !== undefined && Number(known) === Number(current) ? "Matrix" : "Manual";
    paintKmSource(item.row);
  }
  refreshRoute(prefix);
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
    const fuelValue = Number(row.querySelector("[data-stop-fuel]")?.value);
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
      fuelTypeId: Number.isInteger(fuelValue) && fuelValue > 0 ? fuelValue : null,
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
      cost,
      kmSource: row.dataset.kmSource || null
    });
    paintKmSource(row);
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

function customerSnapshot(order) {
  if (!order) return "";
  const parts = [order.customerIco ? `IČO ${order.customerIco}` : "", [order.customerStreet, order.customerCity, order.customerZip].filter(Boolean).join(", ")].filter(Boolean);
  return parts.join(" · ");
}

function readForm(prefix) {
  const value = (name) => document.querySelector(`#${prefix}-${name}`).value;
  const fuelSelect = document.querySelector(`#${prefix}-fuel`);
  const fuelTypeId = Number(fuelSelect?.value);
  const idText = value("customer-id").trim();
  const liters = Number(value("liters"));
  const surcharges = value("surcharges").trim() === "" ? 0 : Number(value("surcharges"));
  const route = refreshRoute(prefix);
  const agreed = roundMoney(route.legs.reduce((sum, leg) => sum + leg.salePrice, 0));
  const last = route.stops.length ? route.stops[route.stops.length - 1].place : "";
  return {
    customerPohodaId: idText === "" ? null : Number(idText),
    customerName: value("customer"),
    customerIco: value("customer-ico"),
    customerDic: value("customer-dic"),
    customerStreet: value("customer-street"),
    customerCity: value("customer-city"),
    customerZip: value("customer-zip"),
    customerEmail: value("customer-email"),
    customerPhone: value("customer-phone"),
    loadingPlace: value("loading"),
    unloadingPlace: last,
    loadingDate: value("loading-date"),
    unloadingDate: value("unloading-date"),
    fuelType: fuelSelect?.selectedOptions?.[0]?.textContent?.trim() || "",
    fuelTypeId: Number.isInteger(fuelTypeId) && fuelTypeId > 0 ? fuelTypeId : null,
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
      saleOverridden: leg.saleOverridden,
      kmSource: leg.kmSource
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
  document.querySelector("#ocount-stornovano").textContent = orderCount(data, "Stornováno");
  document.querySelector("#order-cards").hidden = false;
  document.querySelector("#order-rows").innerHTML = orderList.map((order) => `<tr>
      <td>${orderText(order.code)}<br><span class="route-to">v${orderText(order.currentVersion)}</span></td>
      <td>${orderText(order.customerName)}</td>
      <td>${orderText(order.loadingPlace)}<br><span class="route-to">→ ${orderText(order.unloadingPlace)}</span></td>
      <td>${orderDay(order.loadingDate)}</td>
      <td>${orderText(order.fuelType)}<br><span class="route-to">${orderLiters.format(Number(order.liters || 0))} l</span></td>
      <td class="num">${orderText(priceText(order))}</td>
      <td>${orderText(order.status)}${order.isLocked ? `<br><span class="pill">Zámek</span>` : ""}</td>
      <td>${orderText(order.pohodaOrderNumber || "—")}<br>${order.latestSyncStatus ? syncPill(order.latestSyncStatus) : `<span class="pill">neodesláno</span>`}${order.latestSyncWarning ? `<div class="note warn">${orderText(order.latestSyncWarning)}</div>` : ""}${order.latestSyncError ? `<div class="note">${orderText(order.latestSyncError)}</div>` : ""}</td>
      <td><button type="button" data-open="${orderText(order.id)}">Otevřít</button></td>
    </tr>`).join("");
  document.querySelector("#order-table-wrap").hidden = false;
  const empty = document.querySelector("#orders-empty");
  empty.hidden = orderList.length > 0;
  empty.textContent = orderStatusFilter && !orderStatusFilter.isAll()
    ? "V tomto stavu není žádná zakázka."
    : "Zatím není žádná zakázka.";
}

function renderDetail(data) {
  orderDetail = data;
  const order = data.order;
  document.querySelector("#order-detail").hidden = false;
  document.querySelector("#order-detail-title").textContent = `${order.code} · verze ${order.currentVersion}`;
  const syncWarning = (data.sync ?? []).find((item) => item.warning)?.warning || order.latestSyncWarning || "";
  const warningNote = document.querySelector("#order-sync-warning");
  warningNote.hidden = !syncWarning;
  warningNote.textContent = syncWarning;
  document.querySelector("#order-lock-note").hidden = !order.isLocked || order.status === "Stornováno";
  const cancelled = order.status === "Stornováno";
  const cancelNote = document.querySelector("#order-cancel-note");
  cancelNote.hidden = !cancelled;
  cancelNote.textContent = cancelled
    ? `Stornováno.${order.cancelReason ? ` Důvod: ${order.cancelReason}` : ""} Zakázka je jen ke čtení, z jízdy je odebraná a do součtů se nepočítá.`
    : "";
  document.querySelector("#order-confirm").disabled = !!order.isLocked || cancelled;
  for (const id of ["order-save", "order-confirm", "order-invoice", "order-resend", "order-cancel"])
    document.querySelector(`#${id}`).hidden = cancelled;
  document.querySelector("#order-edit").innerHTML = formFields("edit", order);
  if (cancelled) {
    document.querySelectorAll("#order-edit input, #order-edit select, #order-edit button").forEach((element) => {
      element.disabled = true;
    });
  }
  applyTripCrew("edit");
  stampPairs("edit");
  refreshRoute("edit");
  labelKnownKm("edit");
  document.querySelector("#version-page").hidden = true;
  showOrderChrome();
  renderVersionLinks(data);
  document.querySelector("#order-sync").innerHTML = (data.sync ?? []).length === 0
    ? `<p class="note">Do Pohody se zatím nic neposílalo. Poznámka, řidič a CMR se do fronty nedávají.</p>`
    : (data.sync ?? []).map((item) => `<article>
        <p><strong>${orderText(actionLabel(item.action))}</strong> verze ${orderText(item.versionFrom ?? "—")} → ${orderText(item.versionTo)}
        ${syncPill(item.status)} pokus ${orderText(item.attempts)}${item.pohodaDocNumber ? `, doklad ${orderText(item.pohodaDocNumber)}` : ""}</p>
        ${item.warning ? `<p class="note warn">${orderText(item.warning)}</p>` : ""}
        ${item.lastError ? `<p class="note">${orderText(item.lastError)}</p>` : ""}
        <pre class="xml">${orderText(item.payloadXml)}</pre>
      </article>`).join("");
}

function renderVersionLinks(data) {
  const current = data.order?.currentVersion;
  const previous = (data.versions ?? []).filter((version) => version.currentVersion !== current);
  const list = document.querySelector("#version-links");
  list.innerHTML = previous.map((version) => {
    const href = `#verze-${encodeURIComponent(data.order.id)}-${version.currentVersion}`;
    const label = `Verze ${version.currentVersion} – ${orderWhen(version.updatedAt)} – ${versionStatus(data, version)}`;
    return `<li><a href="${orderText(href)}" data-version="${orderText(version.currentVersion)}">${orderText(label)}</a></li>`;
  }).join("");
  document.querySelector("#version-empty").hidden = previous.length > 0;
}

function versionLines(version) {
  const legs = version.legs || [];
  const stops = version.stops || [];
  if (!legs.length && !stops.length) return "";
  const rows = (legs.length ? legs : stops.map((stop) => ({ sequence: stop.sequence, toPlace: stop.place, liters: stop.liters }))).map((leg, index) => {
    const stop = stops.find((item) => item.sequence === leg.sequence) || stops[index] || {};
    const source = leg.kmSource === "Matrix" ? " · z matice" : leg.kmSource === "Manual" ? " · ručně" : "";
    const km = leg.kilometers === null || leg.kilometers === undefined || leg.kilometers === "" ? "—" : `${orderLiters.format(Number(leg.kilometers))} km${source}`;
    const fuel = stop.fuelType || version.fuelType || "";
    return `<tr>
      <td>${orderText(leg.sequence || index + 1)}</td>
      <td>${orderText(leg.fromPlace || "")}</td>
      <td>${orderText(leg.toPlace || stop.place || "")}</td>
      <td>${orderText(fuel)}</td>
      <td class="num">${orderText(km)}</td>
      <td class="num">${orderLiters.format(Number(leg.liters ?? stop.liters ?? 0))} l</td>
      <td class="num">${orderMoney.format(Number(leg.salePrice || 0))} Kč${leg.saleOverridden ? " · ruční" : ""}</td>
      <td class="num">${orderMoney.format(Number(leg.costPrice || 0))} Kč</td>
      <td>${orderText(stop.note || "")}</td>
    </tr>`;
  }).join("");
  return `<table id="version-lines">
    <thead><tr><th>Úsek</th><th>Odkud</th><th>Kam</th><th>Palivo</th><th>Km</th><th>Litry</th><th>Prodej</th><th>Náklad</th><th>Poznámka</th></tr></thead>
    <tbody>${rows}</tbody>
  </table>`;
}

function versionFact(label, value) {
  return `<p><span>${orderText(label)}</span>${orderText(value || "—")}</p>`;
}

function versionPage(data, version) {
  const order = data.order;
  const quote = version.quote || {};
  const mode = version.pricingMode === "Liters" ? "Za litr" : "Za kilometr";
  const address = [version.customerStreet, version.customerCity, version.customerZip].filter(Boolean).join(", ");
  const sync = versionSync(data, version);
  const syncText = sync ? syncLabel(sync.status) : "neodesláno";
  const totals = [
    ["Km", orderLiters.format(Number(quote.totalKm || 0))],
    ["Naloženo l", orderLiters.format(Number(quote.loadedLiters ?? version.liters ?? 0))],
    ["Vyloženo l", orderLiters.format(Number(quote.deliveredLiters || 0))],
    ["Náklad Kč", orderMoney.format(Number(quote.totalCost || 0))],
    ["Prodej Kč", orderMoney.format(Number(quote.totalSale ?? version.agreedPrice ?? 0))],
    ["Příplatky Kč", orderMoney.format(Number(version.surcharges || 0))],
    ["Marže Kč", orderMoney.format(Number(quote.margin || 0))]
  ];
  return `
    <p><button type="button" class="ghost" data-version-back>Zpět na zakázku</button></p>
    <h2>${orderText(order.code)} · verze ${orderText(version.currentVersion)}</h2>
    <p class="note">Uložený snímek verze, jen ke čtení. ${orderText(orderWhen(version.updatedAt))}. ${orderText(syncText)}</p>
    ${versionWarning(data, version)}
    <div class="snapshot">
      ${versionFact("Zákazník", version.customerName)}
      ${versionFact("IČO", version.customerIco)}
      ${versionFact("DIČ", version.customerDic)}
      ${versionFact("Adresa", address)}
      ${versionFact("Nakládka", version.loadingPlace)}
      ${versionFact("Vykládka", version.unloadingPlace)}
      ${versionFact("Datum nakládky", orderDay(version.loadingDate))}
      ${versionFact("Datum vykládky", orderDay(version.unloadingDate))}
      ${versionFact("Palivo", version.fuelType)}
      ${versionFact("Naložené litry", orderLiters.format(Number(version.liters || 0)))}
      ${versionFact("Režim", mode)}
      ${versionFact("Prodej Kč/km", orderMoney.format(Number(version.salePerKm || 0)))}
      ${versionFact("Prodej Kč/l", orderMoney.format(Number(version.salePerLiter || 0)))}
      ${versionFact("Náklad Kč/km", orderMoney.format(Number(version.costPerKm || 0)))}
      ${versionFact("Příplatky", `${orderMoney.format(Number(version.surcharges || 0))} Kč`)}
      ${versionFact("Dohodnutá cena", `${orderMoney.format(Number(version.agreedPrice || 0))} Kč`)}
      ${versionFact("Stav", version.status)}
      ${versionFact("Číslo CMR", version.cmrNumber)}
      ${versionFact("Poznámka", version.note)}
    </div>
    <div class="totals">${totals.map(([label, value]) => `<article><span>${orderText(label)}</span><strong>${orderText(value)}</strong></article>`).join("")}</div>
    <h3>Úseky</h3>
    ${versionLines(version) || `<p class="note">Úseky v této verzi nejsou.</p>`}
  `;
}

function showOrderChrome() {
  for (const id of ["order-filter", "order-cards", "order-table-wrap", "order-sync-note"]) {
    const element = document.querySelector(`#${id}`);
    if (element) element.hidden = false;
  }
}

function hideOrderChrome() {
  for (const id of ["order-filter", "order-cards", "order-table-wrap", "order-detail", "order-sync-note"]) {
    const element = document.querySelector(`#${id}`);
    if (element) element.hidden = true;
  }
}

function showVersion(versionNumber) {
  const data = orderDetail;
  const version = (data?.versions ?? []).find((item) => item.currentVersion === Number(versionNumber));
  const page = document.querySelector("#version-page");
  if (!data?.order || !version || !page) {
    orderShow(orderMessage, "Tuto verzi se nepodařilo otevřít.");
    return;
  }
  hideOrderChrome();
  page.innerHTML = versionPage(data, version);
  page.hidden = false;
  history.replaceState(null, "", window.keepQuery(`#verze-${encodeURIComponent(data.order.id)}-${version.currentVersion}`));
  page.scrollIntoView({ behavior: "smooth", block: "start" });
}

function closeVersion() {
  const page = document.querySelector("#version-page");
  if (page) {
    page.hidden = true;
    page.innerHTML = "";
  }
  showOrderChrome();
  const detail = document.querySelector("#order-detail");
  if (detail && orderDetail?.order) detail.hidden = false;
  if (location.hash.startsWith("#verze-")) history.replaceState(null, "", window.keepQuery("#zakazky"));
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
      <td>${item.warning ? `<div class="note warn">${orderText(item.warning)}</div>` : ""}${orderText(item.lastError || "")}</td>
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
  if (name === "trips") history.replaceState(null, "", window.keepQuery(""));
  if (name === "orders") history.replaceState(null, "", window.keepQuery("#zakazky"));
  if (name === "queue") history.replaceState(null, "", window.keepQuery("#fronta"));
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

async function fetchCatalog() {
  const [vehicles, drivers, trips, fuels] = await Promise.all([
    orderFetch("/api/dispatch/vehicles"),
    orderFetch("/api/dispatch/drivers"),
    orderFetch("/api/dispatch/trips"),
    orderFetch("/api/dispatch/fuel-types")
  ]);
  if (Array.isArray(vehicles.data)) orderCatalog.vehicles = vehicles.data;
  if (Array.isArray(drivers.data)) orderCatalog.drivers = drivers.data;
  if (Array.isArray(trips.data)) orderCatalog.trips = trips.data;
  if (Array.isArray(fuels.data)) orderCatalog.fuelTypes = fuels.data;
}

async function loadCatalog() {
  await fetchCatalog();
  document.querySelector("#order-create").innerHTML = formFields("create");
  applyTripCrew("create");
  stampPairs("create");
  refreshRoute("create");
  labelKnownKm("create");
}

async function loadOrders() {
  orderShow(orderMessage, "Načítám zakázky…");
  const params = new URLSearchParams();
  for (const status of orderStatusFilter?.values() ?? []) params.append("status", status);
  const query = params.toString() ? `?${params}` : "";
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
  const tripId = result.data?.order?.tripId || "";
  if (tripId && !orderCatalog.trips.some((item) => item.id === tripId))
    await fetchCatalog();
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

const customerSearchTimers = {};

function scheduleCustomerSearch(prefix) {
  clearTimeout(customerSearchTimers[prefix]);
  customerSearchTimers[prefix] = setTimeout(() => searchCustomers(prefix), 250);
}

async function searchCustomers(prefix) {
  const name = document.querySelector(`#${prefix}-customer`).value.trim();
  const results = document.querySelector(`#${prefix}-results`);
  if (!results) return;
  if (name.length < 2) {
    results.innerHTML = "";
    return;
  }
  const result = await orderFetch(`/api/dispatch/customers?q=${encodeURIComponent(name)}`);
  if (!result.ok) {
    orderShow(orderMessage, result.data.message || "Adresář Pohody se nepodařilo načíst.");
    results.innerHTML = "";
    return;
  }
  const items = Array.isArray(result.data) ? result.data : [];
  orderShow(orderMessage, items.length ? "" : "V adresáři Pohody nic není.");
  results.innerHTML = items.map((item) => {
    const detail = [item.ico ? `IČO ${item.ico}` : "", item.street, item.city, item.zip].filter(Boolean).join(", ");
    return `<button type="button" data-pick="${prefix}" data-id="${orderText(item.id)}" data-name="${orderText(item.name)}" data-ico="${orderText(item.ico || "")}" data-dic="${orderText(item.dic || "")}" data-street="${orderText(item.street || "")}" data-city="${orderText(item.city || "")}" data-zip="${orderText(item.zip || "")}" data-email="${orderText(item.email || "")}" data-phone="${orderText(item.phone || "")}">${orderText(item.name)}${detail ? ` · ${orderText(detail)}` : ""}</button>`;
  }).join("");
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
document.querySelector("#queue-reload").addEventListener("click", () => loadQueue());

document.querySelector("#orders-view").addEventListener("click", (event) => {
  const versionLink = event.target.closest("[data-version]");
  if (versionLink) {
    event.preventDefault();
    showVersion(versionLink.dataset.version);
    return;
  }
  if (event.target.closest("[data-version-back]")) {
    closeVersion();
    return;
  }
  const open = event.target.closest("[data-open]");
  if (open) {
    openOrder(open.dataset.open, true);
    return;
  }
  const pick = event.target.closest("[data-pick]");
  if (pick) {
    const prefix = pick.dataset.pick;
    const set = (name, value) => {
      const field = document.querySelector(`#${prefix}-${name}`);
      if (field) field.value = value || "";
    };
    set("customer", pick.dataset.name);
    set("customer-id", pick.dataset.id);
    set("customer-ico", pick.dataset.ico);
    set("customer-dic", pick.dataset.dic);
    set("customer-street", pick.dataset.street);
    set("customer-city", pick.dataset.city);
    set("customer-zip", pick.dataset.zip);
    set("customer-email", pick.dataset.email);
    set("customer-phone", pick.dataset.phone);
    const snapshot = document.querySelector(`#${prefix}-customer-snapshot`);
    if (snapshot) snapshot.textContent = [pick.dataset.ico ? `IČO ${pick.dataset.ico}` : "", [pick.dataset.street, pick.dataset.city, pick.dataset.zip].filter(Boolean).join(", ")].filter(Boolean).join(" · ");
    const warn = document.querySelector(`#${prefix}-customer-warn`);
    if (warn) warn.hidden = true;
    document.querySelector(`#${prefix}-results`).innerHTML = "";
    orderShow(orderMessage, "");
    return;
  }
  const openTripButton = event.target.closest("[data-open-trip]");
  if (openTripButton) {
    const prefix = openTripButton.dataset.openTrip;
    const selected = document.querySelector(`#${prefix}-trip`)?.value || "";
    const assigned = prefix === "edit" ? (orderDetail?.order?.tripId || "") : "";
    const tripId = selected || assigned;
    if (tripId && typeof window.openTrip === "function") {
      orderShow(orderMessage, "");
      window.openTrip(tripId);
    } else {
      orderShow(orderMessage, "Nejdřív vyberte jízdu.");
    }
    return;
  }
  const route = event.target.closest("[data-route]");
  if (!route) return;
  const prefix = route.dataset.route;
  const row = event.target.closest("[data-stop-row]");
  if (event.target.closest("[data-add-stop]")) {
    const headerFuel = document.querySelector(`#${prefix}-fuel`);
    document.querySelector(`#${prefix}-stops`).insertAdjacentHTML("beforeend", stopRow(prefix, {}, null, headerFuel?.value, headerFuel?.selectedOptions?.[0]?.textContent));
    refreshRoute(prefix);
    scheduleDistanceFill(prefix);
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
    scheduleDistanceFill(prefix);
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
  if (event.target.closest("[data-stop-remove], [data-stop-up], [data-stop-down]"))
    scheduleDistanceFill(prefix);
});

document.querySelector("#orders-view").addEventListener("input", (event) => {
  if (event.target.matches("[id$='-customer']") && !event.target.id.endsWith("-customer-id")) {
    const prefix = event.target.id.startsWith("edit-") ? "edit" : "create";
    const idField = document.querySelector(`#${prefix}-customer-id`);
    if (idField && event.target.id === `${prefix}-customer`) {
      idField.value = "";
      scheduleCustomerSearch(prefix);
    }
  }
  if (event.target.matches("[id$='-cost-km']")) event.target.dataset.touched = "1";
  if (event.target.matches("[data-leg-km]") && !applyingMatrixKm) {
    const kmRow = event.target.closest("[data-stop-row]");
    if (kmRow) {
      kmRow.dataset.kmSource = event.target.value.trim() === "" ? "" : "Manual";
      paintKmSource(kmRow);
    }
  }
  const route = event.target.closest("[data-route]") || event.target.closest("form");
  const prefix = event.target.id?.startsWith("edit-") ? "edit" : event.target.id?.startsWith("create-") ? "create" : route?.dataset?.route;
  if (prefix) refreshRoute(prefix);
  if (prefix && event.target.matches("[data-stop-place], [id$='-loading']")) scheduleDistanceFill(prefix);
});

document.querySelector("#orders-view").addEventListener("change", (event) => {
  if (event.target.matches("[id$='-trip']")) {
    const prefix = event.target.id.startsWith("edit-") ? "edit" : "create";
    applyTripCrew(prefix);
  }
  if (event.target.matches("[id$='-fuel']") && !event.target.matches("[data-stop-fuel]")) {
    const prefix = event.target.id.startsWith("edit-") ? "edit" : "create";
    document.querySelectorAll(`#${prefix}-stops [data-stop-fuel][data-fuel-linked="1"]`).forEach((select) => {
      select.value = event.target.value;
    });
  }
  if (event.target.matches("[data-stop-fuel]")) {
    const prefix = event.target.closest("[data-route]")?.dataset.route;
    const header = prefix ? document.querySelector(`#${prefix}-fuel`) : null;
    event.target.dataset.fuelLinked = header && event.target.value === header.value ? "1" : "0";
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
  const body = readForm("create");
  if (!body.customerPohodaId) {
    orderShow(orderMessage, "Zákazníka vyberte z adresáře Pohody.");
    return;
  }
  const created = await saveOrder("/api/dispatch/orders", body, "POST");
  if (!created) return;
  distanceCache.clear();
  document.querySelector("#order-create").innerHTML = formFields("create");
  stampPairs("create");
  refreshRoute("create");
  document.querySelector("#order-create-dialog")?.close();
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

document.querySelector("#order-cancel").addEventListener("click", async () => {
  const order = orderDetail?.order;
  if (!order || order.status === "Stornováno") return;
  const reason = window.prompt("Důvod storna (nepovinný). Zrušení dialogu nic neudělá.", "");
  if (reason === null) return;
  const sent = !!(order.pohodaOrderId || order.lastSentVersion);
  let remove = false;
  if (!sent) {
    remove = window.confirm("Zakázka ještě nebyla v Pohodě.\n\nOK = smazat natrvalo\nZrušit = jen stornovat a nechat v historii");
  } else if (order.isLocked) {
    if (!window.confirm(`Stornovat ${order.code}? Původní objednávka v Pohodě se nemění, protože je uzamčená. Vznikne opravný doklad ${order.code}/N jen se storno položkami.`)) return;
  } else if (!window.confirm(`Stornovat ${order.code}? V Pohodě se přijatá objednávka smaže. Když už tam není, bere se to jako hotovo. Zakázka zůstane v historii.`)) return;
  const saved = await saveOrder(`/api/dispatch/orders/${encodeURIComponent(order.id)}/cancel`, { reason, delete: remove }, "POST");
  if (!saved) return;
  if (saved.deleted) {
    orderDetail = null;
    document.querySelector("#order-detail").hidden = true;
    orderShow(orderMessage, "Zakázka je smazaná.");
    await loadOrders();
    return;
  }
  orderShow(orderMessage, "Zakázka je stornovaná.");
  await loadOrders();
  await openOrder(saved.id, false);
});

document.querySelector("#order-close").addEventListener("click", () => {
  orderDetail = null;
  document.querySelector("#order-detail").hidden = true;
  closeVersion();
});

const orderStatusFilter = window.bindStatusFilter(document.querySelector("#order-status-filter"), {
  param: "zakazky",
  known: ["Nová", "Naplánovaná", "Na cestě", "Doručeno", "K fakturaci", "Vyfakturováno", "Stornováno"],
  excludeByDefault: ["Stornováno"],
  onChange() { loadOrders(); }
});

window.showOrder = async (id) => {
  document.querySelector("#tab-orders")?.click();
  await openOrder(id, true);
};

document.querySelector("#open-order-create")?.addEventListener("click", () => {
  const local = document.querySelector("#order-create-message");
  if (local) local.hidden = true;
  document.querySelector("#order-create-dialog")?.showModal();
});

loadCatalog().then(() => {
  if (location.hash === "#zakazky") showView("orders");
  if (location.hash === "#fronta") showView("queue");
});
