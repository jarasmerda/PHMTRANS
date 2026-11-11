// Výběr času řidiče ve formuláři zakázky: den po hodinách, řádek pro každého řidiče.
// Tažením přes volný úsek (nebo klepnutím na začátek) se vyplní řidič, datum a čas od–do.
(() => {
  // Šířka hodiny: na mobilu užší, ať je vidět víc hodin najednou.
  const HOUR = window.matchMedia?.("(max-width: 760px)").matches ? 28 : 40;
  const SNAP = 15;
  const LAST = 23 * 60 + 45;
  const state = {};
  const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  }[char]));
  const dayLabel = new Intl.DateTimeFormat("cs-CZ", { weekday: "long", day: "numeric", month: "numeric", year: "numeric", timeZone: "UTC" });
  const pad = (value) => String(value).padStart(2, "0");
  const hm = (minutes) => `${pad(Math.floor(minutes / 60))}:${pad(minutes % 60)}`;
  const field = (prefix, id) => document.querySelector(`#${prefix}-${id}`);

  function toMinutes(value) {
    const match = /^(\d{1,2}):(\d{2})/.exec(value || "");
    return match ? Number(match[1]) * 60 + Number(match[2]) : null;
  }

  function shiftIso(iso, days) {
    const date = new Date(`${iso}T00:00:00Z`);
    date.setUTCDate(date.getUTCDate() + days);
    return date.toISOString().slice(0, 10);
  }

  function todayIso() {
    const now = new Date();
    return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
  }

  // "2026-11-06T06:00:00" -> minuty od začátku dne day (může být záporné nebo přes 1440).
  function minutesFrom(day, value) {
    const [date, time = "00:00"] = String(value).split("T");
    const days = Math.round((Date.parse(`${date}T00:00:00Z`) - Date.parse(`${day}T00:00:00Z`)) / 86400000);
    return days * 1440 + (toMinutes(time) ?? 0);
  }

  function ownId(prefix) {
    try {
      // orderDetail je z dispecink-orders.js.
      return prefix === "edit" ? orderDetail?.order?.id ?? null : null;
    } catch {
      return null;
    }
  }

  // Výběr z formuláře, oříznutý na zobrazený den.
  function formPick(prefix, day) {
    const driver = field(prefix, "driver")?.value || "";
    const fromDate = field(prefix, "loading-date")?.value || "";
    const toDate = field(prefix, "unloading-date")?.value || fromDate;
    if (!driver || !fromDate) return null;
    const start = minutesFrom(day, `${fromDate}T${field(prefix, "loading-time")?.value || "06:00"}`);
    const end = minutesFrom(day, `${toDate}T${field(prefix, "unloading-time")?.value || "14:00"}`);
    if (end <= 0 || start >= 1440 || end <= start) return null;
    return { driver, start: Math.max(start, 0), end: Math.min(end, 1440), rawStart: start, rawEnd: end };
  }

  function busyOf(prefix, day) {
    const own = ownId(prefix);
    return (state[prefix]?.data?.events ?? [])
      .filter((event) => event.id !== own)
      .map((event) => ({ event, start: minutesFrom(day, event.start), end: minutesFrom(day, event.end) }))
      .filter((item) => item.end > 0 && item.start < 1440);
  }

  function clashes(prefix, day, pick) {
    if (!pick) return [];
    return busyOf(prefix, day).filter((item) =>
      item.event.driverId === pick.driver && item.start < pick.rawEnd && item.end > pick.rawStart);
  }

  async function load(prefix) {
    const current = state[prefix];
    const sequence = ++current.sequence;
    current.message = "Načítám kalendář…";
    render(prefix);
    let body = null;
    let error = "";
    try {
      const response = await fetch(`/api/dispatch/calendar/events?from=${current.day}&days=1`, { cache: "no-store" });
      if (response.status === 401) {
        location.href = "/login.html";
        return;
      }
      body = await response.json().catch(() => null);
      if (!response.ok) error = body?.message || "Kalendář se nepodařilo načíst.";
    } catch {
      error = "Kalendář se nepodařilo načíst.";
    }
    if (sequence !== current.sequence) return;
    current.data = error ? null : body;
    current.message = error;
    render(prefix);
  }

  function render(prefix) {
    const panel = field(prefix, "booking");
    const current = state[prefix];
    if (!panel || panel.hidden || !current) return;
    const day = current.day;
    const drivers = current.data?.drivers ?? [];
    const pick = formPick(prefix, day);
    const clash = clashes(prefix, day, pick);
    const busy = busyOf(prefix, day);
    const hours = Array.from({ length: 24 }, (_, hour) => `<span style="width:${HOUR}px">${hour}</span>`).join("");
    const rows = drivers.map((driver) => {
      const blocks = busy.filter((item) => item.event.driverId === driver.id).map((item) => {
        const left = Math.max(item.start, 0) / 60 * HOUR;
        const width = Math.max((Math.min(item.end, 1440) - Math.max(item.start, 0)) / 60 * HOUR, 6);
        const isClash = clash.some((other) => other.event.id === item.event.id);
        return `<div class="bk-busy${isClash ? " clash" : ""}" style="left:${left}px;width:${width}px">${esc(item.event.code)}</div>`;
      }).join("");
      const mine = pick && pick.driver === driver.id
        ? `<div class="bk-pick" style="left:${pick.start / 60 * HOUR}px;width:${Math.max((pick.end - pick.start) / 60 * HOUR, 6)}px">${esc(hm(Math.min(pick.start, LAST)))}–${esc(pick.end >= 1440 ? "24:00" : hm(pick.end))}</div>`
        : "";
      const pending = current.pending?.driver === driver.id
        ? `<div class="bk-start" style="left:${current.pending.start / 60 * HOUR}px"><span>od ${esc(hm(current.pending.start))}</span></div>`
        : "";
      return `<div class="bk-row${pick?.driver === driver.id ? " chosen" : ""}${pending ? " pending" : ""}">
        <div class="bk-name">${esc(driver.name)}</div>
        <div class="bk-track" data-bk-track="${prefix}" data-driver="${esc(driver.id)}" style="width:${24 * HOUR}px;--bk-hour:${HOUR}px">${blocks}${mine}${pending}</div>
      </div>`;
    }).join("");
    let note = current.message;
    if (!note && current.pending) {
      const name = drivers.find((item) => item.id === current.pending.driver)?.name || "";
      note = `Začátek ${hm(current.pending.start)} u řidiče ${name}. Teď klepněte na konec ve stejném řádku.`;
    } else if (!note && clash.length)
      note = `Pozor: řidič má v tu dobu i zakázku ${[...new Set(clash.map((item) => item.event.code))].join(", ")}.`;
    else if (!note && current.applied)
      note = current.applied;
    panel.innerHTML = `
      <div class="bk-tools">
        <button type="button" class="ghost" data-bk-shift="${prefix}" data-days="-1" aria-label="Předchozí den">‹</button>
        <strong>${esc(dayLabel.format(new Date(`${day}T00:00:00Z`)))}</strong>
        <button type="button" class="ghost" data-bk-shift="${prefix}" data-days="1" aria-label="Další den">›</button>
        <span class="note">Klepněte u řidiče na začátek a pak na konec (myší můžete i táhnout). Šedé jsou jiné zakázky.</span>
      </div>
      <p class="note bk-note${clash.length && !current.pending ? " warn" : ""}">${esc(note)}${current.pending ? ` <button type="button" class="ghost bk-cancel" data-bk-cancel="${prefix}">Zrušit</button>` : ""}</p>
      <div class="bk-scroll" data-bk-scroll="${prefix}">
        <div class="bk-grid">
          <div class="bk-row bk-head"><div class="bk-name"></div><div class="bk-hours">${hours}</div></div>
          ${rows || `<div class="bk-row"><div class="bk-name">V dispečinku zatím nejsou řidiči.</div></div>`}
        </div>
      </div>`;
    const scroller = panel.querySelector(".bk-scroll");
    if (current.scroll == null) current.scroll = 5 * HOUR;
    scroller.scrollLeft = current.scroll;
    scroller.addEventListener("scroll", () => { current.scroll = scroller.scrollLeft; });
  }

  function apply(prefix, driverId, start, end) {
    const driver = field(prefix, "driver");
    if (!driver || driver.disabled) return;
    const current = state[prefix];
    current.pending = null;
    const from = Math.min(Math.max(start, 0), LAST);
    const to = Math.min(Math.max(end, from + SNAP), LAST);
    const set = (id, value) => {
      const input = field(prefix, id);
      if (!input) return;
      input.value = value;
      input.dispatchEvent(new Event("change", { bubbles: true }));
    };
    set("loading-date", current.day);
    set("unloading-date", current.day);
    set("loading-time", hm(from));
    set("unloading-time", hm(to));
    driver.value = driverId;
    driver.dispatchEvent(new Event("change", { bubbles: true }));
    const name = (current.data?.drivers ?? []).find((item) => item.id === driverId)?.name || "";
    current.applied = `Vybráno: ${name}, ${hm(from)}–${hm(to)}. Údaje jsou ve formuláři, po uložení zakázky se čas objeví v kalendáři.`;
    render(prefix);
  }

  function minuteAt(track, clientX) {
    const rect = track.getBoundingClientRect();
    const raw = (clientX - rect.left) / rect.width * 1440;
    return Math.min(Math.max(Math.round(raw / SNAP) * SNAP, 0), 1440);
  }

  let drag = null;

  document.addEventListener("pointerdown", (event) => {
    const track = event.target.closest("[data-bk-track]");
    if (!track || event.button > 0) return;
    const start = minuteAt(track, event.clientX);
    drag = {
      track, prefix: track.dataset.bkTrack, driver: track.dataset.driver, start, end: start,
      touch: event.pointerType === "touch", x: event.clientX, y: event.clientY, ghost: null
    };
    if (!drag.touch) {
      event.preventDefault();
      track.setPointerCapture?.(event.pointerId);
    }
  });

  document.addEventListener("pointermove", (event) => {
    if (!drag || drag.touch) return;
    drag.end = minuteAt(drag.track, event.clientX);
    if (!drag.ghost) {
      drag.ghost = document.createElement("div");
      drag.ghost.className = "bk-pick bk-ghost";
      drag.track.append(drag.ghost);
    }
    const from = Math.min(drag.start, drag.end);
    const to = Math.max(drag.start, drag.end);
    drag.ghost.style.left = `${from / 60 * HOUR}px`;
    drag.ghost.style.width = `${Math.max((to - from) / 60 * HOUR, 2)}px`;
    drag.ghost.textContent = `${hm(Math.min(from, LAST))}–${to >= 1440 ? "24:00" : hm(to)}`;
  });

  document.addEventListener("pointerup", (event) => {
    if (!drag) return;
    const { prefix, driver, start, touch } = drag;
    const moved = Math.abs(event.clientX - drag.x) + Math.abs(event.clientY - drag.y);
    let end = touch ? start : minuteAt(drag.track, event.clientX);
    drag.ghost?.remove();
    drag = null;
    // Na mobilu je posun prstem posouvání, ne výběr.
    if (touch && moved > 12) return;
    const from = Math.min(start, end);
    const to = Math.max(start, end);
    // Klepnutí (mobil) nebo kliknutí bez tažení: první určí začátek, druhé ve stejném řádku konec.
    if (to - from < SNAP) {
      tapAt(prefix, driver, start);
      return;
    }
    apply(prefix, driver, from, to);
  });

  function tapAt(prefix, driver, minute) {
    const current = state[prefix];
    if (!current) return;
    const pending = current.pending;
    if (pending && pending.driver === driver && minute > pending.start) {
      apply(prefix, driver, pending.start, minute);
      return;
    }
    current.pending = { driver, start: Math.min(minute, LAST - SNAP) };
    current.applied = "";
    render(prefix);
  }

  document.addEventListener("pointercancel", () => {
    drag?.ghost?.remove();
    drag = null;
  });

  document.addEventListener("click", (event) => {
    const toggle = event.target.closest("[data-toggle-booking]");
    if (toggle) {
      const prefix = toggle.dataset.toggleBooking;
      const panel = field(prefix, "booking");
      if (!panel) return;
      panel.hidden = !panel.hidden;
      toggle.textContent = panel.hidden ? "Naplánovat čas řidiče" : "Skrýt plánování";
      if (panel.hidden) return;
      state[prefix] = { day: field(prefix, "loading-date")?.value || todayIso(), data: null, sequence: 0, message: "", applied: "", scroll: state[prefix]?.scroll ?? null };
      load(prefix);
      return;
    }
    const shift = event.target.closest("[data-bk-shift]");
    if (shift) {
      const prefix = shift.dataset.bkShift;
      if (!state[prefix]) return;
      state[prefix].day = shiftIso(state[prefix].day, Number(shift.dataset.days));
      state[prefix].applied = "";
      state[prefix].pending = null;
      load(prefix);
      return;
    }
    const cancel = event.target.closest("[data-bk-cancel]");
    if (cancel && state[cancel.dataset.bkCancel]) {
      state[cancel.dataset.bkCancel].pending = null;
      render(cancel.dataset.bkCancel);
    }
  });

  // Změna data, času nebo řidiče ve formuláři se hned ukáže v plánování.
  document.addEventListener("change", (event) => {
    const match = /^(create|edit)-(loading-date|unloading-date|loading-time|unloading-time|driver)$/.exec(event.target.id || "");
    if (!match) return;
    const prefix = match[1];
    const current = state[prefix];
    const panel = field(prefix, "booking");
    if (!current || !panel || panel.hidden) return;
    if (match[2] === "loading-date" && event.target.value && event.target.value !== current.day) {
      current.day = event.target.value;
      load(prefix);
      return;
    }
    render(prefix);
  });
})();
