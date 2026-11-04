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
    <label>Vykládka <input id="${prefix}-unloading" required maxlength="160" value="${orderText(order?.unloadingPlace || "")}"></label>
    <label>Datum nakládky <input id="${prefix}-loading-date" type="date" required value="${orderText(order?.loadingDate || orderToday())}"></label>
    <label>Datum vykládky <input id="${prefix}-unloading-date" type="date" required value="${orderText(order?.unloadingDate || orderToday())}"></label>
    <label>Druh paliva <input id="${prefix}-fuel" required maxlength="80" value="${orderText(order?.fuelType || "")}" placeholder="motorová nafta"></label>
    <label>Litry <input id="${prefix}-liters" type="number" min="0" step="0.001" required value="${orderText(order?.liters ?? "")}"></label>
    <label>Dohodnutá cena (Kč) <input id="${prefix}-price" type="number" min="0" step="0.01" required value="${orderText(order?.agreedPrice ?? "")}"></label>
    <label>Příplatky (Kč) <input id="${prefix}-surcharges" type="number" step="0.01" value="${orderText(order?.surcharges ?? 0)}"></label>
    <label>Vozidlo <select id="${prefix}-vehicle">${orderOptions(orderCatalog.vehicles, vehicle, "— nepřiřazeno —", (item) => `${item.plate} · ${item.label}`)}</select></label>
    <label>Řidič <select id="${prefix}-driver">${orderOptions(orderCatalog.drivers, driver, "— nepřiřazeno —", (item) => item.name)}</select></label>
    <label>Jízda <select id="${prefix}-trip">${orderOptions(orderCatalog.trips, trip, "— bez jízdy —", (item) => `${item.code} · ${item.customer}`)}</select></label>
    <label>Stav <select id="${prefix}-status">${orderStatusOptions(status)}</select></label>
    <label>Číslo CMR <input id="${prefix}-cmr" maxlength="40" value="${orderText(order?.cmrNumber || "")}"></label>
    <label>Číslo faktury <input id="${prefix}-invoice" maxlength="40" value="${orderText(order?.pohodaInvoiceNumber || "")}"></label>
    <label class="wide">Poznámka <input id="${prefix}-note" maxlength="500" value="${orderText(order?.note || "")}"></label>
    ${prefix === "create" ? `<div class="wide"><button type="submit">Založit zakázku</button></div>` : ""}
  `;
}

function readForm(prefix) {
  const value = (name) => document.querySelector(`#${prefix}-${name}`).value;
  const idText = value("customer-id").trim();
  const liters = Number(value("liters"));
  const price = Number(value("price"));
  const surcharges = value("surcharges").trim() === "" ? 0 : Number(value("surcharges"));
  return {
    customerPohodaId: idText === "" ? null : Number(idText),
    customerName: value("customer"),
    loadingPlace: value("loading"),
    unloadingPlace: value("unloading"),
    loadingDate: value("loading-date"),
    unloadingDate: value("unloading-date"),
    fuelType: value("fuel"),
    liters: Number.isFinite(liters) ? liters : null,
    agreedPrice: Number.isFinite(price) ? price : null,
    surcharges: Number.isFinite(surcharges) ? surcharges : null,
    vehicleId: value("vehicle"),
    driverId: value("driver"),
    tripId: value("trip"),
    status: value("status"),
    cmrNumber: value("cmr"),
    pohodaInvoiceNumber: value("invoice"),
    note: value("note")
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
  document.querySelector("#order-cards").hidden = false;
  document.querySelector("#order-rows").innerHTML = orderList.map((order) => `<tr>
      <td>${orderText(order.code)}<br><span class="route-to">v${orderText(order.currentVersion)}</span></td>
      <td>${orderText(order.customerName)}</td>
      <td>${orderText(order.loadingPlace)}<br><span class="route-to">→ ${orderText(order.unloadingPlace)}</span></td>
      <td>${orderDay(order.loadingDate)}</td>
      <td>${orderText(order.fuelType)}<br><span class="route-to">${orderLiters.format(Number(order.liters || 0))} l</span></td>
      <td class="num">${orderText(priceText(order))}</td>
      <td>${orderText(order.status)}${order.isLocked ? `<br><span class="pill">Zámek</span>` : ""}</td>
      <td>${orderText(order.pohodaOrderNumber || "—")}<br>${syncPill(order.latestSyncStatus)}${order.latestSyncWarning ? `<div class="note warn">${orderText(order.latestSyncWarning)}</div>` : ""}</td>
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
  const syncWarning = (data.sync ?? []).find((item) => item.warning)?.warning || order.latestSyncWarning || "";
  const warningNote = document.querySelector("#order-sync-warning");
  warningNote.hidden = !syncWarning;
  warningNote.textContent = syncWarning;
  document.querySelector("#order-lock-note").hidden = !order.isLocked;
  document.querySelector("#order-confirm").disabled = !!order.isLocked;
  document.querySelector("#order-edit").innerHTML = formFields("edit", order);
  document.querySelector("#version-rows").innerHTML = (data.versions ?? []).map((version) => `<tr>
      <td>${orderText(version.currentVersion)}</td>
      <td>${orderWhen(version.updatedAt)}</td>
      <td>${orderText(version.customerName)}</td>
      <td>${orderText(version.loadingPlace)} → ${orderText(version.unloadingPlace)}</td>
      <td>${orderDay(version.loadingDate)} – ${orderDay(version.unloadingDate)}</td>
      <td>${orderText(version.fuelType)}, ${orderLiters.format(Number(version.liters || 0))} l</td>
      <td class="num">${orderMoney.format(Number(version.agreedPrice || 0))}</td>
      <td class="num">${orderMoney.format(Number(version.surcharges || 0))}</td>
      <td>${orderText(version.status)}${versionWarning(data, version)}</td>
    </tr>`).join("");
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
