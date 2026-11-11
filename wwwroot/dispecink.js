// Dispečink: společné pomůcky a kalendář vytíženosti řidičů. Nákladové sazby se mění v SQL.
// Zakázky a frontu Pohody obsluhuje dispecink-orders.js.
const text = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
}[char]));
const litersLabel = (value) => new Intl.NumberFormat("cs-CZ", { maximumFractionDigits: 0 }).format(Number(value || 0));
const dayLabel = new Intl.DateTimeFormat("cs-CZ", { weekday: "short", day: "numeric", month: "numeric" });
let calendarSeq = 0;

window.keepQuery = (hash) => `${location.pathname}${location.search}${hash || ""}`;

// ---------- Kalendář řidičů ----------

function isoDate(date) {
  const pad = (value) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function parseIso(value) {
  const [year, month, day] = String(value || "").split("-").map(Number);
  return year && month && day ? new Date(year, month - 1, day) : null;
}

function weekStart(date) {
  const copy = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  copy.setDate(copy.getDate() - ((copy.getDay() + 6) % 7));
  return copy;
}

function calendarDays() {
  return Number(document.querySelector("#calendar-days").value) || 14;
}

function calendarShow(value) {
  const box = document.querySelector("#calendar-message");
  box.hidden = !value;
  box.textContent = value ?? "";
}

function cellClass(date, count) {
  const day = parseIso(date);
  const classes = [];
  if (day && (day.getDay() === 0 || day.getDay() === 6)) classes.push("weekend");
  if (date === isoDate(new Date())) classes.push("today");
  if (count > 1) classes.push("over");
  else if (count === 1) classes.push("busy");
  return classes.join(" ");
}

function renderCalendar(data) {
  const dates = data.dates ?? [];
  document.querySelector("#calendar-head").innerHTML = `<tr><th>Řidič</th>${dates.map((date) =>
    `<th class="${cellClass(date, 0)}">${text(dayLabel.format(parseIso(date)))}</th>`).join("")}</tr>`;
  const rows = data.drivers ?? [];
  document.querySelector("#calendar-rows").innerHTML = rows.map((row) => {
    const head = `<td><strong>${text(row.name)}</strong>${row.phone ? `<br><small class="note">${text(row.phone)}</small>` : ""}
      <small class="note">${row.driverId ? `${row.busyDays} z ${dates.length} dní · ${row.percent} %` : "nepřiřazené zakázky"}</small>
      ${row.driverId ? `<span class="load-bar" aria-hidden="true"><i style="width:${Math.min(100, Number(row.percent) || 0)}%"></i></span>` : ""}</td>`;
    const cells = (row.days ?? []).map((day) => {
      const orders = day.orders ?? [];
      const body = orders.map((order) =>
        `<button type="button" class="linkish cal-order" data-cal-order="${text(order.id)}" title="${text(order.status)}">${text(order.code)}
          <small>${text(order.customer)} · ${litersLabel(order.liters)} l${order.vehicle ? ` · ${text(order.vehicle)}` : ""}</small></button>`).join("");
      return `<td class="${cellClass(day.date, row.driverId ? orders.length : 0)}">${body}</td>`;
    }).join("");
    return `<tr>${head}${cells}</tr>`;
  }).join("") || `<tr><td colspan="${dates.length + 1}">V dispečinku zatím nejsou řidiči.</td></tr>`;
  const clashes = rows.filter((row) => row.driverId && (row.days ?? []).some((day) => (day.orders ?? []).length > 1));
  calendarShow(clashes.length
    ? `Pozor: ${clashes.map((row) => row.name).join(", ")} má v některý den víc zakázek najednou (červeně).`
    : "");
}

async function loadCalendar() {
  const current = ++calendarSeq;
  const fromInput = document.querySelector("#calendar-from");
  if (!fromInput.value) fromInput.value = isoDate(weekStart(new Date()));
  calendarShow("Načítám kalendář…");
  const params = new URLSearchParams({ from: fromInput.value, days: String(calendarDays()) });
  const response = await fetch(`/api/dispatch/calendar?${params}`, { cache: "no-store" }).catch(() => null);
  if (current !== calendarSeq) return;
  if (!response) {
    calendarShow("Kalendář se nepodařilo načíst.");
    return;
  }
  if (response.status === 401) {
    location.href = "/login.html";
    return;
  }
  const data = await response.json().catch(() => ({}));
  if (current !== calendarSeq) return;
  if (!response.ok) {
    calendarShow(data.message || "Kalendář se nepodařilo načíst.");
    return;
  }
  renderCalendar(data);
}

window.loadCalendar = loadCalendar;

document.querySelector("#calendar-tools").addEventListener("click", (event) => {
  const fromInput = document.querySelector("#calendar-from");
  if (event.target.closest("[data-cal-today]")) {
    fromInput.value = isoDate(weekStart(new Date()));
    loadCalendar();
    return;
  }
  const shift = event.target.closest("[data-cal-shift]");
  if (!shift) return;
  const start = parseIso(fromInput.value) || weekStart(new Date());
  start.setDate(start.getDate() + Number(shift.dataset.calShift) * calendarDays());
  fromInput.value = isoDate(start);
  loadCalendar();
});
document.querySelector("#calendar-tools").addEventListener("submit", (event) => event.preventDefault());
document.querySelector("#calendar-from").addEventListener("change", () => loadCalendar());
document.querySelector("#calendar-days").addEventListener("change", () => loadCalendar());
document.querySelector("#calendar-rows").addEventListener("click", (event) => {
  const order = event.target.closest("[data-cal-order]");
  if (order && typeof window.showOrder === "function") window.showOrder(order.dataset.calOrder);
});

// ---------- Společné ----------

document.querySelector("#logout").addEventListener("click", async () => {
  await fetch("/api/logout", { method: "POST" });
  location.href = "/login.html";
});

for (const button of document.querySelectorAll("[data-close-dialog]")) {
  button.addEventListener("click", () => button.closest("dialog")?.close());
}
