const money = new Intl.NumberFormat("cs-CZ", { style: "currency", currency: "CZK" });
const text = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
}[char]));
const dateFormat = new Intl.DateTimeFormat("cs-CZ");
const fromInput = document.querySelector("#from");
const toInput = document.querySelector("#to");
const customerInput = document.querySelector("#customer");
const message = document.querySelector("#message");
let requestSeq = 0;

const today = new Date();
const iso = (date) => date.toISOString().slice(0, 10);

function setRange(kind) {
  const end = new Date();
  const start = new Date(end);
  if (kind === "year") start.setMonth(0, 1);
  if (kind === "month") start.setDate(1);
  fromInput.value = iso(start);
  toInput.value = iso(end);
}

function showMessage(text) {
  message.hidden = !text;
  message.textContent = text ?? "";
}

function formatDate(value) {
  return value ? dateFormat.format(new Date(`${value}T00:00:00`)) : "";
}

function pill(status) {
  const kind = status === "Uhrazeno" ? "ok" : status === "Po splatnosti" || status === "Částečně" ? "warn" : "";
  return `<span class="pill ${kind}">${text(status)}</span>`;
}

function customerNames(data) {
  if (Array.isArray(data.customers))
    return data.customers.map((name) => String(name ?? "").trim()).filter((name) => name.length > 0);

  const names = [];
  const seen = new Set();
  for (const invoice of data.invoices ?? []) {
    const name = String(invoice.customer ?? "").trim();
    const key = name.toLocaleLowerCase("cs-CZ");
    if (name.length === 0 || seen.has(key)) continue;
    seen.add(key);
    names.push(name);
  }
  names.sort((left, right) => left.localeCompare(right, "cs-CZ"));
  return names;
}

function fillCustomers(names) {
  const selected = customerInput.value;
  customerInput.replaceChildren(new Option("Všichni odběratelé", ""));
  for (const name of names)
    customerInput.add(new Option(name, name));

  if (selected && names.includes(selected)) {
    customerInput.value = selected;
    return false;
  }

  customerInput.value = "";
  return selected.length > 0;
}

async function load(event, refresh = false) {
  event?.preventDefault();
  const current = ++requestSeq;
  if (refresh) {
    showMessage("Načítám faktury z Pohody…");
    document.querySelector("#cards").hidden = true;
    document.querySelector("#chart-wrap").hidden = true;
    document.querySelector("#table-wrap").hidden = true;
  } else {
    showMessage("Filtruji faktury…");
  }

  const params = new URLSearchParams({ from: fromInput.value, to: toInput.value, refresh: refresh ? "true" : "false" });
  if (customerInput.value)
    params.set("customer", customerInput.value);

  let response;
  try {
    response = await fetch(`/api/bi/issued-invoices?${params}`);
  } catch {
    if (current !== requestSeq) return;
    showMessage("Faktury se nepodařilo načíst.");
    return;
  }
  if (current !== requestSeq) return;
  if (response.status === 401) {
    location.href = "/login.html";
    return;
  }
  const data = await response.json();
  if (current !== requestSeq) return;
  if (!response.ok) {
    showMessage(data.message || "Faktury se nepodařilo načíst.");
    return;
  }

  if (fillCustomers(customerNames(data))) {
    await load(null, false);
    return;
  }
  if (current !== requestSeq) return;

  showMessage("");
  document.querySelector("#count").textContent = data.count;
  document.querySelector("#total").textContent = money.format(data.total);
  document.querySelector("#paid").textContent = money.format(data.paid);
  document.querySelector("#unpaid").textContent = money.format(data.unpaid);
  document.querySelector("#overdue").textContent = money.format(data.overdue);
  document.querySelector("#cards").hidden = false;

  const peak = Math.max(...data.months.map((month) => month.total), 1);
  document.querySelector("#chart").innerHTML = data.months.map((month) => `
    <div class="bar">
      <span>${text(month.month)}</span>
      <i style="width:${Math.max(2, month.total / peak * 100)}%"></i>
      <b>${money.format(month.total)}</b>
    </div>`).join("");
  document.querySelector("#chart-wrap").hidden = false;

  document.querySelector("#rows").innerHTML = data.invoices.map((invoice) => {
    const remaining = Math.max(0, invoice.total - invoice.paid);
    return `<tr>
      <td>${text(invoice.number)}</td>
      <td>${formatDate(invoice.date)}</td>
      <td>${formatDate(invoice.dueDate)}</td>
      <td>${text(invoice.customer)}</td>
      <td class="num">${money.format(invoice.total)}</td>
      <td class="num">${money.format(invoice.paid)}</td>
      <td class="num">${money.format(remaining)}</td>
      <td>${pill(invoice.status)}</td>
    </tr>`;
  }).join("") || `<tr><td colspan="8">${customerInput.value
    ? "Pro vybraného odběratele v tomto období není žádná vydaná faktura."
    : "V tomto období není žádná vydaná faktura."}</td></tr>`;
  document.querySelector("#table-wrap").hidden = false;
}

document.querySelector("#logout").addEventListener("click", async () => {
  await fetch("/api/logout", { method: "POST" });
  location.href = "/login.html";
});

const erpSections = {
  bi: "Vydané faktury",
  "zpracovani-objednavek": "Zpracování objednávek",
  "vytvareni-objednavek": "Vytváření objednávek",
  "uctovani-faktur": "Účtování faktur"
};

function erpSectionId() {
  const raw = decodeURIComponent(location.hash.replace(/^#/, "")).toLowerCase();
  return Object.hasOwn(erpSections, raw) ? raw : "bi";
}

function showErpSection() {
  const id = erpSectionId();
  document.querySelectorAll("[data-erp-section]").forEach((panel) => {
    panel.hidden = panel.dataset.erpSection !== id;
  });
  document.querySelectorAll(".erp-sections a").forEach((link) => {
    const active = link.getAttribute("href") === `#${id}`;
    if (active) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  });
  document.title = `${erpSections[id]} – PHM Trans`;
}

window.addEventListener("hashchange", showErpSection);
showErpSection();

setRange("year");
document.querySelector("#range").addEventListener("submit", (event) => load(event, true));
document.querySelectorAll("[data-range]").forEach((button) => {
  button.addEventListener("click", () => {
    setRange(button.dataset.range);
    load(null, true);
  });
});
customerInput.addEventListener("change", () => load(null, false));
load(null, true);
