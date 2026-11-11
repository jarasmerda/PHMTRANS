const orderText = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
}[char]));
const orderMoney = new Intl.NumberFormat("cs-CZ", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const orderLiters = new Intl.NumberFormat("cs-CZ", { maximumFractionDigits: 3 });
const orderTime = new Intl.DateTimeFormat("cs-CZ", { dateStyle: "short", timeStyle: "short" });
const orderStatuses = ["Nová", "Naplánovaná", "Na cestě", "Doručeno", "K fakturaci", "Vyfakturováno"];
const orderMessage = document.querySelector("#order-message");
const queueMessage = document.querySelector("#queue-message");
let orderCatalog = { vehicles: [], drivers: [], fuelTypes: [], loadingPlaces: [], points: [], defaultCostPerKm: 0 };
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

// Dostupnost řidičů podle data nakládky a vykládky: výběr Řidič a kalendář ve formuláři.
const availabilityTimers = {};
const availabilityGeneration = {};
const availabilityBusy = {};
const availabilityData = {};
const availabilityBefore = 3;
const availabilityAfter = 7;
const availabilityDay = new Intl.DateTimeFormat("cs-CZ", { weekday: "short", day: "numeric", month: "numeric", timeZone: "UTC" });

function isoShift(iso, days) {
  const date = new Date(`${iso}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() + days);
  return date.toISOString().slice(0, 10);
}

function availabilityRange(prefix) {
  const from = document.querySelector(`#${prefix}-loading-date`)?.value || "";
  let to = document.querySelector(`#${prefix}-unloading-date`)?.value || from;
  if (!/^\d{4}-\d{2}-\d{2}$/.test(from)) return null;
  if (!/^\d{4}-\d{2}-\d{2}$/.test(to) || to < from) to = from;
  const days = Math.min(Math.round((Date.parse(to) - Date.parse(from)) / 86400000) + 1, 62 - availabilityBefore - availabilityAfter);
  return { from, to: isoShift(from, days - 1), days };
}

function renderAvailabilityPanel(prefix) {
  const panel = document.querySelector(`#${prefix}-availability`);
  if (!panel || panel.hidden) return;
  const info = availabilityData[prefix];
  if (!info) {
    panel.innerHTML = `<p class="note">Kalendář se ukáže po zadání data nakládky.</p>`;
    return;
  }
  const { range, data, ownId } = info;
  const dates = data.dates ?? [];
  const inRange = (date) => date >= range.from && date <= range.to;
  const selected = document.querySelector(`#${prefix}-driver`)?.value || "";
  const head = `<tr><th>Řidič</th>${dates.map((date) =>
    `<th class="${inRange(date) ? "pick" : ""}">${orderText(availabilityDay.format(new Date(`${date}T00:00:00Z`)))}</th>`).join("")}</tr>`;
  const rows = (data.drivers ?? []).filter((row) => row.driverId).map((row) => {
    const cells = (row.days ?? []).map((day) => {
      const orders = day.orders ?? [];
      const others = orders.filter((order) => order.id !== ownId);
      const pick = inRange(day.date);
      const state = others.length ? (pick ? "over" : "busy") : (pick ? "free" : "");
      const body = orders.map((order) =>
        `<span title="${orderText(order.customer)}">${orderText(order.id === ownId ? "tato" : order.code)}</span>`).join("");
      return `<td class="${pick ? "pick " : ""}${state}">${body}</td>`;
    }).join("");
    const busy = availabilityBusy[prefix]?.has(row.driverId);
    return `<tr${row.driverId === selected ? ` class="chosen"` : ""}>
      <td><button type="button" class="linkish" data-pick-driver="${orderText(row.driverId)}" data-prefix="${prefix}">${orderText(row.name)}</button>
      <small class="note">${busy ? "obsazený" : "volný"}</small></td>${cells}</tr>`;
  }).join("") || `<tr><td>V dispečinku zatím nejsou řidiči.</td></tr>`;
  panel.innerHTML = `<p class="note">Orámované sloupce jsou dny této zakázky: zeleně volno, červeně jiná zakázka. Klepnutím na jméno řidiče ho vyberete.</p>
    <div class="avail-scroll"><table class="avail"><thead>${head}</thead><tbody>${rows}</tbody></table></div>`;
}

function scheduleAvailability(prefix) {
  clearTimeout(availabilityTimers[prefix]);
  availabilityTimers[prefix] = setTimeout(() => refreshAvailability(prefix), 250);
}

async function refreshAvailability(prefix) {
  if (!document.querySelector(`#${prefix}-driver`)) return;
  const generation = (availabilityGeneration[prefix] || 0) + 1;
  availabilityGeneration[prefix] = generation;
  const range = availabilityRange(prefix);
  const span = range ? range.days + availabilityBefore + availabilityAfter : 0;
  const [result, events] = range
    ? await Promise.all([
      orderFetch(`/api/dispatch/calendar?from=${isoShift(range.from, -availabilityBefore)}&days=${span}`),
      orderFetch(`/api/dispatch/calendar/events?from=${isoShift(range.from, -1)}&days=${range.days + 2}`)
    ])
    : [{ ok: false }, { ok: false }];
  if (availabilityGeneration[prefix] !== generation) return;
  const ownId = prefix === "edit" ? orderDetail?.order?.id : null;
  const busy = new Map();
  if (events.ok && Array.isArray(events.data?.events)) {
    // Obsazenost po hodinách: zakázka řidiče se překrývá s časem od–do této zakázky.
    const time = (id, fallback) => document.querySelector(`#${prefix}-${id}`)?.value || fallback;
    const start = `${range.from}T${time("loading-time", "06:00")}`;
    const end = `${document.querySelector(`#${prefix}-unloading-date`)?.value || range.from}T${time("unloading-time", "14:00")}`;
    for (const event of events.data.events) {
      if (!event.driverId || event.id === ownId) continue;
      if (String(event.start).slice(0, 16) >= end || String(event.end).slice(0, 16) <= start) continue;
      busy.set(event.driverId, [...new Set([...(busy.get(event.driverId) ?? []), event.code])]);
    }
  } else {
    for (const row of (result.ok ? result.data?.drivers : null) ?? []) {
      if (!row.driverId) continue;
      const codes = new Set();
      for (const day of row.days ?? []) {
        if (day.date < range.from || day.date > range.to) continue;
        for (const order of day.orders ?? [])
          if (order.id !== ownId) codes.add(order.code);
      }
      if (codes.size) busy.set(row.driverId, [...codes]);
    }
  }
  availabilityBusy[prefix] = result.ok ? busy : null;
  availabilityData[prefix] = result.ok ? { range, data: result.data, ownId } : null;
  applyAvailability(prefix);
}

function applyAvailability(prefix) {
  const select = document.querySelector(`#${prefix}-driver`);
  const note = document.querySelector(`#${prefix}-driver-note`);
  if (!select || !note) return;
  const busy = availabilityBusy[prefix];
  const selected = select.value;
  if (!busy) {
    select.innerHTML = orderOptions(orderCatalog.drivers, selected, "— nepřiřazeno —", (item) => item.name);
    note.hidden = true;
    renderAvailabilityPanel(prefix);
    return;
  }
  const free = orderCatalog.drivers.filter((item) => !busy.has(item.id));
  const taken = orderCatalog.drivers.filter((item) => busy.has(item.id));
  select.innerHTML = orderOptions([...free, ...taken], selected, "— nepřiřazeno —", (item) =>
    busy.has(item.id) ? `${item.name} – obsazený (${busy.get(item.id).join(", ")})` : `${item.name} – volný`);
  const clash = busy.get(select.value);
  note.hidden = false;
  note.classList.toggle("warn", !!clash);
  note.textContent = clash
    ? `Pozor, řidič má v tu dobu i zakázku ${clash.join(", ")}.`
    : `Volní řidiči v tu dobu: ${free.length} z ${orderCatalog.drivers.length}.`;
  renderAvailabilityPanel(prefix);
}

document.addEventListener("click", (event) => {
  const toggle = event.target.closest("[data-toggle-availability]");
  if (toggle) {
    const prefix = toggle.dataset.toggleAvailability;
    const panel = document.querySelector(`#${prefix}-availability`);
    if (!panel) return;
    panel.hidden = !panel.hidden;
    toggle.textContent = panel.hidden ? "Kalendář řidičů" : "Skrýt kalendář";
    renderAvailabilityPanel(prefix);
    return;
  }
  const pick = event.target.closest("[data-pick-driver]");
  if (pick) {
    const select = document.querySelector(`#${pick.dataset.prefix}-driver`);
    if (!select || select.disabled) return;
    select.value = pick.dataset.pickDriver;
    select.dispatchEvent(new Event("change", { bubbles: true }));
  }
});

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
  const status = order?.status || "Nová";
  const mode = order?.pricingMode === "Liters" ? "Liters" : "Km";
  const stops = order?.stops?.length
    ? order.stops
    : [{ place: order?.unloadingPlace || "", liters: order?.liters ?? "", note: "", arrivalAt: "" }];
  const legs = order?.legs || [];
  const legacy = !!order && !order.customerPohodaId;
  return `
    <fieldset class="wide form-card card-customer">
      <legend>Zákazník</legend>
      <div class="card-grid">
        <label class="wide">Zákazník z adresáře Pohody
          <input id="${prefix}-customer" required maxlength="160" autocomplete="off" placeholder="Název nebo IČO" value="${orderText(order?.customerName || "")}">
        </label>
        <p class="note warn wide" id="${prefix}-customer-warn"${legacy ? "" : " hidden"}>Zakázka nemá vazbu na adresář Pohody (AD). Vyberte zákazníka z našeptávače.</p>
        <p class="note wide" id="${prefix}-customer-snapshot">${orderText(customerSnapshot(order))}</p>
        <div class="picker customer-results wide" id="${prefix}-results"></div>
      </div>
      <input type="hidden" id="${prefix}-customer-id" value="${orderText(order?.customerPohodaId ?? "")}">
      <input type="hidden" id="${prefix}-customer-ico" value="${orderText(order?.customerIco || "")}">
      <input type="hidden" id="${prefix}-customer-dic" value="${orderText(order?.customerDic || "")}">
      <input type="hidden" id="${prefix}-customer-street" value="${orderText(order?.customerStreet || "")}">
      <input type="hidden" id="${prefix}-customer-city" value="${orderText(order?.customerCity || "")}">
      <input type="hidden" id="${prefix}-customer-zip" value="${orderText(order?.customerZip || "")}">
      <input type="hidden" id="${prefix}-customer-email" value="${orderText(order?.customerEmail || "")}">
      <input type="hidden" id="${prefix}-customer-phone" value="${orderText(order?.customerPhone || "")}">
    </fieldset>
    <fieldset class="wide form-card card-schedule">
      <legend>Termín a posádka</legend>
      <div class="card-grid">
        ${loadingField(prefix, order?.loadingPlace || "")}
        <label>Nakládka (datum a čas)
          <span class="date-time">
            <input id="${prefix}-loading-date" type="date" required value="${orderText(order?.loadingDate || orderToday())}">
            <input id="${prefix}-loading-time" type="time" step="900" aria-label="Čas nakládky" value="${orderText(order ? order.loadingTime || "" : "06:00")}">
          </span></label>
        <label>Vykládka (datum a čas)
          <span class="date-time">
            <input id="${prefix}-unloading-date" type="date" required value="${orderText(order?.unloadingDate || orderToday())}">
            <input id="${prefix}-unloading-time" type="time" step="900" aria-label="Čas vykládky" value="${orderText(order ? order.unloadingTime || "" : "14:00")}">
          </span></label>
        <label>Vozidlo <select id="${prefix}-vehicle">${orderOptions(orderCatalog.vehicles, vehicle, "— nepřiřazeno —", (item) => `${item.plate} · ${item.label}`)}</select></label>
        <label>Řidič <select id="${prefix}-driver">${orderOptions(orderCatalog.drivers, driver, "— nepřiřazeno —", (item) => item.name)}</select></label>
      </div>
      <div class="availability">
        <button type="button" data-toggle-booking="${prefix}">Naplánovat čas řidiče</button>
        <button type="button" class="ghost" data-toggle-availability="${prefix}">Kalendář řidičů</button>
        <button type="button" class="ghost" data-open-week="${prefix}">Vytíženost po hodinách ↗</button>
        <span class="note" id="${prefix}-driver-note" hidden></span>
        <div id="${prefix}-booking" class="avail-panel" hidden></div>
        <div id="${prefix}-availability" class="avail-panel" hidden></div>
      </div>
    </fieldset>
    <fieldset class="wide form-card card-price">
      <legend>Cena a náklady</legend>
      <div class="card-grid">
        <label>Režim prodeje
          <select id="${prefix}-mode">
            <option value="Km"${mode === "Km" ? " selected" : ""}>Za kilometr</option>
            <option value="Liters"${mode === "Liters" ? " selected" : ""}>Za litr</option>
          </select>
        </label>
        <label>Prodej Kč/km <input id="${prefix}-sale-km" type="number" min="0" step="0.0001" value="${orderText(order?.salePerKm ?? 0)}"></label>
        <label>Prodej Kč/l <input id="${prefix}-sale-liter" type="number" min="0" step="0.0001" value="${orderText(order?.salePerLiter ?? 0)}"></label>
        <label>Náklad Kč/km <input id="${prefix}-cost-km" type="number" min="0" step="0.0001" value="${orderText(order?.costPerKm ?? "")}">
          <span class="note" id="${prefix}-cost-note"></span></label>
        <label>Příplatky (Kč) <input id="${prefix}-surcharges" type="number" step="0.01" value="${orderText(order?.surcharges ?? 0)}"></label>
      </div>
    </fieldset>
    <fieldset class="wide form-card card-state">
      <legend>Stav a evidence</legend>
      <div class="card-grid">
        <label>Stav <select id="${prefix}-status">${orderStatusOptions(status)}</select></label>
        <label>Číslo CMR <input id="${prefix}-cmr" maxlength="40" value="${orderText(order?.cmrNumber || "")}"></label>
        <label>Číslo faktury <input id="${prefix}-invoice" maxlength="40" value="${orderText(order?.pohodaInvoiceNumber || "")}"></label>
        <label class="wide">Poznámka <input id="${prefix}-note" maxlength="500" value="${orderText(order?.note || "")}"></label>
        ${prefix === "edit" ? `<label class="wide docs-reason-field">Důvod chybějících dokladů
          <textarea id="${prefix}-docs-reason" rows="2" maxlength="500" placeholder="Vyplní se při potvrzení k fakturaci bez všech dokladů. Můžete ho tu upravit nebo smazat.">${orderText(order?.missingDocumentsReason || "")}</textarea>
        </label>` : ""}
      </div>
    </fieldset>
    <div class="wide loads" data-loads="${prefix}">
      <h3>Nakládka po palivech</h3>
      <div id="${prefix}-loads">${orderLoadsOf(order).map((load) => loadRow(load, !order)).join("")}</div>
      <button type="button" data-add-load>Přidat palivo</button>
      <span class="note" id="${prefix}-loads-total"></span>
    </div>
    <div class="wide" id="${prefix}-route" data-route="${prefix}">
      <h3>Vykládky a úseky</h3>
      <div id="${prefix}-stops">${stops.map((stop, index) => stopRow(prefix, stop, legs[index], orderLoadsOf(order)[0]?.fuelTypeId, orderLoadsOf(order)[0]?.fuelType)).join("")}</div>
      <button type="button" data-add-stop="${prefix}">Přidat vykládku</button>
      <div id="${prefix}-totals" class="totals"></div>
      <p id="${prefix}-liter-warn" class="route-warn" hidden></p>
    </div>
    ${prefix === "create" ? `<div class="wide create-actions"><button type="submit">Založit zakázku</button>
      <p class="note">Doklady (fotky, PDF) přiložíte hned po založení: v detailu zakázky tlačítkem Otevřít doklady.</p></div>` : ""}
  `;
}

function orderLoadsOf(order) {
  if (order?.loads?.length) return order.loads;
  return [{ fuelTypeId: order?.fuelTypeId || null, fuelType: order?.fuelType || "", liters: order?.liters ?? "" }];
}

function loadRow(load, preferFirst) {
  return `<div class="load-row" data-load-row>
      <label>Palivo <select data-load-fuel required>${fuelOptions(load?.fuelTypeId, load?.fuelType, preferFirst)}</select></label>
      <label>Naložené litry <input data-load-liters type="number" min="0" step="0.001" required value="${orderText(load?.liters ?? "")}"></label>
      <button type="button" class="ghost" data-load-remove>Odebrat</button>
      <div class="load-comp" data-load-comp data-selected="${orderText((load?.compartments || []).join(","))}"></div>
    </div>`;
}

function readLoads(prefix) {
  return [...document.querySelectorAll(`#${prefix}-loads [data-load-row]`)].map((row) => {
    const select = row.querySelector("[data-load-fuel]");
    const id = Number(select?.value);
    const litersText = row.querySelector("[data-load-liters]")?.value.trim() ?? "";
    return {
      fuelTypeId: Number.isInteger(id) && id > 0 ? id : null,
      fuelType: select?.selectedOptions?.[0]?.textContent?.trim() || "",
      liters: litersText === "" ? null : Number(litersText),
      compartments: rowCompartments(row)
    };
  });
}

function firstLoadFuel(prefix) {
  const first = readLoads(prefix)[0];
  return { id: first?.fuelTypeId || "", name: first?.fuelType || "" };
}

// Přepážky vozidla u řádku nakládky. Jedna přepážka může mít jen jedno palivo.
function vehicleCompartmentsOf(prefix) {
  const id = document.querySelector(`#${prefix}-vehicle`)?.value;
  return [...((orderCatalog.vehicles || []).find((item) => item.id === id)?.compartments ?? [])]
    .sort((a, b) => a.number - b.number);
}

function rowCompartments(row) {
  return (row.querySelector("[data-load-comp]")?.dataset.selected || "")
    .split(",").map(Number).filter((number) => Number.isInteger(number) && number > 0);
}

function renderCompartments(prefix) {
  const rows = [...document.querySelectorAll(`#${prefix}-loads [data-load-row]`)];
  const vehicleSelect = document.querySelector(`#${prefix}-vehicle`);
  const list = vehicleCompartmentsOf(prefix);
  const locked = !!vehicleSelect?.disabled;
  const owner = new Map();
  rows.forEach((row, index) => rowCompartments(row).forEach((number) => owner.set(number, index)));
  rows.forEach((row, index) => {
    const box = row.querySelector("[data-load-comp]");
    if (!box) return;
    let selected = rowCompartments(row);
    if (!list.length) {
      const hint = vehicleSelect?.value ? "Vozidlo nemá zadané přepážky (doplní se v SQL, tabulka Disp_VehicleCompartment)." : "Nejdřív vyberte vozidlo.";
      box.innerHTML = `<span class="comp-label">Přepážky</span><span class="note">${orderText(hint)}</span>`
        + (selected.length ? ` <span class="note warn">Vybrané přepážky ${orderText(selected.join(", "))} bez vozidla neuložíte.</span>` : "");
      return;
    }
    // Po změně vozidla zůstanou jen přepážky, které nové vozidlo má.
    const kept = selected.filter((number) => list.some((item) => item.number === number));
    if (kept.length !== selected.length) {
      selected = kept;
      box.dataset.selected = kept.join(",");
    }
    const liters = Number(row.querySelector("[data-load-liters]")?.value || 0);
    const capacity = list.filter((item) => selected.includes(item.number))
      .reduce((sum, item) => sum + Number(item.capacityLiters || 0), 0);
    const chips = list.map((item) => {
      const mine = selected.includes(item.number);
      const taken = !mine && owner.has(item.number) && owner.get(item.number) !== index;
      return `<label class="comp-chip${mine ? " on" : ""}${taken ? " taken" : ""}" title="${orderText(`${orderLiters.format(Number(item.capacityLiters || 0))} l${taken ? " – už má jiné palivo" : ""}`)}">
        <input type="checkbox" data-comp-number="${item.number}"${mine ? " checked" : ""}${taken || locked ? " disabled" : ""}>
        <b>${item.number}</b> <small>${orderText(orderLiters.format(Number(item.capacityLiters || 0)))} l</small></label>`;
    }).join("");
    let status = "";
    if (!selected.length && owner.size && liters > 0) {
      const free = list.filter((item) => !owner.has(item.number)).length;
      status = free
        ? `<span class="note warn">Vyberte přepážku. Ostatní paliva přepážky mají, toto taky musí.</span>`
        : `<span class="note warn">Nemá kam: všechny přepážky už mají jiné palivo. Zakázku takhle nepůjde uložit.</span>`;
    } else if (selected.length)
      status = liters > capacity
        ? `<span class="note warn">Nevejde se: ${orderText(orderLiters.format(liters))} l, přepážky pojmou ${orderText(orderLiters.format(capacity))} l.</span>`
        : `<span class="note">Přepážky pojmou ${orderText(orderLiters.format(capacity))} l.</span>`;
    box.innerHTML = `<span class="comp-label">Přepážky</span>${chips}${status}`;
  });
}

function loadsText(order) {
  return orderLoadsOf(order)
    .map((load) => `${load.fuelType || "—"} ${orderLiters.format(Number(load.liters || 0))} l${load.compartments?.length ? ` (přepážky ${load.compartments.join(", ")})` : ""}`)
    .join(", ");
}

function loadingField(prefix, current) {
  const places = orderCatalog.loadingPlaces || [];
  if (!places.length) {
    return `<label>Nakládka <input id="${prefix}-loading" required maxlength="160" value="${orderText(current)}">
      <span class="note">Seznam nakládek je prázdný (tabulka Disp_LoadingPlace).</span></label>`;
  }
  const known = places.some((item) => item.name === current);
  const legacy = current && !known
    ? `<option value="${orderText(current)}" selected>${orderText(current)} (mimo seznam)</option>`
    : "";
  const body = places.map((item) =>
    `<option value="${orderText(item.name)}"${item.name === current ? " selected" : ""}>${orderText(item.name)}</option>`).join("");
  return `<label>Nakládka <select id="${prefix}-loading" required>
      <option value=""${current ? "" : " selected"}>— vyberte nakládku —</option>${legacy}${body}
    </select></label>`;
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

function stopPlaceField(current) {
  const points = (orderCatalog.points || []).filter((item) => item.isUnloading);
  if (!points.length)
    return `<label>Místo <input data-stop-place maxlength="160" value="${orderText(current)}"></label>`;
  const known = points.some((item) => item.name === current);
  const legacy = current && !known
    ? `<option value="${orderText(current)}" selected>${orderText(current)} (mimo seznam)</option>`
    : "";
  const body = points.map((item) =>
    `<option value="${orderText(item.name)}"${item.name === current ? " selected" : ""}>${orderText(item.name)}</option>`).join("");
  return `<label>Místo <select data-stop-place>
      <option value=""${current ? "" : " selected"}>— vyberte místo —</option>${legacy}${body}
    </select></label>`;
}

function distanceEntry(ok, hit) {
  if (!ok || !hit || hit.km === null || hit.km === undefined) return null;
  return { km: Number(hit.km), billed: hit.billedKm === null || hit.billedKm === undefined ? null : Number(hit.billedKm) };
}

// Jeden řádek vykládky: přepážka × palivo × litry. Vykládka jich může mít víc.
function stopItemRow(item, headerFuelId, headerFuelName) {
  const headerDefault = stopFuelId(null, headerFuelId, headerFuelName);
  const fuelId = item?.fuelTypeId || headerDefault;
  const linked = !item?.fuelTypeId || String(item.fuelTypeId) === String(headerDefault) ? "1" : "0";
  return `<div class="stop-item" data-stop-item>
      <label>Palivo <select data-stop-fuel data-fuel-linked="${linked}">${fuelOptions(fuelId, item?.fuelType || headerFuelName, false)}</select></label>
      <label>Litry <input data-stop-liters type="number" min="0" step="0.001" value="${orderText(item?.liters ?? "")}"></label>
      <button type="button" class="ghost" data-stop-item-remove title="Odebrat palivo z vykládky">×</button>
      <div class="item-comp" data-item-comp data-selected="${orderText(itemCompartments(item).join(","))}"></div>
    </div>`;
}

function itemCompartments(item) {
  if (item?.compartments?.length) return item.compartments;
  return item?.compartment ? [item.compartment] : [];
}

function selectedItemCompartments(box) {
  return (box?.dataset.selected || "").split(",").map(Number).filter((number) => Number.isInteger(number) && number > 0);
}

function stopItemsOf(stop) {
  if (stop?.items?.length) return stop.items;
  return [{ fuelTypeId: stop?.fuelTypeId || null, fuelType: stop?.fuelType || "", compartment: null, liters: stop?.liters ?? "" }];
}

function stopRow(prefix, stop, leg, headerFuelId, headerFuelName) {
  const arrival = stop?.arrivalAt ? toOrderLocal(stop.arrivalAt) : "";
  const source = leg?.kmSource === "Matrix" || leg?.kmSource === "Manual" ? leg.kmSource : "";
  return `<div class="stop-row" data-stop-row data-km-source="${orderText(source)}">
      <p class="stop-pair" data-stop-pair></p>
      ${stopPlaceField(stop?.place || "")}
      <div class="stop-items">
        <div data-stop-items>${stopItemsOf(stop).map((item) => stopItemRow(item, headerFuelId, headerFuelName)).join("")}</div>
        <button type="button" class="ghost" data-add-stop-item>+ Další palivo / přepážka</button>
        <span class="note" data-stop-items-total></span>
      </div>
      <div class="stop-km">
        <label>Nákladové km <input data-leg-km type="number" min="0" step="0.001" value="${orderText(leg?.kilometers ?? "")}"></label>
        <label>Fakturované km <input data-leg-billed type="number" min="0" step="0.001" placeholder="= nákladové" value="${orderText(leg?.billedKilometers ?? "")}"></label>
        <span class="km-source" data-km-label></span>
        <button type="button" class="km-save" data-km-save hidden>Uložit do matice</button>
      </div>
      <div class="stop-sale">
        <label>Prodej Kč <input data-leg-sale type="number" min="0" step="0.01" value="${orderText(leg?.salePrice ?? "")}"${leg?.saleOverridden ? "" : " readonly"}></label>
        <label class="stop-override"><input data-leg-override type="checkbox"${leg?.saleOverridden ? " checked" : ""}> Ruční cena</label>
      </div>
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
  label.classList.toggle("missing", source !== "Matrix" && source !== "Manual" && !!place && km === "");
  if (source === "Matrix") label.textContent = "z matice";
  else if (source === "Manual") label.textContent = "ručně, není v matici";
  else if (place && km === "") label.textContent = "není v matici, zadejte km";
  else label.textContent = "";
  const save = row.querySelector("[data-km-save]");
  if (save) {
    const pair = row.dataset.kmFrom && row.dataset.kmTo;
    save.hidden = !(pair && Number(km) > 0 && (source === "Manual" || row.dataset.billedDirty === "1"));
    save.disabled = false;
  }
}

function paintPairs(prefix) {
  const rows = [...document.querySelectorAll(`#${prefix}-stops [data-stop-row]`)];
  const loading = document.querySelector(`#${prefix}-loading`)?.value.trim() || "";
  rows.forEach((row, index) => {
    const from = index === 0 ? loading : (rows[index - 1].querySelector("[data-stop-place]")?.value.trim() || "");
    const to = row.querySelector("[data-stop-place]")?.value.trim() || "";
    row.dataset.kmFrom = from;
    row.dataset.kmTo = to;
    const fromName = index === 0 ? "Nakládka" : `Vykládka ${index}`;
    const target = row.querySelector("[data-stop-pair]");
    if (target) target.textContent = `Úsek ${index + 1}: ${fromName}${from ? ` (${from})` : ""} → Vykládka ${index + 1}${to ? ` (${to})` : ""}`;
    paintKmSource(row);
  });
}

async function saveKmToMatrix(prefix, row) {
  const button = row.querySelector("[data-km-save]");
  const km = Number(row.querySelector("[data-leg-km]")?.value);
  const from = row.dataset.kmFrom || "";
  const to = row.dataset.kmTo || "";
  if (!from || !to || !(km > 0)) return;
  const billedText = row.querySelector("[data-leg-billed]")?.value.trim() ?? "";
  const billedKm = billedText === "" ? null : Number(billedText);
  if (button) button.disabled = true;
  const result = await orderFetch("/api/dispatch/distances", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ from, to, km, billedKm })
  });
  if (!result.ok) {
    if (button) button.disabled = false;
    orderShow(orderMessage, result.data?.message || "Kilometry se nepodařilo uložit do matice.");
    return;
  }
  distanceCache.set(canonicalKey(from, to), { km: Number(result.data?.km ?? km), billed: billedKm });
  row.dataset.kmSource = "Matrix";
  row.dataset.billedDirty = "";
  orderShow(orderMessage, "");
  paintKmSource(row);
  refreshRoute(prefix);
}

function applyLookedUpKm(row, entry) {
  const input = row.querySelector("[data-leg-km]");
  const billed = row.querySelector("[data-leg-billed]");
  if (!input) return;
  applyingMatrixKm = true;
  row.dataset.billedDirty = "";
  if (!entry) {
    input.value = "";
    if (billed) billed.value = "";
    row.dataset.kmSource = "";
  } else {
    input.value = String(entry.km);
    if (billed) billed.value = entry.billed === null || entry.billed === undefined ? "" : String(entry.billed);
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
      const entry = distanceEntry(result.ok, hits[index]);
      distanceCache.set(item.key, entry);
      if (item.row.dataset.kmPair === item.key) applyLookedUpKm(item.row, entry);
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
      distanceCache.set(item.key, distanceEntry(result.ok, hits[index]));
    });
  }
  for (const item of items) {
    if (item.row.dataset.kmSource) continue;
    const known = distanceCache.has(item.key) ? distanceCache.get(item.key) : undefined;
    const current = item.row.querySelector("[data-leg-km]").value.trim();
    if (current === "") {
      if (known) applyLookedUpKm(item.row, known);
      else paintKmSource(item.row);
      continue;
    }
    item.row.dataset.kmSource = known && Number(known.km) === Number(current) ? "Matrix" : "Manual";
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
    const items = [...row.querySelectorAll("[data-stop-item]")].map((itemRow) => {
      const fuel = Number(itemRow.querySelector("[data-stop-fuel]")?.value);
      const compartments = selectedItemCompartments(itemRow.querySelector("[data-item-comp]"));
      const litersText = itemRow.querySelector("[data-stop-liters]")?.value.trim() ?? "";
      return {
        fuelTypeId: Number.isInteger(fuel) && fuel > 0 ? fuel : null,
        compartment: compartments[0] ?? null,
        compartments,
        liters: litersText === "" || !Number.isFinite(Number(litersText)) ? null : Number(litersText)
      };
    });
    const liters = items.reduce((sum, item) => sum + Number(item.liters || 0), 0);
    const itemsTotal = row.querySelector("[data-stop-items-total]");
    if (itemsTotal) itemsTotal.textContent = items.length > 1 ? `Vykládka celkem ${orderLiters.format(liters)} l` : "";
    const kmText = row.querySelector("[data-leg-km]").value.trim();
    const kilometers = kmText === "" ? null : Number(kmText);
    const billedText = row.querySelector("[data-leg-billed]")?.value.trim() ?? "";
    const billedKilometers = billedText === "" || !Number.isFinite(Number(billedText)) ? null : Number(billedText);
    const overridden = row.querySelector("[data-leg-override]").checked;
    const overridePrice = Number(row.querySelector("[data-leg-sale]").value || 0);
    const from = index === 0 ? loading : stops[index - 1].place;
    const safeLiters = Number.isFinite(liters) ? liters : 0;
    const safeKm = kilometers === null || !Number.isFinite(kilometers) ? 0 : kilometers;
    const sale = overridden
      ? roundMoney(overridePrice)
      : mode === "Liters"
        ? roundMoney(safeLiters * (Number.isFinite(salePerLiter) ? salePerLiter : 0))
        : roundMoney((billedKilometers ?? safeKm) * (Number.isFinite(salePerKm) ? salePerKm : 0));
    const cost = roundMoney(safeKm * (Number.isFinite(costPerKm) ? costPerKm : 0));
    stops.push({
      place,
      liters: Number.isFinite(liters) ? liters : null,
      fuelTypeId: items[0]?.fuelTypeId ?? null,
      items,
      note: row.querySelector("[data-stop-note]").value,
      arrivalAt: row.querySelector("[data-stop-arrival]").value
        ? new Date(row.querySelector("[data-stop-arrival]").value).toISOString()
        : null
    });
    legs.push({
      from,
      to: place,
      kilometers,
      billedKilometers,
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

// Přepážky u vykládky: nabízí jen přepážky, které se v nakládce plní, s palivem, které v nich je.
function renderStopCompartments(prefix) {
  const owners = new Map();
  readLoads(prefix).forEach((load) => load.compartments.forEach((number) => owners.set(number, load)));
  const numbers = [...owners.keys()].sort((a, b) => a - b);
  document.querySelectorAll(`#${prefix}-stops [data-item-comp]`).forEach((box) => {
    const selected = selectedItemCompartments(box);
    const itemRow = box.closest("[data-stop-item]");
    const fuelSelect = itemRow?.querySelector("[data-stop-fuel]");
    const liters = Number(itemRow?.querySelector("[data-stop-liters]")?.value || 0);
    if (!numbers.length) {
      box.innerHTML = `<span class="comp-label">Přepážky</span><span class="note">${selected.length
        ? `Vybrané přepážky ${orderText(selected.join(", "))}: nakládka přepážky nemá, zakázku takhle neuložíte.`
        : "Nakládka nemá přepážky, u vykládky se nevybírají."}</span>`;
      box.classList.remove("missing");
      return;
    }
    // Palivo se řídí přepážkou: v přepážce je palivo z nakládky.
    const owner = owners.get(selected[0]);
    if (owner?.fuelTypeId && fuelSelect && fuelSelect.value !== String(owner.fuelTypeId)) {
      fuelSelect.value = String(owner.fuelTypeId);
      fuelSelect.dataset.fuelLinked = "0";
    }
    const fuelId = Number(fuelSelect?.value || 0);
    const chips = numbers.map((number) => {
      const load = owners.get(number);
      const mine = selected.includes(number);
      // Do jednoho řádku patří jen přepážky se stejným palivem.
      const other = !mine && selected.length > 0 && load.fuelTypeId && fuelId && load.fuelTypeId !== fuelId;
      return `<label class="comp-chip${mine ? " on" : ""}${other ? " taken" : ""}" title="${orderText(`${load.fuelType || ""}${other ? " – jiné palivo než v řádku" : ""}`)}">
        <input type="checkbox" data-item-comp-number="${number}"${mine ? " checked" : ""}${other ? " disabled" : ""}>
        <b>${number}</b> <small>${orderText(load.fuelType || "")}</small></label>`;
    }).join("");
    const strangers = selected.filter((number) => !owners.has(number));
    const status = strangers.length
      ? `<span class="note warn">Přepážka ${orderText(strangers.join(", "))} se v nakládce neplní.</span>`
      : !selected.length && liters > 0 ? `<span class="note warn">Vyberte přepážku (i víc), ze které se vykládá.</span>` : "";
    box.classList.toggle("missing", !selected.length && liters > 0);
    box.innerHTML = `<span class="comp-label">Z přepážek</span>${chips}${status}`;
  });
}

// Náklad Kč/km: když ho nikdo nevyplnil (prázdné nebo 0), doplní se sazba vozidla,
// jinak výchozí sazba. Obě se zadávají v SQL (Disp_Vehicle.CostPerKm, Disp_Setting CostPerKm).
function applyCostDefault(prefix) {
  const field = document.querySelector(`#${prefix}-cost-km`);
  const note = document.querySelector(`#${prefix}-cost-note`);
  if (!field) return;
  const vehicleId = document.querySelector(`#${prefix}-vehicle`)?.value || "";
  const vehicle = (orderCatalog.vehicles || []).find((item) => item.id === vehicleId);
  const vehicleRate = vehicle?.costPerKm === null || vehicle?.costPerKm === undefined ? null : Number(vehicle.costPerKm);
  const rate = vehicleRate ?? Number(orderCatalog.defaultCostPerKm || 0);
  const source = vehicleRate !== null ? `sazba vozidla ${vehicle.plate || ""}`.trim() : "výchozí sazba";
  const locked = prefix === "edit" && !!orderDetail?.order?.isLocked;
  const blank = field.value.trim() === "" || Number(field.value) === 0;
  if (!field.disabled && !locked && !field.dataset.touched && (blank || field.dataset.auto === "1")) {
    field.value = rate > 0 ? String(rate) : "";
    field.dataset.auto = rate > 0 ? "1" : "";
  }
  if (!note) return;
  if (rate <= 0)
    note.textContent = "Sazba není zadaná. Doplňte ji v SQL (Disp_Vehicle.CostPerKm) nebo ji napište sem.";
  else if (field.dataset.auto === "1")
    note.textContent = `Doplněno: ${source}.`;
  else if (Number(field.value) !== rate)
    note.textContent = `${source[0].toUpperCase()}${source.slice(1)}: ${orderMoney.format(rate)} Kč/km.`;
  else
    note.textContent = "";
}

function refreshRoute(prefix) {
  applyCostDefault(prefix);
  paintPairs(prefix);
  renderCompartments(prefix);
  renderStopCompartments(prefix);
  const state = routeState(prefix);
  const loads = readLoads(prefix);
  const loaded = loads.reduce((sum, load) => sum + Number(load.liters || 0), 0);
  const surcharges = Number(document.querySelector(`#${prefix}-surcharges`)?.value || 0);
  const delivered = state.stops.reduce((sum, stop) => sum + Number(stop.liters || 0), 0);
  const totalKm = state.legs.reduce((sum, leg) => sum + Number(leg.kilometers || 0), 0);
  const billedKm = state.legs.reduce((sum, leg) => sum + Number(leg.billedKilometers ?? leg.kilometers ?? 0), 0);
  const legSale = roundMoney(state.legs.reduce((sum, leg) => sum + leg.salePrice, 0));
  const totalCost = roundMoney(state.legs.reduce((sum, leg) => sum + leg.cost, 0));
  const totalSale = roundMoney(legSale + (Number.isFinite(surcharges) ? surcharges : 0));
  const margin = roundMoney(totalSale - totalCost);
  const marginPerKm = totalKm === 0 ? 0 : roundMoney(margin / totalKm);
  const marginPercent = totalSale === 0 ? 0 : Math.round((margin / totalSale) * 10000) / 100;
  const box = document.querySelector(`#${prefix}-totals`);
  if (box) {
    const cards = [
      ["Nákladové km", totalKm],
      ["Fakturované km", billedKm],
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
  const total = document.querySelector(`#${prefix}-loads-total`);
  if (total) total.textContent = `Celkem naloženo ${orderLiters.format(loaded)} l`;
  const warn = document.querySelector(`#${prefix}-liter-warn`);
  if (warn) {
    const first = loads[0];
    const balance = new Map();
    const key = (id, name) => (id ? `id:${id}` : `n:${String(name || "").toLowerCase()}`);
    for (const load of loads) {
      const k = key(load.fuelTypeId, load.fuelType);
      const item = balance.get(k) || { name: load.fuelType, loaded: 0, delivered: 0 };
      item.loaded += Number(load.liters || 0);
      balance.set(k, item);
    }
    for (const part of state.stops.flatMap((stop) => stop.items)) {
      const k = part.fuelTypeId ? key(part.fuelTypeId) : key(first?.fuelTypeId, first?.fuelType);
      const name = part.fuelTypeId
        ? ((orderCatalog.fuelTypes || []).find((item) => item.id === part.fuelTypeId)?.name || "")
        : first?.fuelType;
      const item = balance.get(k) || { name, loaded: 0, delivered: 0 };
      item.delivered += Number(part.liters || 0);
      balance.set(k, item);
    }
    // Vyloženo musí po každém palivu sedět na naloženo, jinak server zakázku neuloží.
    const wrong = [...balance.values()].filter((item) => Math.abs(item.delivered - item.loaded) > 0.0005);
    warn.hidden = wrong.length === 0;
    warn.textContent = wrong.length
      ? `Vyložené litry nesedí na naložené, zakázku nepůjde uložit: ${wrong.map((item) => {
        const difference = item.delivered - item.loaded;
        return `${item.name || "palivo"} naloženo ${orderLiters.format(item.loaded)} l, vyloženo ${orderLiters.format(item.delivered)} l (${difference > 0 ? "o" : "chybí"} ${orderLiters.format(Math.abs(difference))} l${difference > 0 ? " víc" : ""})`;
      }).join("; ")}.`
      : "";
  }
  return state;
}

function customerSnapshot(order) {
  if (!order) return "";
  const parts = [order.customerIco ? `IČO ${order.customerIco}` : "", [order.customerStreet, order.customerCity, order.customerZip].filter(Boolean).join(", ")].filter(Boolean);
  return parts.join(" · ");
}

function readForm(prefix) {
  const value = (name) => document.querySelector(`#${prefix}-${name}`).value;
  const loads = readLoads(prefix);
  const firstLoad = loads[0] || {};
  const idText = value("customer-id").trim();
  const liters = loads.reduce((sum, load) => sum + Number(load.liters || 0), 0);
  const surcharges = value("surcharges").trim() === "" ? 0 : Number(value("surcharges"));
  const route = refreshRoute(prefix);
  const agreed = roundMoney(route.legs.reduce((sum, leg) => sum + leg.salePrice, 0));
  const last = route.stops.length ? route.stops[route.stops.length - 1].place : "";
  return {
    customerPohodaId: idText === "" ? null : Number(idText),
    customerName: value("customer"),
    missingDocumentsReason: document.querySelector(`#${prefix}-docs-reason`) ? value("docs-reason") : null,
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
    loadingTime: value("loading-time") || null,
    unloadingTime: value("unloading-time") || null,
    fuelType: firstLoad.fuelType || "",
    fuelTypeId: firstLoad.fuelTypeId || null,
    liters: Number.isFinite(liters) ? liters : null,
    loads: loads.map((load) => ({ fuelTypeId: load.fuelTypeId, liters: load.liters, compartments: load.compartments.length ? load.compartments : null })),
    agreedPrice: agreed,
    surcharges: Number.isFinite(surcharges) ? surcharges : null,
    vehicleId: value("vehicle"),
    driverId: value("driver"),
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
      billedKilometers: leg.billedKilometers,
      salePrice: leg.salePrice,
      saleOverridden: leg.saleOverridden,
      kmSource: leg.kmSource
    }))
  };
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
  document.querySelector("#order-rows").innerHTML = orderList.map((order) => `<tr${order.missingDocumentsReason ? ` class="has-reason"` : ""}>
      <td>${orderText(order.code)}<br><span class="route-to">v${orderText(order.currentVersion)}</span></td>
      <td>${orderText(order.customerName)}</td>
      <td>${orderText(order.loadingPlace)}<br><span class="route-to">→ ${orderText(order.unloadingPlace)}</span></td>
      <td>${orderDay(order.loadingDate)}</td>
      <td>${orderText(loadsText(order))}<br><span class="route-to">celkem ${orderLiters.format(Number(order.liters || 0))} l</span></td>
      <td class="num">${orderText(priceText(order))}</td>
      <td>${orderText(order.status)}${order.isLocked ? `<br><span class="pill">Zámek</span>` : ""}</td>
      <td>${orderText(order.pohodaOrderNumber || "—")}<br>${order.latestSyncStatus ? syncPill(order.latestSyncStatus) : `<span class="pill">neodesláno</span>`}${order.latestSyncWarning ? `<div class="note warn">${orderText(order.latestSyncWarning)}</div>` : ""}${order.latestSyncError ? `<div class="note">${orderText(order.latestSyncError)}</div>` : ""}</td>
      <td><button type="button" data-open="${orderText(order.id)}">Otevřít</button></td>
    </tr>${order.missingDocumentsReason
      ? `<tr class="reason-row"><td colspan="9"><span class="reason-label">Chybějící doklady, důvod:</span>${orderText(order.missingDocumentsReason)}</td></tr>`
      : ""}`).join("");
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
  if (typeof window.loadOrderDocuments === "function") window.loadOrderDocuments(order);
  document.querySelector("#order-detail").hidden = false;
  document.querySelector("#order-detail-title").textContent = `${order.code} · verze ${order.currentVersion}`;
  const statusPill = document.querySelector("#order-detail-status");
  statusPill.textContent = order.status || "";
  statusPill.dataset.status = order.status || "";
  const docsReason = document.querySelector("#order-docs-reason");
  docsReason.hidden = !order.missingDocumentsReason;
  docsReason.textContent = order.missingDocumentsReason
    ? `Potvrzeno k fakturaci bez všech dokladů. Důvod: ${order.missingDocumentsReason}`
    : "";
  const syncWarning = (data.sync ?? []).find((item) => item.warning)?.warning || order.latestSyncWarning || "";
  const warningNote = document.querySelector("#order-sync-warning");
  warningNote.hidden = !syncWarning;
  warningNote.textContent = syncWarning;
  document.querySelector("#order-lock-note").hidden = !order.isLocked || order.status === "Stornováno";
  const cancelled = order.status === "Stornováno";
  const cancelNote = document.querySelector("#order-cancel-note");
  cancelNote.hidden = !cancelled;
  cancelNote.textContent = cancelled
    ? `Stornováno.${order.cancelReason ? ` Důvod: ${order.cancelReason}` : ""} Zakázka je jen ke čtení a do součtů ani kalendáře se nepočítá.`
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
  stampPairs("edit");
  refreshRoute("edit");
  labelKnownKm("edit");
  if (!cancelled) refreshAvailability("edit");
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
    const billed = leg.billedKilometers === null || leg.billedKilometers === undefined ? "= nákladové" : `${orderLiters.format(Number(leg.billedKilometers))} km`;
    const fuel = stop.items?.length
      ? stop.items.map((item) => `${item.fuelType || ""}${itemCompartments(item).length ? ` (p. ${itemCompartments(item).join(", ")})` : ""} ${orderLiters.format(Number(item.liters || 0))} l`).join(", ")
      : stop.fuelType || version.fuelType || "";
    return `<tr>
      <td>${orderText(leg.sequence || index + 1)}</td>
      <td>${orderText(leg.fromPlace || "")}</td>
      <td>${orderText(leg.toPlace || stop.place || "")}</td>
      <td>${orderText(fuel)}</td>
      <td class="num">${orderText(km)}</td>
      <td class="num">${orderText(billed)}</td>
      <td class="num">${orderLiters.format(Number(leg.liters ?? stop.liters ?? 0))} l</td>
      <td class="num">${orderMoney.format(Number(leg.salePrice || 0))} Kč${leg.saleOverridden ? " · ruční" : ""}</td>
      <td class="num">${orderMoney.format(Number(leg.costPrice || 0))} Kč</td>
      <td>${orderText(stop.note || "")}</td>
    </tr>`;
  }).join("");
  return `<table id="version-lines">
    <thead><tr><th>Úsek</th><th>Odkud</th><th>Kam</th><th>Palivo</th><th>Nákladové km</th><th>Fakturované km</th><th>Litry</th><th>Prodej</th><th>Náklad</th><th>Poznámka</th></tr></thead>
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
      ${versionFact("Datum nakládky", `${orderDay(version.loadingDate)} ${version.loadingTime || ""}`.trim())}
      ${versionFact("Datum vykládky", `${orderDay(version.unloadingDate)} ${version.unloadingTime || ""}`.trim())}
      ${versionFact("Nakládka po palivech", loadsText(version))}
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
  document.querySelector("#orders-view").hidden = name !== "orders";
  document.querySelector("#calendar-view").hidden = name !== "calendar";
  document.querySelector("#queue-view").hidden = name !== "queue";
  const driverView = document.querySelector("#driver-view");
  if (driverView) driverView.hidden = name !== "driver";
  for (const button of document.querySelectorAll(".view-tabs button"))
    button.removeAttribute("aria-current");
  // Kalendář patří pod záložku Zakázky.
  document.querySelector(`#tab-${name === "calendar" ? "orders" : name}`)?.setAttribute("aria-current", "page");
  if (name === "orders") loadOrders();
  if (name === "queue") loadQueue();
  if (name === "calendar" && typeof window.loadCalendar === "function") window.loadCalendar();
  if (name === "orders") history.replaceState(null, "", window.keepQuery("#zakazky"));
  if (name === "calendar") history.replaceState(null, "", window.keepQuery("#kalendar"));
  if (name === "queue") history.replaceState(null, "", window.keepQuery("#fronta"));
  if (name === "driver") {
    history.replaceState(null, "", window.keepQuery("#ridic"));
    if (typeof window.loadDriverView === "function") window.loadDriverView();
  }
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
  const [vehicles, drivers, fuels, loadingPlaces, points, rates] = await Promise.all([
    orderFetch("/api/dispatch/vehicles"),
    orderFetch("/api/dispatch/drivers"),
    orderFetch("/api/dispatch/fuel-types"),
    orderFetch("/api/dispatch/loading-places"),
    orderFetch("/api/dispatch/points"),
    orderFetch("/api/dispatch/rates")
  ]);
  if (rates.ok) orderCatalog.defaultCostPerKm = Number(rates.data?.defaultCostPerKm || 0);
  if (Array.isArray(vehicles.data)) orderCatalog.vehicles = vehicles.data;
  if (Array.isArray(drivers.data)) orderCatalog.drivers = drivers.data;
  if (Array.isArray(fuels.data)) orderCatalog.fuelTypes = fuels.data;
  if (Array.isArray(loadingPlaces.data)) orderCatalog.loadingPlaces = loadingPlaces.data;
  if (Array.isArray(points.data)) orderCatalog.points = points.data;
}

async function loadCatalog() {
  await fetchCatalog();
  document.querySelector("#order-create").innerHTML = formFields("create");
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

// Klepnutí na záložku Zakázky vždy ukáže jen seznam, otevřená zakázka se zavře.
document.querySelector("#tab-orders").addEventListener("click", () => {
  closeOrderDetail();
  showView("orders");
});
document.querySelector("#tab-queue").addEventListener("click", () => showView("queue"));
document.querySelector("#open-calendar").addEventListener("click", () => showView("calendar"));
document.querySelector("#calendar-back").addEventListener("click", () => showView("orders"));
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
  const loadsBox = event.target.closest("[data-loads]");
  if (loadsBox) {
    const loadsPrefix = loadsBox.dataset.loads;
    if (event.target.closest("[data-add-load]")) {
      const used = new Set(readLoads(loadsPrefix).map((load) => String(load.fuelTypeId)));
      const next = (orderCatalog.fuelTypes || []).find((item) => !used.has(String(item.id)));
      document.querySelector(`#${loadsPrefix}-loads`).insertAdjacentHTML("beforeend", loadRow(next ? { fuelTypeId: next.id, liters: "" } : {}, false));
      refreshRoute(loadsPrefix);
    } else if (event.target.closest("[data-load-remove]")) {
      if (document.querySelectorAll(`#${loadsPrefix}-loads [data-load-row]`).length > 1)
        event.target.closest("[data-load-row]")?.remove();
      refreshRoute(loadsPrefix);
    }
    return;
  }
  const route = event.target.closest("[data-route]");
  if (!route) return;
  // Přepážky vykládky obslouží událost change (překreslení při kliknutí by výběr zrušilo).
  if (event.target.closest("[data-item-comp]")) return;
  const prefix = route.dataset.route;
  const row = event.target.closest("[data-stop-row]");
  if (event.target.closest("[data-add-stop]")) {
    const headerFuel = firstLoadFuel(prefix);
    document.querySelector(`#${prefix}-stops`).insertAdjacentHTML("beforeend", stopRow(prefix, {}, null, headerFuel.id, headerFuel.name));
    refreshRoute(prefix);
    scheduleDistanceFill(prefix);
    return;
  }
  if (!row) {
    refreshRoute(prefix);
    return;
  }
  if (event.target.closest("[data-add-stop-item]")) {
    const headerFuel = firstLoadFuel(prefix);
    row.querySelector("[data-stop-items]").insertAdjacentHTML("beforeend", stopItemRow({ liters: "" }, headerFuel.id, headerFuel.name));
    refreshRoute(prefix);
    return;
  }
  if (event.target.closest("[data-stop-item-remove]")) {
    const items = row.querySelectorAll("[data-stop-item]");
    if (items.length > 1) event.target.closest("[data-stop-item]").remove();
    refreshRoute(prefix);
    return;
  }
  if (event.target.closest("[data-km-save]")) {
    saveKmToMatrix(prefix, row);
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
  // Zaškrtnutí přepážky řeší posluchač "change".
  if (event.target.matches("[data-comp-number], [data-item-comp-number]")) return;
  if (event.target.matches("[id$='-customer']") && !event.target.id.endsWith("-customer-id")) {
    const prefix = event.target.id.startsWith("edit-") ? "edit" : "create";
    const idField = document.querySelector(`#${prefix}-customer-id`);
    if (idField && event.target.id === `${prefix}-customer`) {
      idField.value = "";
      scheduleCustomerSearch(prefix);
    }
  }
  if (event.target.matches("[id$='-cost-km']")) {
    event.target.dataset.touched = "1";
    event.target.dataset.auto = "";
  }
  if (event.target.matches("[data-leg-billed]")) {
    const billedRow = event.target.closest("[data-stop-row]");
    if (billedRow) {
      billedRow.dataset.billedDirty = "1";
      paintKmSource(billedRow);
    }
  }
  if (event.target.matches("[data-leg-km]") && !applyingMatrixKm) {
    const kmRow = event.target.closest("[data-stop-row]");
    if (kmRow) {
      kmRow.dataset.kmSource = event.target.value.trim() === "" ? "" : "Manual";
      paintKmSource(kmRow);
    }
  }
  const route = event.target.closest("[data-route]") || event.target.closest("form");
  const prefix = event.target.id?.startsWith("edit-") ? "edit" : event.target.id?.startsWith("create-") ? "create" : (event.target.closest("[data-loads]")?.dataset.loads || route?.dataset?.route);
  if (prefix) refreshRoute(prefix);
  if (prefix && event.target.matches("[data-stop-place], [id$='-loading']")) scheduleDistanceFill(prefix);
});

document.querySelector("#orders-view").addEventListener("change", (event) => {
  if (event.target.matches("[data-comp-number]")) {
    const box = event.target.closest("[data-load-comp]");
    const prefix = event.target.closest("[data-loads]")?.dataset.loads;
    if (box && prefix) {
      const numbers = new Set(rowCompartments(event.target.closest("[data-load-row]")));
      const number = Number(event.target.dataset.compNumber);
      if (event.target.checked) numbers.add(number);
      else numbers.delete(number);
      box.dataset.selected = [...numbers].sort((a, b) => a - b).join(",");
      renderCompartments(prefix);
    }
    return;
  }
  if (event.target.matches("select[id$='-vehicle']"))
    renderCompartments(event.target.id.startsWith("edit-") ? "edit" : "create");
  if (event.target.matches("[id$='-loading-date'], [id$='-unloading-date'], [id$='-loading-time'], [id$='-unloading-time']"))
    scheduleAvailability(event.target.id.startsWith("edit-") ? "edit" : "create");
  if (event.target.matches("select[id$='-driver']"))
    applyAvailability(event.target.id.startsWith("edit-") ? "edit" : "create");
  if (event.target.matches("select[id$='-loading']")) {
    const prefix = event.target.id.startsWith("edit-") ? "edit" : "create";
    refreshRoute(prefix);
    scheduleDistanceFill(prefix);
  }
  if (event.target.matches("[data-load-fuel]")) {
    const prefix = event.target.closest("[data-loads]")?.dataset.loads;
    const first = prefix ? firstLoadFuel(prefix) : null;
    if (first) {
      document.querySelectorAll(`#${prefix}-stops [data-stop-fuel][data-fuel-linked="1"]`).forEach((select) => {
        select.value = String(first.id);
      });
      refreshRoute(prefix);
    }
  }
  if (event.target.matches("[data-item-comp-number]")) {
    const box = event.target.closest("[data-item-comp]");
    const numbers = new Set(selectedItemCompartments(box));
    const number = Number(event.target.dataset.itemCompNumber);
    if (event.target.checked) numbers.add(number);
    else numbers.delete(number);
    box.dataset.selected = [...numbers].sort((a, b) => a - b).join(",");
    const prefix = event.target.closest("[data-route]")?.dataset.route;
    if (prefix) refreshRoute(prefix);
    return;
  }
  if (event.target.matches("[data-stop-fuel]")) {
    const prefix = event.target.closest("[data-route]")?.dataset.route;
    const first = prefix ? firstLoadFuel(prefix) : null;
    event.target.dataset.fuelLinked = first && event.target.value === String(first.id) ? "1" : "0";
    if (prefix) refreshRoute(prefix);
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

// Potvrzení k fakturaci kontroluje povinné doklady. Když chybí, otevře se okno s důvodem.
const missingDocsDialog = document.querySelector("#confirm-docs-dialog");

function openMissingDocs(missing) {
  document.querySelector("#confirm-docs-missing").innerHTML = missing.map((item) => `<li>${orderText(item)}</li>`).join("");
  const message = document.querySelector("#confirm-docs-message");
  message.hidden = true;
  message.textContent = "";
  if (!missingDocsDialog.open) missingDocsDialog.showModal();
  document.querySelector("#confirm-docs-reason").focus();
}

// Zakázka, která se právě potvrzuje (detail nebo pohled řidiče).
let confirmTargetId = null;

function driverViewOpen() {
  const view = document.querySelector("#driver-view");
  return !!view && !view.hidden;
}

function confirmNotice(text) {
  if (driverViewOpen() && typeof window.driverViewNotice === "function") window.driverViewNotice(text);
  else orderShow(orderMessage, text);
}

async function confirmOrder(reason) {
  if (!confirmTargetId || orderSaving) return;
  orderSaving = true;
  const result = await orderFetch(`/api/dispatch/orders/${encodeURIComponent(confirmTargetId)}/confirm`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(reason ? { missingDocumentsReason: reason } : {})
  });
  orderSaving = false;
  if (result.status === 409 && result.data?.needsReason) {
    openMissingDocs(result.data.missingDocuments ?? []);
    return;
  }
  if (!result.ok) {
    const text = result.data?.message || "Potvrzení se nepodařilo.";
    if (missingDocsDialog.open) {
      const message = document.querySelector("#confirm-docs-message");
      message.hidden = false;
      message.textContent = text;
    } else {
      confirmNotice(text);
    }
    return;
  }
  if (missingDocsDialog.open) missingDocsDialog.close();
  const done = reason
    ? `Zakázka ${result.data.code || ""} je potvrzená k fakturaci. Důvod chybějících dokladů je uložený.`
    : `Zakázka ${result.data.code || ""} je potvrzená k fakturaci.`;
  if (driverViewOpen()) {
    if (typeof window.loadDriverView === "function") await window.loadDriverView();
    confirmNotice(done);
    return;
  }
  orderShow(orderMessage, done);
  await loadOrders();
  if (orderDetail?.order?.id === result.data.id) await openOrder(result.data.id, false);
}

// Potvrdit k fakturaci: zkontroluje doklady (když jsou načtené), při chybějících se ptá na důvod.
window.confirmOrderToInvoice = (id) => {
  if (!id) return;
  confirmTargetId = id;
  const missing = typeof window.missingOrderDocuments === "function" ? window.missingOrderDocuments(id) : null;
  if (missing?.length) {
    openMissingDocs(missing);
    return;
  }
  if (!window.confirm("Potvrdit zakázku k fakturaci a zařadit ji do Pohody?")) return;
  confirmOrder(null);
};

document.querySelector("#order-confirm").addEventListener("click", () => {
  if (orderDetail?.order) window.confirmOrderToInvoice(orderDetail.order.id);
});

document.querySelector("#confirm-docs-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const reason = document.querySelector("#confirm-docs-reason").value.trim();
  if (!reason) {
    const message = document.querySelector("#confirm-docs-message");
    message.hidden = false;
    message.textContent = "Vyplňte, proč doklady chybí.";
    return;
  }
  confirmOrder(reason);
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

// Detail zakázky je ve vlastním okně (dialog). Otevře se, když se detail ukáže, a zavře, když se skryje.
const orderDetailDialog = document.querySelector("#order-detail-dialog");
new MutationObserver(() => {
  const visible = !document.querySelector("#order-detail").hidden;
  if (visible && !orderDetailDialog.open) orderDetailDialog.showModal();
  if (!visible && orderDetailDialog.open) orderDetailDialog.close();
}).observe(document.querySelector("#order-detail"), { attributes: true, attributeFilter: ["hidden"] });
orderDetailDialog.addEventListener("close", () => {
  // Zavření okna (Esc, Zavřít) zavře i zakázku.
  if (!document.querySelector("#order-detail").hidden) closeOrderDetail();
});

function closeOrderDetail() {
  orderDetail = null;
  document.querySelector("#order-detail").hidden = true;
  closeVersion();
}

document.querySelector("#order-close").addEventListener("click", closeOrderDetail);

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

function openCreate(prefill) {
  const local = document.querySelector("#order-create-message");
  if (local) local.hidden = true;
  if (prefill?.date) {
    const set = (id, value) => {
      const input = document.querySelector(`#create-${id}`);
      if (input && value) input.value = value;
    };
    set("loading-date", prefill.date);
    set("unloading-date", prefill.date);
    if (prefill.time) {
      const [hours, minutes] = prefill.time.split(":").map(Number);
      const end = Math.min(hours * 60 + minutes + 8 * 60, 23 * 60 + 45);
      set("loading-time", prefill.time);
      set("unloading-time", `${String(Math.floor(end / 60)).padStart(2, "0")}:${String(end % 60).padStart(2, "0")}`);
    }
    if (prefill.driver && orderCatalog.drivers.some((driver) => driver.id === prefill.driver))
      set("driver", prefill.driver);
  }
  const dialog = document.querySelector("#order-create-dialog");
  if (dialog && !dialog.open) dialog.showModal();
  refreshAvailability("create");
}

document.querySelector("#open-order-create")?.addEventListener("click", () => openCreate());

// Odkazy z hodinového kalendáře: #zakazka=ID otevře zakázku, #nova=RRRR-MM-DDTHH:MM&ridic=ID založí novou.
function handleLinkHash() {
  const hash = decodeURIComponent(location.hash);
  if (hash.startsWith("#zakazka=")) {
    const id = hash.slice("#zakazka=".length);
    showView("orders");
    if (id) openOrder(id, true);
    return true;
  }
  if (hash.startsWith("#nova")) {
    const params = new URLSearchParams(hash.slice(1).replace(/^nova=?/, "datum="));
    const [date, time] = (params.get("datum") || "").split("T");
    showView("orders");
    openCreate({
      date: /^\d{4}-\d{2}-\d{2}$/.test(date || "") ? date : null,
      time: /^\d{2}:\d{2}$/.test(time || "") ? time : null,
      driver: params.get("ridic")
    });
    return true;
  }
  return false;
}

window.addEventListener("hashchange", () => {
  if (!location.hash.startsWith("#tyden")) handleLinkHash();
});

loadCatalog().then(() => {
  if (location.hash.startsWith("#tyden")) return;
  if (handleLinkHash()) return;
  if (location.hash === "#fronta") showView("queue");
  else if (location.hash === "#ridic") showView("driver");
  else if (location.hash === "#kalendar") showView("calendar");
  else showView("orders");
});
