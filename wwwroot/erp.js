const money = new Intl.NumberFormat("cs-CZ", { style: "currency", currency: "CZK" });
const text = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
}[char]));
const dateFormat = new Intl.DateTimeFormat("cs-CZ");
const fromInput = document.querySelector("#from");
const toInput = document.querySelector("#to");
const message = document.querySelector("#message");
const customerFilter = window.bindStatusFilter(document.querySelector("#customer"), {
  param: "odberatel",
  dynamic: true,
  noun: { few: "odběratelé", many: "odběratelů" },
  onChange() { load(null, false); }
});
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
  return customerFilter.setChoices(names.map((name) => ({ value: name, label: name })));
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
  for (const name of customerFilter.values()) params.append("customer", name);

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
  }).join("") || `<tr><td colspan="8">${customerFilter.isAll()
    ? "V tomto období není žádná vydaná faktura."
    : "Pro vybrané odběratele v tomto období není žádná vydaná faktura."}</td></tr>`;
  document.querySelector("#table-wrap").hidden = false;
}

const whole = new Intl.NumberFormat("cs-CZ", { maximumFractionDigits: 0 });
const monthlyYear = document.querySelector("#monthly-year");
const monthlyMessage = document.querySelector("#monthly-message");
let monthlySeq = 0;
let monthlyLoadedYear = null;

function showMonthlyMessage(value) {
  monthlyMessage.hidden = !value;
  monthlyMessage.textContent = value ?? "";
}

function monthlyCells(months, quarters, total) {
  const cells = [];
  for (let quarter = 0; quarter < 4; quarter++) {
    for (let month = quarter * 3; month < quarter * 3 + 3; month++)
      cells.push(`<td class="num">${whole.format(months[month] ?? 0)}</td>`);
    cells.push(`<td class="num quarter">${whole.format(quarters[quarter] ?? 0)}</td>`);
  }
  cells.push(`<td class="num quarter total">${whole.format(total ?? 0)}</td>`);
  return cells.join("");
}

function monthlyTable(section, empty) {
  const head = ["<th>Fakturace v Kč</th>"];
  for (let quarter = 0; quarter < 4; quarter++) {
    for (let month = quarter * 3 + 1; month <= quarter * 3 + 3; month++)
      head.push(`<th class="num">${month}</th>`);
    head.push(`<th class="num quarter">${quarter + 1}Q</th>`);
  }
  head.push(`<th class="num quarter total">Celkem</th>`);
  const rows = section.rows.map((row) =>
    `<tr><td>${text(row.customer)}</td>${monthlyCells(row.months, row.quarters, row.total)}</tr>`).join("")
    || `<tr><td colspan="18">${text(empty)}</td></tr>`;
  return `<thead><tr>${head.join("")}</tr></thead>
    <tbody>${rows}</tbody>
    <tfoot><tr><th>Celkem</th>${monthlyCells(section.months, section.quarters, section.total)}</tr></tfoot>`;
}

async function loadMonthly(event) {
  event?.preventDefault();
  const year = Number(monthlyYear.value);
  const current = ++monthlySeq;
  showMonthlyMessage("Načítám faktury z Pohody…");
  document.querySelector("#monthly-main-wrap").hidden = true;
  document.querySelector("#monthly-b-wrap").hidden = true;
  document.querySelector("#monthly-note").hidden = true;

  let response;
  try {
    response = await fetch(`/api/bi/monthly-invoices?${new URLSearchParams({ year: String(year) })}`);
  } catch {
    if (current === monthlySeq) showMonthlyMessage("Přehled se nepodařilo načíst.");
    return;
  }
  if (current !== monthlySeq) return;
  if (response.status === 401) {
    location.href = "/login.html";
    return;
  }
  const data = await response.json().catch(() => ({}));
  if (current !== monthlySeq) return;
  if (!response.ok) {
    showMonthlyMessage(data.message || "Přehled se nepodařilo načíst.");
    return;
  }

  showMonthlyMessage("");
  monthlyLoadedYear = year;
  document.querySelector("#monthly-main").innerHTML = monthlyTable(data.main, "V tomto roce není žádná faktura.");
  document.querySelector("#monthly-b").innerHTML = monthlyTable(data.groupB, "Odběratelé ze skupiny B v tomto roce nemají fakturu.");
  document.querySelector("#monthly-main-wrap").hidden = false;
  document.querySelector("#monthly-b-wrap").hidden = false;
  document.querySelector("#monthly-note").hidden = false;
}

const reports = { faktury: "Vydané faktury", mesicni: "Vydané faktury – měsíční přehled" };

function showReport() {
  const raw = location.hash.replace(/^#/, "");
  const id = Object.hasOwn(reports, raw) ? raw : "faktury";
  document.querySelectorAll("[data-report]").forEach((panel) => {
    panel.hidden = panel.dataset.report !== id;
  });
  document.querySelectorAll("[data-report-link]").forEach((link) => {
    if (link.dataset.reportLink === id) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  });
  document.title = `${reports[id]} – PHM Trans`;
  if (id === "mesicni" && monthlyLoadedYear === null) loadMonthly(null);
}

document.querySelector("#logout").addEventListener("click", async () => {
  await fetch("/api/logout", { method: "POST" });
  location.href = "/login.html";
});

monthlyYear.value = String(today.getFullYear());
document.querySelector("#monthly-form").addEventListener("submit", loadMonthly);
window.addEventListener("hashchange", showReport);
showReport();

setRange("year");
document.querySelector("#range").addEventListener("submit", (event) => load(event, true));
document.querySelectorAll("[data-range]").forEach((button) => {
  button.addEventListener("click", () => {
    setRange(button.dataset.range);
    load(null, true);
  });
});
load(null, true);
