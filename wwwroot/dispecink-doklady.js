// Doklady k zakázce: focení z mobilu a přikládání souborů.
// Soubory ukládá server do složky zakázky, kterou synchronizuje OneDrive.
(() => {
  const esc = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
  }[char]));
  const when = new Intl.DateTimeFormat("cs-CZ", { dateStyle: "short", timeStyle: "short" });
  const maxSide = 2400;
  const shrinkAbove = 1.5 * 1024 * 1024;
  let current = null;
  let uploading = false;

  const message = document.querySelector("#doc-message");
  const list = document.querySelector("#doc-list");
  const buttons = [document.querySelector("#doc-camera-button"), document.querySelector("#doc-file-button")];

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

  function render(documents) {
    list.innerHTML = (documents ?? []).map((doc) => {
      const url = fileUrl(doc.name);
      const preview = doc.isImage && !/\.(heic|heif)$/i.test(doc.name)
        ? `<img src="${esc(url)}" alt="" loading="lazy">`
        : `<span class="doc-pdf">${esc((doc.name.split(".").pop() || "").toUpperCase())}</span>`;
      return `<li><a href="${esc(url)}" target="_blank" rel="noopener">${preview}${esc(doc.name)}</a>
        <br><span class="note">${esc(sizeText(doc.size))} · ${esc(when.format(new Date(doc.modifiedAt)))}</span></li>`;
    }).join("");
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
    current = order;
    list.innerHTML = "";
    const cancelled = order.status === "Stornováno";
    buttons.forEach((button) => { button.disabled = cancelled; });
    show("Načítám doklady…");
    const result = await request(`/api/dispatch/orders/${encodeURIComponent(order.id)}/documents`);
    if (current !== order) return;
    if (!result.ok) {
      show(result.data.message || "Doklady se nepodařilo načíst.");
      return;
    }
    if (result.data.configured === false) {
      buttons.forEach((button) => { button.disabled = true; });
      show(result.data.message);
      return;
    }
    render(result.data.documents);
    show(result.data.documents?.length ? "" : "Zatím bez dokladů.");
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

  function photoName(file) {
    // Fotoaparát v mobilu dává názvy jako image.jpg, doplní se kód zakázky.
    if (!/^(image|photo|img)[-_\d]*\.\w+$/i.test(file.name)) return file.name;
    const extension = (file.name.split(".").pop() || "jpg").toLowerCase();
    return `${current.code}-foto.${extension}`;
  }

  async function upload(files) {
    if (!current || uploading || !files.length) return;
    uploading = true;
    buttons.forEach((button) => { button.disabled = true; });
    show(files.length === 1 ? "Nahrávám doklad…" : `Nahrávám ${files.length} dokladů…`);
    const form = new FormData();
    for (const file of files) {
      const ready = await shrink(file);
      form.append("files", ready, photoName(ready));
    }
    const order = current;
    const result = await request(`/api/dispatch/orders/${encodeURIComponent(order.id)}/documents`, { method: "POST", body: form });
    uploading = false;
    buttons.forEach((button) => { button.disabled = order.status === "Stornováno"; });
    if (current !== order) return;
    if (!result.ok) {
      show(result.data.message || "Doklad se nepodařilo nahrát.");
      return;
    }
    render(result.data.documents);
    const count = result.data.saved?.length ?? 0;
    show(count === 1 ? "Doklad je uložený." : `Uloženo dokladů: ${count}.`);
  }

  for (const [buttonId, inputId] of [["#doc-camera-button", "#doc-camera"], ["#doc-file-button", "#doc-file"]]) {
    const input = document.querySelector(inputId);
    document.querySelector(buttonId).addEventListener("click", () => input.click());
    input.addEventListener("change", async () => {
      const files = [...input.files];
      input.value = "";
      await upload(files);
    });
  }

  window.loadOrderDocuments = load;
})();
