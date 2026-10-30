const money = new Intl.NumberFormat("cs-CZ", { style: "currency", currency: "CZK" });
const text = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
}[char]));
const dateFormat = new Intl.DateTimeFormat("cs-CZ");
const fromInput = document.querySelector("#from");
const toInput = document.querySelector("#to");
const message = document.querySelector("#message");

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

async function load(event) {
  event?.preventDefault();
  showMessage("Načítám faktury z Pohody…");
  document.querySelector("#cards").hidden = true;
  document.querySelector("#chart-wrap").hidden = true;
  document.querySelector("#table-wrap").hidden = true;

  const response = await fetch(`/api/bi/issued-invoices?from=${fromInput.value}&to=${toInput.value}`);
  if (response.status === 401) {
    location.href = "/login.html";
    return;
  }
  const data = await response.json();
  if (!response.ok) {
    showMessage(data.message || "Faktury se nepodařilo načíst.");
    return;
  }

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
  }).join("") || `<tr><td colspan="8">V tomto období není žádná vydaná faktura.</td></tr>`;
  document.querySelector("#table-wrap").hidden = false;
}

document.querySelector("#logout").addEventListener("click", async () => {
  await fetch("/api/logout", { method: "POST" });
  location.href = "/login.html";
});

setRange("year");
document.querySelector("#range").addEventListener("submit", load);
document.querySelectorAll("[data-range]").forEach((button) => {
  button.addEventListener("click", () => {
    setRange(button.dataset.range);
    load();
  });
});
load();
