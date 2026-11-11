// Doklady k zakázce po krocích: nakládka (nákladní list), každá vykládka (stáčecí list,
// potvrzení od zákazníka) a ostatní doklady. Soubory ukládá server do složky zakázky,
// kterou synchronizuje OneDrive. Krok se pozná podle názvu souboru (…-vykladka-1-staceci-list.jpg).
(() => {
  const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  }[char]));
  const when = new Intl.DateTimeFormat("cs-CZ", { dateStyle: "short", timeStyle: "short" });
  const maxSide = 2400;
  const shrinkAbove = 1.5 * 1024 * 1024;
  const closedStatuses = ["K fakturaci", "Vyfakturováno", "Stornováno"];
  let current = null;
  let documents = [];
  let configured = true;
  let uploading = false;
  let pendingSlot = null;
  let justFinished = false;

  const message = document.querySelector("#doc-message");
  const list = document.querySelector("#doc-list");
  const steps = document.querySelector("#doc-steps");
  const progress = document.querySelector("#doc-progress");
  const confirmBox = document.querySelector("#doc-confirm");
  const camera = document.querySelector("#doc-camera");
  const dialog = document.querySelector("#docs-dialog");
  const summary = document.querySelector("#order-docs-summary");
  const picker = document.querySelector("#doc-file");

  function show(value) {
    message.hidden = !value;
    message.textContent = value ?? "";
  }

  function sizeText(bytes) {
    const value = Number(bytes || 0);
    if (value >= 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1).replace(".", ",")} MB`;
    return `${Math.max(1, Math.round(value / 1024))} kB`;
  }

  function fileUrl(name) {
    return `/api/dispatch/orders/${encodeURIComponent(current.id)}/documents/${encodeURIComponent(name)}`;
  }

  // Kroky podle zakázky: nakládka a každá vykládka.
  function slotsFor(order) {
    const groups = [{
      title: "Doklady k nakládce",
      place: order.loadingPlace || "",
      slots: [{ key: "nakladka-nakladni-list", label: "Nákladní list" }]
    }];
    const stops = order.stops?.length ? order.stops : [{ place: order.unloadingPlace || "" }];
    stops.forEach((stop, index) => {
      const number = index + 1;
      groups.push({
        title: `Doklady k vykládce ${number}`,
        place: stop.place || "",
        slots: [
          { key: `vykladka-${number}-staceci-list`, label: "Stáčecí list" },
          { key: `vykladka-${number}-potvrzeni`, label: "Potvrzení od zákazníka" }
        ]
      });
    });
    return groups;
  }

  function slotOf(name) {
    return /^\d{8}-\d{6}-(nakladka-nakladni-list|vykladka-\d+-(?:staceci-list|potvrzeni))(?:-\d+)?\.[a-z0-9]+$/i.exec(name)?.[1]?.toLowerCase() || null;
  }

  function preview(doc) {
    const url = fileUrl(doc.name);
    return doc.isImage && !/\.(heic|heif)$/i.test(doc.name)
      ? `<img src="${esc(url)}" alt="" loading="lazy">`
      : `<span class="doc-pdf">${esc((doc.name.split(".").pop() || "").toUpperCase())}</span>`;
  }

  function card(doc) {
    return `<li><a href="${esc(fileUrl(doc.name))}" target="_blank" rel="noopener">${preview(doc)}${esc(doc.name)}</a>
      <br><span class="note">${esc(sizeText(doc.size))} · ${esc(when.format(new Date(doc.modifiedAt)))}</span></li>`;
  }

  function canAdd() {
    return configured && current && current.status !== "Stornováno" && !uploading;
  }

  function render() {
    if (!current) return;
    const groups = slotsFor(current);
    const bySlot = new Map();
    const others = [];
    for (const doc of documents) {
      const slot = slotOf(doc.name);
      if (slot) bySlot.set(slot, [...(bySlot.get(slot) ?? []), doc]);
      else others.push(doc);
    }
    const all = groups.flatMap((group) => group.slots);
    const done = all.filter((slot) => bySlot.has(slot.key)).length;
    const next = all.find((slot) => !bySlot.has(slot.key))?.key;
    const enabled = canAdd();

    progress.hidden = !configured;
    progress.innerHTML = `Nahráno <strong>${done} z ${all.length}</strong> povinných dokladů`
      + `<span class="doc-bar" aria-hidden="true"><i style="width:${all.length ? Math.round(done / all.length * 100) : 0}%"></i></span>`;

    let step = 0;
    steps.innerHTML = groups.map((group, groupIndex) => {
      const letter = String.fromCharCode(97 + groupIndex);
      const rows = group.slots.map((slot, slotIndex) => {
        step += 1;
        const files = bySlot.get(slot.key) ?? [];
        const isDone = files.length > 0;
        return `<div class="doc-step${isDone ? " done" : ""}${slot.key === next ? " next" : ""}">
          <div class="doc-step-head">
            <span class="doc-check" aria-hidden="true">${isDone ? "✓" : step}</span>
            <strong>${esc(`${letter}${slotIndex + 1}) ${isDone ? "" : "Vyfoť "}${isDone ? slot.label : slot.label.toLowerCase()}`)}</strong>
            ${isDone ? `<span class="note">nahráno ${files.length}×</span>` : slot.key === next ? `<span class="pill">další krok</span>` : ""}
          </div>
          <div class="doc-step-actions">
            <button type="button" data-doc-slot="${slot.key}" data-doc-mode="camera"${enabled ? "" : " disabled"}${isDone ? ` class="ghost"` : ""}>${isDone ? "Přidat další foto" : "Vyfotit"}</button>
            <button type="button" class="ghost" data-doc-slot="${slot.key}" data-doc-mode="file"${enabled ? "" : " disabled"}>Soubor</button>
          </div>
          ${files.length ? `<ul class="doc-list">${files.map(card).join("")}</ul>` : ""}
        </div>`;
      }).join("");
      const tone = groupIndex === 0 ? "load" : groupIndex % 2 === 1 ? "unload-a" : "unload-b";
      return `<section class="doc-group ${tone}">
        <h4>${esc(`${letter}) ${group.title}`)}${group.place ? ` <span class="note">${esc(group.place)}</span>` : ""}</h4>
        ${rows}
      </section>`;
    }).join("");

    list.innerHTML = others.map(card).join("");
    document.querySelector("#doc-others-empty").hidden = others.length > 0;
    for (const id of ["#doc-camera-button", "#doc-file-button"])
      document.querySelector(id).disabled = !enabled;

    const complete = all.length > 0 && done === all.length;
    if (complete && current.status === "K fakturaci") {
      confirmBox.hidden = false;
      confirmBox.className = "doc-confirm ok";
      confirmBox.innerHTML = `<strong>Všechny doklady jsou nahrané.</strong> Zakázka je potvrzená k fakturaci.`;
    } else if (complete && !closedStatuses.includes(current.status) && !current.isLocked) {
      confirmBox.hidden = false;
      confirmBox.className = "doc-confirm";
      confirmBox.innerHTML = `<strong>Všechny doklady jsou nahrané.</strong>
        <button type="button" id="doc-confirm-button">Potvrdit k fakturaci</button>`;
      if (justFinished) confirmBox.scrollIntoView({ behavior: "smooth", block: "center" });
    } else {
      confirmBox.hidden = true;
      confirmBox.innerHTML = "";
    }
    justFinished = false;
    renderSummary(all, bySlot, complete);
  }

  // Přehled dokladů v detailu zakázky, celá agenda je v okně Doklady.
  function renderSummary(all, bySlot, complete) {
    document.querySelector("#docs-dialog-title").textContent = `Doklady · ${current.code}`;
    const text = document.querySelector("#doc-summary-text");
    const missingNote = document.querySelector("#doc-summary-missing");
    summary.classList.toggle("complete", configured && complete);
    if (!configured) {
      text.textContent = "Složka pro doklady není nastavená.";
      missingNote.textContent = "";
      return;
    }
    const done = all.filter((slot) => bySlot.has(slot.key)).length;
    text.textContent = `Nahráno ${done} z ${all.length} povinných`;
    const missing = window.missingOrderDocuments() ?? [];
    // Krátce: do dvou dokladů celý výčet, jinak počet a kde chybí.
    const places = [...new Set(missing.map((item) => item.split(":")[0]))];
    missingNote.textContent = missing.length
      ? missing.length <= 2 ? `Chybí: ${missing.join(", ")}.` : `Chybí ${missing.length} dokladů: ${places.join(", ")}.`
      : closedStatuses.includes(current.status) ? "Všechny doklady jsou nahrané." : "Všechny doklady jsou nahrané, zakázku můžete potvrdit k fakturaci.";
  }

  async function request(url, options) {
    let response;
    try {
      response = await fetch(url, { cache: "no-store", ...options });
    } catch {
      return { ok: false, data: { message: "Spojení se nepodařilo." } };
    }
    if (response.status === 401) {
      location.href = "/login.html";
      return { ok: false, data: {} };
    }
    const data = await response.json().catch(() => ({}));
    return { ok: response.ok, data };
  }

  async function load(order) {
    if (current?.id !== order.id && dialog.open) dialog.close();
    current = order;
    documents = [];
    configured = true;
    steps.innerHTML = "";
    list.innerHTML = "";
    confirmBox.hidden = true;
    show("Načítám doklady…");
    const result = await request(`/api/dispatch/orders/${encodeURIComponent(order.id)}/documents`);
    if (current !== order) return;
    if (!result.ok) {
      show(result.data.message || "Doklady se nepodařilo načíst.");
      render();
      return;
    }
    if (result.data.configured === false) {
      configured = false;
      show(result.data.message);
      render();
      return;
    }
    documents = result.data.documents ?? [];
    show("");
    render();
  }

  // Velkou fotku z mobilu zmenší na nejvýš 2400 px, ať se rychle nahraje.
  async function shrink(file) {
    if (!/^image\/(jpeg|png|webp)$/.test(file.type) || file.size < shrinkAbove || typeof createImageBitmap !== "function")
      return file;
    try {
      const bitmap = await createImageBitmap(file);
      const scale = Math.min(1, maxSide / Math.max(bitmap.width, bitmap.height));
      const canvas = document.createElement("canvas");
      canvas.width = Math.round(bitmap.width * scale);
      canvas.height = Math.round(bitmap.height * scale);
      canvas.getContext("2d").drawImage(bitmap, 0, 0, canvas.width, canvas.height);
      bitmap.close?.();
      const blob = await new Promise((resolve) => canvas.toBlob(resolve, "image/jpeg", 0.85));
      if (!blob || blob.size >= file.size) return file;
      const name = file.name.replace(/\.[^.]+$/, "") + ".jpg";
      return new File([blob], name, { type: "image/jpeg" });
    } catch {
      return file;
    }
  }

  function extensionOf(file) {
    const fromName = /\.([a-z0-9]+)$/i.exec(file.name)?.[1]?.toLowerCase();
    if (fromName) return fromName;
    return { "image/jpeg": "jpg", "image/png": "png", "image/webp": "webp", "application/pdf": "pdf" }[file.type] || "jpg";
  }

  function uploadName(file, slot) {
    if (slot) return `${slot}.${extensionOf(file)}`;
    // Fotoaparát v mobilu dává názvy jako image.jpg, doplní se kód zakázky.
    if (!/^(image|photo|img)[-_\d]*\.\w+$/i.test(file.name)) return file.name;
    return `${current.code}-foto.${extensionOf(file)}`;
  }

  async function upload(files, slot) {
    if (!current || uploading || !files.length) return;
    const order = current;
    const before = slotsFor(order).flatMap((group) => group.slots).filter((item) => !documents.some((doc) => slotOf(doc.name) === item.key)).length;
    uploading = true;
    render();
    show(files.length === 1 ? "Nahrávám doklad…" : `Nahrávám ${files.length} dokladů…`);
    const form = new FormData();
    for (const file of files) {
      const ready = await shrink(file);
      form.append("files", ready, uploadName(ready, slot));
    }
    const result = await request(`/api/dispatch/orders/${encodeURIComponent(order.id)}/documents`, { method: "POST", body: form });
    uploading = false;
    if (current !== order) return;
    if (!result.ok) {
      show(result.data.message || "Doklad se nepodařilo nahrát.");
      render();
      return;
    }
    documents = result.data.documents ?? documents;
    const missing = slotsFor(order).flatMap((group) => group.slots).filter((item) => !documents.some((doc) => slotOf(doc.name) === item.key)).length;
    justFinished = before > 0 && missing === 0;
    const count = result.data.saved?.length ?? 0;
    show(count === 1 ? "Doklad je uložený." : `Uloženo dokladů: ${count}.`);
    render();
  }

  function pick(slot, mode) {
    if (!canAdd()) return;
    pendingSlot = slot;
    (mode === "file" ? picker : camera).click();
  }

  document.querySelector("#order-docs").addEventListener("click", (event) => {
    const step = event.target.closest("[data-doc-slot]");
    if (step) {
      pick(step.dataset.docSlot, step.dataset.docMode);
      return;
    }
    if (event.target.closest("#doc-camera-button")) pick(null, "camera");
    else if (event.target.closest("#doc-file-button")) pick(null, "file");
    else if (event.target.closest("#doc-confirm-button")) {
      dialog.close();
      window.confirmOrderToInvoice?.(current.id);
    }
  });

  // Okno Doklady se otevírá z přehledu v detailu a z okna Chybí doklady.
  document.addEventListener("click", (event) => {
    if (!event.target.closest("[data-open-docs]") || !current) return;
    if (!dialog.open) dialog.showModal();
  });

  for (const input of [camera, picker]) {
    input.addEventListener("change", async () => {
      const files = [...input.files];
      const slot = pendingSlot;
      input.value = "";
      pendingSlot = null;
      await upload(files, slot);
    });
  }

  window.loadOrderDocuments = load;
  // Otevře okno dokladů pro zakázku (pohled řidiče).
  window.openOrderDocuments = async (order) => {
    await load(order);
    if (!dialog.open) dialog.showModal();
  };
  // Chybějící povinné doklady otevřené zakázky (popisky). null = nejde zjistit (složka není nastavená).
  window.missingOrderDocuments = (id) => {
    if (!current || !configured || (id && current.id !== id)) return null;
    return slotsFor(current).flatMap((group, groupIndex) => group.slots
      .filter((slot) => !documents.some((doc) => slotOf(doc.name) === slot.key))
      .map((slot) => `${groupIndex === 0 ? "Nakládka" : `Vykládka ${groupIndex}`}${group.place ? ` (${group.place})` : ""}: ${slot.label.toLowerCase()}`));
  };
})();
