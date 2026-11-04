const text = (value) => String(value ?? "").replace(/[&<>"']/g, (char) => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;"
}[char]));
const dateTime = new Intl.DateTimeFormat("cs-CZ", { dateStyle: "short", timeStyle: "short" });
const money = new Intl.NumberFormat("cs-CZ", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const message = document.querySelector("#message");
const customerRows = document.querySelector("#customer-rows");
const contactRows = document.querySelector("#contact-rows");
const opportunityRows = document.querySelector("#opportunity-rows");
const activityRows = document.querySelector("#activity-rows");
let requestSeq = 0;
let saving = false;
let customers = [];
let contacts = [];
let opportunities = [];
let activities = [];
let catalogCustomers = [];
let catalogContacts = [];
let catalogOpportunities = [];
let catalogActivities = [];
let kindFilter;
let contactFilter;
let stageFilter;
let activityFilter;

function showMessage(value) {
  message.hidden = !value;
  message.textContent = value ?? "";
}

function pad(value) {
  return String(value).padStart(2, "0");
}

function formatDate(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "" : dateTime.format(date);
}

function formatDay(value) {
  if (!value) return "—";
  const match = String(value).match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (!match) return "—";
  return `${Number(match[3])}. ${Number(match[2])}. ${match[1]}`;
}

function formatMoney(value) {
  if (value === null || value === undefined || value === "") return "—";
  const number = Number(value);
  return Number.isNaN(number) ? "—" : money.format(number);
}

function defaultWhen() {
  const date = new Date();
  date.setSeconds(0, 0);
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function toLocalInput(value) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function toIso(value) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

function readDay(value) {
  const trimmed = String(value ?? "").trim();
  return trimmed || null;
}

function readAmount(value) {
  const trimmed = String(value ?? "").trim().replace(/\s/g, "").replace(",", ".");
  if (!trimmed) return { ok: true, amount: null };
  const amount = Number(trimmed);
  if (!Number.isFinite(amount)) return { ok: false, message: "Částka není číslo." };
  if (amount < 0) return { ok: false, message: "Částka nesmí být záporná." };
  return { ok: true, amount };
}

function options(items, selected, emptyLabel, labelOf, titleOf) {
  const head = `<option value="">${text(emptyLabel)}</option>`;
  const known = items.some((item) => item.id === selected);
  const extra = selected && !known
    ? `<option value="${text(selected)}" selected>${text(selected)}</option>`
    : "";
  const body = items.map((item) => {
    const title = titleOf ? titleOf(item) : "";
    const selectedAttr = item.id === selected ? " selected" : "";
    const titleAttr = title ? ` title="${text(title)}"` : "";
    return `<option value="${text(item.id)}"${selectedAttr}${titleAttr}>${text(labelOf(item))}</option>`;
  }).join("");
  return head + extra + body;
}

function choiceOptions(values, selected) {
  return values.map((value) => `<option${value === selected ? " selected" : ""}>${text(value)}</option>`).join("");
}

function customerLabel(item) {
  return `${item.name} (${item.code})`;
}

function opportunityLabel(item) {
  return `${item.code} · ${item.title}`;
}

function customerName(id) {
  const item = catalogCustomers.find((customer) => customer.id === id);
  return item ? item.name : (id || "—");
}

function opportunityText(id) {
  const item = catalogOpportunities.find((opportunity) => opportunity.id === id);
  return item ? opportunityLabel(item) : (id || "—");
}

function contactsLabel(count) {
  if (count === 1) return "1 kontakt";
  if (count >= 2 && count <= 4) return `${count} kontakty`;
  return `${count} kontaktů`;
}

function contactLine(item) {
  const parts = [item.email, item.phone].filter(Boolean);
  return parts.length ? parts.join(" · ") : "—";
}

function addressText(item) {
  const cityLine = [item.zip, item.city].filter(Boolean).join(" ");
  const line = [item.street, cityLine].filter(Boolean).join(", ");
  return line || "—";
}

function stagePill(stage) {
  const tone = stage === "Vyhráno" ? " ok" : stage === "Prohráno" ? " warn" : "";
  return `<span class="pill${tone}">${text(stage)}</span>`;
}

function countStage(data, stage) {
  const found = (data.counts?.stages ?? []).find((item) => item.stage === stage);
  return found ? found.count : 0;
}

function countKind(data, kind) {
  const found = (data.counts?.activityKinds ?? []).find((item) => item.kind === kind);
  return found ? found.count : 0;
}

function showTab(name) {
  for (const panel of document.querySelectorAll("[data-panel]"))
    panel.hidden = panel.dataset.panel !== name;
  for (const button of document.querySelectorAll("[data-tab]"))
    button.setAttribute("aria-current", button.dataset.tab === name ? "page" : "false");
}

function openCard(card) {
  if ("stage" in card.dataset) stageFilter.setValues(card.dataset.stage || "*");
  if ("activityKind" in card.dataset) activityFilter.setValues(card.dataset.activityKind || "*");
  showTab(card.dataset.openTab);
}

function setOptions(selector, items, selected, emptyLabel, labelOf) {
  document.querySelector(selector).innerHTML = options(items, selected, emptyLabel, labelOf);
}

function fillSelects() {
  const contactCustomer = document.querySelector("#contact-customer").value;
  const oppCustomer = document.querySelector("#opp-customer").value;
  const actCustomer = document.querySelector("#act-customer").value;
  const actOpportunity = document.querySelector("#act-opportunity").value;
  setOptions("#contact-customer", catalogCustomers, contactCustomer, "— vyberte zákazníka —", customerLabel);
  setOptions("#opp-customer", catalogCustomers, oppCustomer, "— vyberte zákazníka —", customerLabel);
  setOptions("#act-customer", catalogCustomers, actCustomer, "— bez zákazníka —", customerLabel);
  setOptions("#act-opportunity", catalogOpportunities, actOpportunity, "— bez příležitosti —", opportunityLabel);
  if (contactFilter.setChoices(catalogCustomers.map((item) => ({ value: item.id, label: customerLabel(item) }))))
    load();
}

function applyCustomerFilter() {
  const empty = document.querySelector("#customer-empty");
  empty.hidden = customers.length > 0;
  empty.textContent = kindFilter.isAll() ? "Zatím není žádný zákazník." : "Tomuto výběru neodpovídá žádný zákazník.";
}

function applyContactFilter() {
  const empty = document.querySelector("#contact-empty");
  empty.hidden = contacts.length > 0;
  empty.textContent = contactFilter.isAll() ? "Zatím není žádný kontakt." : "Tomuto výběru neodpovídá žádný kontakt.";
}

function applyOpportunityFilter() {
  const empty = document.querySelector("#opportunity-empty");
  empty.hidden = opportunities.length > 0;
  empty.textContent = stageFilter.isAll() ? "Zatím není žádná příležitost." : "Tomuto výběru neodpovídá žádná příležitost.";
}

function applyActivityFilter() {
  const empty = document.querySelector("#activity-empty");
  empty.hidden = activities.length > 0;
  empty.textContent = activityFilter.isAll() ? "Zatím není žádná aktivita." : "Tomuto výběru neodpovídá žádná aktivita.";
}

function actions(entity, id) {
  return `<div class="edit-actions">
      <button type="button" data-save="${text(id)}" data-entity="${entity}">Uložit změny</button>
      <button type="button" class="ghost" data-cancel="${text(id)}" data-entity="${entity}">Zrušit</button>
    </div>`;
}

function customerRow(item) {
  const people = catalogContacts.filter((contact) => contact.customerId === item.id).length;
  const note = item.note ? `<br><span class="route-to">${text(item.note)}</span>` : "";
  const country = item.country ? `<br><span class="route-to">${text(item.country)}</span>` : "";
  return `<tr data-id="${text(item.id)}" data-kind="${text(item.kind)}">
      <td>${text(item.code)}</td>
      <td><span class="pill">${text(item.kind)}</span></td>
      <td>${text(item.name)}${note}</td>
      <td>${text(contactLine(item))}<br><span class="route-to">${text(contactsLabel(people))}</span></td>
      <td>${text(addressText(item))}${country}</td>
      <td>${text(item.ico || "—")}</td>
      <td><button type="button" data-edit="${text(item.id)}" data-entity="customer">Upravit</button></td>
    </tr>`;
}

function customerEdit(item) {
  return `<td>${text(item.code)}</td>
      <td><select data-field="kind" aria-label="Druh ${text(item.code)}">${choiceOptions(["Firma", "Osoba"], item.kind)}</select></td>
      <td><div class="stack">
        <input data-field="name" required maxlength="160" aria-label="Název ${text(item.code)}" value="${text(item.name)}">
        <input data-field="note" maxlength="500" placeholder="Poznámka" aria-label="Poznámka ${text(item.code)}" value="${text(item.note || "")}">
      </div></td>
      <td><div class="stack">
        <input data-field="email" type="email" maxlength="160" placeholder="E-mail" aria-label="E-mail ${text(item.code)}" value="${text(item.email || "")}">
        <input data-field="phone" type="tel" maxlength="40" placeholder="Telefon" aria-label="Telefon ${text(item.code)}" value="${text(item.phone || "")}">
      </div></td>
      <td><div class="stack">
        <input data-field="street" maxlength="160" placeholder="Ulice" aria-label="Ulice ${text(item.code)}" value="${text(item.street || "")}">
        <input data-field="city" maxlength="80" placeholder="Město" aria-label="Město ${text(item.code)}" value="${text(item.city || "")}">
        <input data-field="zip" maxlength="16" placeholder="PSČ" aria-label="PSČ ${text(item.code)}" value="${text(item.zip || "")}">
        <input data-field="country" maxlength="80" placeholder="Stát" aria-label="Stát ${text(item.code)}" value="${text(item.country || "")}">
      </div></td>
      <td><input data-field="ico" maxlength="16" aria-label="IČO ${text(item.code)}" value="${text(item.ico || "")}"></td>
      <td>${actions("customer", item.id)}</td>`;
}

function contactRow(item) {
  return `<tr data-id="${text(item.id)}" data-customer="${text(item.customerId)}">
      <td>${text(item.name)}</td>
      <td>${text(item.role || "—")}</td>
      <td>${text(item.email || "—")}</td>
      <td>${text(item.phone || "—")}</td>
      <td>${text(item.note || "—")}</td>
      <td><button type="button" data-edit="${text(item.id)}" data-entity="contact">Upravit</button></td>
    </tr>`;
}

function contactEdit(item) {
  return `<td><input data-field="name" required maxlength="160" aria-label="Jméno kontaktu" value="${text(item.name)}"></td>
      <td><input data-field="role" maxlength="80" aria-label="Funkce" value="${text(item.role || "")}"></td>
      <td><input data-field="email" type="email" maxlength="160" aria-label="E-mail kontaktu" value="${text(item.email || "")}"></td>
      <td><input data-field="phone" type="tel" maxlength="40" aria-label="Telefon kontaktu" value="${text(item.phone || "")}"></td>
      <td><div class="stack">
        <input data-field="note" maxlength="500" aria-label="Poznámka kontaktu" value="${text(item.note || "")}">
        <select data-field="customer" aria-label="Zákazník kontaktu">${options(catalogCustomers, item.customerId, "— vyberte zákazníka —", customerLabel)}</select>
      </div></td>
      <td>${actions("contact", item.id)}</td>`;
}

function opportunityRow(item) {
  const note = item.note ? `<br><span class="route-to">${text(item.note)}</span>` : "";
  return `<tr data-id="${text(item.id)}" data-stage="${text(item.stage)}">
      <td>${text(item.code)}</td>
      <td>${text(customerName(item.customerId))}</td>
      <td>${text(item.title)}${note}</td>
      <td class="num">${formatMoney(item.amount)}</td>
      <td>${formatDay(item.expectedClose)}</td>
      <td>${stagePill(item.stage)}</td>
      <td><button type="button" data-edit="${text(item.id)}" data-entity="opportunity">Upravit</button></td>
    </tr>`;
}

function opportunityEdit(item) {
  return `<td>${text(item.code)}</td>
      <td><select data-field="customer" aria-label="Zákazník ${text(item.code)}">${options(catalogCustomers, item.customerId, "— vyberte zákazníka —", customerLabel)}</select></td>
      <td><div class="stack">
        <input data-field="title" required maxlength="160" aria-label="Název ${text(item.code)}" value="${text(item.title)}">
        <input data-field="note" maxlength="500" placeholder="Poznámka" aria-label="Poznámka ${text(item.code)}" value="${text(item.note || "")}">
      </div></td>
      <td><input data-field="amount" type="number" min="0" step="0.01" aria-label="Částka ${text(item.code)}" value="${item.amount ?? ""}"></td>
      <td><input data-field="close" type="date" aria-label="Předpoklad ${text(item.code)}" value="${text(item.expectedClose || "")}"></td>
      <td><select data-field="stage" aria-label="Fáze ${text(item.code)}">${choiceOptions(["Nový", "V jednání", "Nabídka", "Vyhráno", "Prohráno"], item.stage)}</select></td>
      <td>${actions("opportunity", item.id)}</td>`;
}

function activityRow(item) {
  const body = item.body ? `<br><span class="route-to">${text(item.body)}</span>` : "";
  return `<tr data-id="${text(item.id)}" data-kind="${text(item.kind)}">
      <td>${text(item.code)}</td>
      <td>${formatDate(item.occurredAt)}</td>
      <td><span class="pill">${text(item.kind)}</span></td>
      <td>${text(customerName(item.customerId))}</td>
      <td>${text(item.opportunityId ? opportunityText(item.opportunityId) : "—")}</td>
      <td>${text(item.subject)}${body}</td>
      <td><button type="button" data-edit="${text(item.id)}" data-entity="activity">Upravit</button></td>
    </tr>`;
}

function activityEdit(item) {
  return `<td>${text(item.code)}</td>
      <td><input data-field="when" type="datetime-local" required aria-label="Kdy ${text(item.code)}" value="${text(toLocalInput(item.occurredAt))}"></td>
      <td><select data-field="kind" aria-label="Druh ${text(item.code)}">${choiceOptions(["Hovor", "Schůzka", "Poznámka"], item.kind)}</select></td>
      <td><select data-field="customer" aria-label="Zákazník ${text(item.code)}">${options(catalogCustomers, item.customerId, "— bez zákazníka —", customerLabel)}</select></td>
      <td><select data-field="opportunity" aria-label="Příležitost ${text(item.code)}">${options(catalogOpportunities, item.opportunityId, "— bez příležitosti —", opportunityLabel)}</select></td>
      <td><div class="stack">
        <input data-field="subject" required maxlength="160" aria-label="Předmět ${text(item.code)}" value="${text(item.subject)}">
        <textarea data-field="body" maxlength="1000" aria-label="Text ${text(item.code)}">${text(item.body || "")}</textarea>
      </div></td>
      <td>${actions("activity", item.id)}</td>`;
}

function renderCounts(data) {
  document.querySelector("#count-customers").textContent = data.counts?.customers ?? 0;
  document.querySelector("#count-opportunities").textContent = data.counts?.opportunities ?? 0;
  document.querySelector("#count-activities").textContent = data.counts?.activities ?? 0;
  document.querySelector("#count-novy").textContent = countStage(data, "Nový");
  document.querySelector("#count-jednani").textContent = countStage(data, "V jednání");
  document.querySelector("#count-nabidka").textContent = countStage(data, "Nabídka");
  document.querySelector("#count-vyhrano").textContent = countStage(data, "Vyhráno");
  document.querySelector("#count-prohrano").textContent = countStage(data, "Prohráno");
  document.querySelector("#count-hovor").textContent = countKind(data, "Hovor");
  document.querySelector("#count-schuzka").textContent = countKind(data, "Schůzka");
  document.querySelector("#count-poznamka").textContent = countKind(data, "Poznámka");
  document.querySelector("#cards").hidden = false;
  document.querySelector("#kind-cards").hidden = false;
}

function renderRecent() {
  const latestDeals = [...catalogOpportunities].sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt)).slice(0, 8);
  document.querySelector("#recent-opportunity-rows").innerHTML = latestDeals.map((item) => `<tr>
      <td>${text(item.code)}</td>
      <td>${text(customerName(item.customerId))}</td>
      <td>${text(item.title)}</td>
      <td class="num">${formatMoney(item.amount)}</td>
      <td>${stagePill(item.stage)}</td>
    </tr>`).join("");
  document.querySelector("#recent-activity-rows").innerHTML = catalogActivities.slice(0, 8).map((item) => `<tr>
      <td>${formatDate(item.occurredAt)}</td>
      <td>${text(item.kind)}</td>
      <td>${text(customerName(item.customerId))}</td>
      <td>${text(item.subject)}</td>
    </tr>`).join("");
  document.querySelector("#recent-opportunities").hidden = false;
  document.querySelector("#recent-activities").hidden = false;
}

function renderCustomers() {
  customerRows.innerHTML = customers.map(customerRow).join("");
  document.querySelector("#customer-table").hidden = false;
  applyCustomerFilter();
}

function renderContacts() {
  contactRows.innerHTML = contacts.map(contactRow).join("");
  document.querySelector("#contact-table").hidden = false;
  applyContactFilter();
}

function renderOpportunities() {
  opportunityRows.innerHTML = opportunities.map(opportunityRow).join("");
  document.querySelector("#opportunity-table").hidden = false;
  applyOpportunityFilter();
}

function renderActivities() {
  activityRows.innerHTML = activities.map(activityRow).join("");
  document.querySelector("#activity-table").hidden = false;
  applyActivityFilter();
}

function render(data) {
  catalogCustomers = data.catalogCustomers ?? [];
  catalogContacts = data.catalogContacts ?? [];
  catalogOpportunities = data.catalogOpportunities ?? [];
  catalogActivities = data.catalogActivities ?? [];
  customers = data.customers ?? [];
  contacts = data.contacts ?? [];
  opportunities = data.opportunities ?? [];
  activities = data.activities ?? [];
  document.querySelector("#storage").hidden = false;
  document.querySelector("#storage").textContent = data.storageNote || "";
  renderCounts(data);
  renderRecent();
  renderCustomers();
  renderContacts();
  renderOpportunities();
  renderActivities();
  fillSelects();
}

function field(row, name) {
  return row.querySelector(`[data-field="${name}"]`).value;
}

function valueOf(prefix, name) {
  return document.querySelector(`#${prefix}-${name}`).value;
}

function customerPayload(read) {
  return {
    kind: read("kind"),
    name: read("name"),
    ico: read("ico"),
    email: read("email"),
    phone: read("phone"),
    street: read("street"),
    city: read("city"),
    zip: read("zip"),
    country: read("country"),
    note: read("note")
  };
}

function opportunityPayload(read) {
  const amount = readAmount(read("amount"));
  if (!amount.ok) return amount;
  if (!String(read("customer") || "").trim())
    return { ok: false, message: "Vyplňte zákazníka." };
  if (!String(read("title") || "").trim())
    return { ok: false, message: "Vyplňte název příležitosti." };
  return {
    ok: true,
    payload: {
      customerId: read("customer"),
      title: read("title"),
      stage: read("stage"),
      amount: amount.amount,
      expectedClose: readDay(read("close")),
      note: read("note")
    }
  };
}

function activityPayload(read) {
  let customerId = String(read("customer") || "").trim();
  const opportunityId = String(read("opportunity") || "").trim();
  if (!customerId && opportunityId) {
    const deal = catalogOpportunities.find((item) => item.id === opportunityId);
    if (deal) customerId = deal.customerId;
  }
  if (opportunityId && customerId) {
    const deal = catalogOpportunities.find((item) => item.id === opportunityId);
    if (deal && deal.customerId !== customerId)
      return { ok: false, message: "Zákazník aktivity nesedí se zákazníkem příležitosti." };
  }
  if (!customerId && !opportunityId)
    return { ok: false, message: "Aktivita musí být u zákazníka nebo u příležitosti." };
  if (!String(read("subject") || "").trim())
    return { ok: false, message: "Vyplňte předmět." };
  const occurredAt = toIso(read("when"));
  if (!occurredAt)
    return { ok: false, message: "Vyplňte termín." };
  return {
    ok: true,
    payload: {
      kind: read("kind"),
      customerId: customerId || null,
      opportunityId: opportunityId || null,
      subject: read("subject"),
      body: read("body"),
      occurredAt
    }
  };
}

function beginEdit(entity, id) {
  if (saving) return;
  if (entity === "customer") {
    const item = customers.find((customer) => customer.id === id);
    if (!item) return;
    renderCustomers();
    const row = customerRows.querySelector(`tr[data-id="${CSS.escape(id)}"]`);
    if (!row) return;
    row.hidden = false;
    row.innerHTML = customerEdit(item);
    row.querySelector("[data-field=name]")?.focus();
    return;
  }
  if (entity === "contact") {
    const item = contacts.find((contact) => contact.id === id);
    if (!item) return;
    renderContacts();
    const row = contactRows.querySelector(`tr[data-id="${CSS.escape(id)}"]`);
    if (!row) return;
    row.hidden = false;
    row.innerHTML = contactEdit(item);
    row.querySelector("[data-field=name]")?.focus();
    return;
  }
  if (entity === "opportunity") {
    const item = opportunities.find((opportunity) => opportunity.id === id);
    if (!item) return;
    renderOpportunities();
    const row = opportunityRows.querySelector(`tr[data-id="${CSS.escape(id)}"]`);
    if (!row) return;
    row.hidden = false;
    row.innerHTML = opportunityEdit(item);
    row.querySelector("[data-field=title]")?.focus();
    return;
  }
  const item = activities.find((activity) => activity.id === id);
  if (!item) return;
  renderActivities();
  const row = activityRows.querySelector(`tr[data-id="${CSS.escape(id)}"]`);
  if (!row) return;
  row.hidden = false;
  row.innerHTML = activityEdit(item);
  row.querySelector("[data-field=subject]")?.focus();
}

function cancelEdit(entity) {
  if (entity === "customer") renderCustomers();
  else if (entity === "contact") renderContacts();
  else if (entity === "opportunity") renderOpportunities();
  else renderActivities();
}

function listQuery(filter, name) {
  const params = new URLSearchParams();
  for (const value of filter?.values() ?? []) params.append(name, value);
  const query = params.toString();
  return query ? `?${query}` : "";
}

async function load() {
  if (saving) return;
  const current = ++requestSeq;
  showMessage("Načítám CRM…");
  let boardResponse;
  let customersResponse;
  let contactsResponse;
  let opportunitiesResponse;
  let activitiesResponse;
  try {
    [boardResponse, customersResponse, contactsResponse, opportunitiesResponse, activitiesResponse] = await Promise.all([
      fetch("/api/crm/board", { cache: "no-store" }),
      fetch(`/api/crm/customers${listQuery(kindFilter, "kind")}`, { cache: "no-store" }),
      fetch(`/api/crm/contacts${listQuery(contactFilter, "customerId")}`, { cache: "no-store" }),
      fetch(`/api/crm/opportunities${listQuery(stageFilter, "stage")}`, { cache: "no-store" }),
      fetch(`/api/crm/activities${listQuery(activityFilter, "kind")}`, { cache: "no-store" })
    ]);
  } catch {
    if (current !== requestSeq) return;
    showMessage("CRM se nepodařilo načíst.");
    return;
  }
  if (current !== requestSeq) return;
  if ([boardResponse, customersResponse, contactsResponse, opportunitiesResponse, activitiesResponse].some((item) => item.status === 401)) {
    location.href = "/login.html";
    return;
  }
  const data = await boardResponse.json().catch(() => ({}));
  const customerData = await customersResponse.json().catch(() => null);
  const contactData = await contactsResponse.json().catch(() => null);
  const opportunityData = await opportunitiesResponse.json().catch(() => null);
  const activityData = await activitiesResponse.json().catch(() => null);
  if (current !== requestSeq) return;
  if (!boardResponse.ok) {
    showMessage(data.message || "CRM se nepodařilo načíst.");
    return;
  }
  data.catalogCustomers = Array.isArray(data.customers) ? data.customers : [];
  data.catalogContacts = Array.isArray(data.contacts) ? data.contacts : [];
  data.catalogOpportunities = Array.isArray(data.opportunities) ? data.opportunities : [];
  data.catalogActivities = Array.isArray(data.activities) ? data.activities : [];
  if (customersResponse.ok && Array.isArray(customerData)) data.customers = customerData;
  if (contactsResponse.ok && Array.isArray(contactData)) data.contacts = contactData;
  if (opportunitiesResponse.ok && Array.isArray(opportunityData)) data.opportunities = opportunityData;
  if (activitiesResponse.ok && Array.isArray(activityData)) data.activities = activityData;
  const listsOk = customersResponse.ok && contactsResponse.ok && opportunitiesResponse.ok && activitiesResponse.ok;
  showMessage(listsOk ? "" : ((customerData && customerData.message) || "Část CRM se nepodařilo načíst."));
  render(data);
}

async function send(url, body, method = "POST") {
  let response;
  try {
    response = await fetch(url, {
      method,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
  } catch {
    showMessage("Uložení se nepodařilo.");
    return null;
  }
  if (response.status === 401) {
    location.href = "/login.html";
    return null;
  }
  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    showMessage(data.message || "Uložení se nepodařilo.");
    return null;
  }
  return data;
}

async function saveEntity(entity, id) {
  if (saving) return;
  const row = document.querySelector(`tr[data-id="${CSS.escape(id)}"]`);
  if (!row) return;
  let url = "";
  let payload = null;
  if (entity === "customer") {
    if (!field(row, "name").trim()) {
      showMessage("Vyplňte název.");
      return;
    }
    url = `/api/crm/customers/${encodeURIComponent(id)}`;
    payload = customerPayload((name) => field(row, name));
  } else if (entity === "contact") {
    if (!field(row, "customer")) {
      showMessage("Vyplňte zákazníka.");
      return;
    }
    if (!field(row, "name").trim()) {
      showMessage("Vyplňte jméno kontaktu.");
      return;
    }
    url = `/api/crm/contacts/${encodeURIComponent(id)}`;
    payload = {
      customerId: field(row, "customer"),
      name: field(row, "name"),
      role: field(row, "role"),
      email: field(row, "email"),
      phone: field(row, "phone"),
      note: field(row, "note")
    };
  } else if (entity === "opportunity") {
    const stage = field(row, "stage");
    if (stage === "Prohráno" && row.dataset.stage !== "Prohráno" && !window.confirm("Označit příležitost jako prohranou?"))
      return;
    const parsed = opportunityPayload((name) => field(row, name));
    if (!parsed.ok) {
      showMessage(parsed.message);
      return;
    }
    url = `/api/crm/opportunities/${encodeURIComponent(id)}`;
    payload = parsed.payload;
  } else {
    const parsed = activityPayload((name) => field(row, name));
    if (!parsed.ok) {
      showMessage(parsed.message);
      return;
    }
    url = `/api/crm/activities/${encodeURIComponent(id)}`;
    payload = parsed.payload;
  }

  saving = true;
  const updated = await send(url, payload, "PUT");
  saving = false;
  if (!updated) return;
  showMessage("");
  await load();
}

function syncCustomer(opportunitySelect, customerSelect) {
  if (!opportunitySelect?.value || !customerSelect) return;
  const deal = catalogOpportunities.find((item) => item.id === opportunitySelect.value);
  if (deal) customerSelect.value = deal.customerId;
}

document.querySelector("#logout").addEventListener("click", async () => {
  await fetch("/api/logout", { method: "POST" });
  location.href = "/login.html";
});

document.querySelector("#reload").addEventListener("click", () => load());
document.querySelector(".tabs").addEventListener("click", (event) => {
  const button = event.target.closest("[data-tab]");
  if (button) showTab(button.dataset.tab);
});
document.body.addEventListener("click", (event) => {
  const card = event.target.closest("[data-open-tab]");
  if (card) {
    openCard(card);
    return;
  }
  const save = event.target.closest("[data-save]");
  if (save) {
    saveEntity(save.dataset.entity, save.dataset.save);
    return;
  }
  const edit = event.target.closest("[data-edit]");
  if (edit) {
    beginEdit(edit.dataset.entity, edit.dataset.edit);
    return;
  }
  const cancel = event.target.closest("[data-cancel]");
  if (cancel) cancelEdit(cancel.dataset.entity);
});
document.body.addEventListener("keydown", (event) => {
  if (event.key !== "Enter" && event.key !== " ") return;
  const card = event.target.closest("[data-open-tab]");
  if (!card) return;
  event.preventDefault();
  openCard(card);
});
kindFilter = window.bindStatusFilter(document.querySelector("#filter-kind"), {
  param: "druh",
  known: ["Firma", "Osoba"],
  noun: { few: "druhy", many: "druhů" },
  onChange() { load(); }
});
contactFilter = window.bindStatusFilter(document.querySelector("#contact-filter"), {
  param: "kontakty",
  dynamic: true,
  noun: { few: "zákazníci", many: "zákazníků" },
  onChange() {
    const selected = contactFilter.values();
    const target = document.querySelector("#contact-customer");
    if (selected.length === 1 && selected[0] !== "*" && target && !target.value) target.value = selected[0];
    load();
  }
});
stageFilter = window.bindStatusFilter(document.querySelector("#filter-stage"), {
  param: "faze",
  known: ["Nový", "V jednání", "Nabídka", "Vyhráno", "Prohráno"],
  noun: { few: "fáze", many: "fází" },
  onChange() { load(); }
});
activityFilter = window.bindStatusFilter(document.querySelector("#filter-activity"), {
  param: "aktivity",
  known: ["Hovor", "Schůzka", "Poznámka"],
  noun: { few: "druhy", many: "druhů" },
  onChange() { load(); }
});
document.querySelector("#act-opportunity").addEventListener("change", (event) => {
  syncCustomer(event.target, document.querySelector("#act-customer"));
});
activityRows.addEventListener("change", (event) => {
  const select = event.target.closest("[data-field=opportunity]");
  if (!select) return;
  syncCustomer(select, select.closest("tr")?.querySelector("[data-field=customer]"));
});

document.querySelector("#act-when").value = defaultWhen();
document.querySelector("#customer-create").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (saving) return;
  if (!valueOf("customer", "name").trim()) {
    showMessage("Vyplňte název.");
    return;
  }
  saving = true;
  const created = await send("/api/crm/customers", customerPayload((name) => valueOf("customer", name)));
  saving = false;
  if (!created) return;
  event.target.reset();
  showMessage("");
  await load();
});
document.querySelector("#contact-create").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (saving) return;
  if (!valueOf("contact", "customer")) {
    showMessage("Vyplňte zákazníka.");
    return;
  }
  if (!valueOf("contact", "name").trim()) {
    showMessage("Vyplňte jméno kontaktu.");
    return;
  }
  saving = true;
  const created = await send("/api/crm/contacts", {
    customerId: valueOf("contact", "customer"),
    name: valueOf("contact", "name"),
    role: valueOf("contact", "role"),
    email: valueOf("contact", "email"),
    phone: valueOf("contact", "phone"),
    note: valueOf("contact", "note")
  });
  saving = false;
  if (!created) return;
  event.target.reset();
  const selectedCustomer = contactFilter.values();
  if (selectedCustomer.length === 1 && selectedCustomer[0] !== "*")
    document.querySelector("#contact-customer").value = selectedCustomer[0];
  showMessage("");
  await load();
});
document.querySelector("#opportunity-create").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (saving) return;
  const parsed = opportunityPayload((name) => valueOf("opp", name));
  if (!parsed.ok) {
    showMessage(parsed.message);
    return;
  }
  saving = true;
  const created = await send("/api/crm/opportunities", parsed.payload);
  saving = false;
  if (!created) return;
  event.target.reset();
  document.querySelector("#opp-customer").value = "";
  showMessage("");
  await load();
});
document.querySelector("#activity-create").addEventListener("submit", async (event) => {
  event.preventDefault();
  if (saving) return;
  const parsed = activityPayload((name) => valueOf("act", name));
  if (!parsed.ok) {
    showMessage(parsed.message);
    return;
  }
  saving = true;
  const created = await send("/api/crm/activities", parsed.payload);
  saving = false;
  if (!created) return;
  event.target.reset();
  document.querySelector("#act-customer").value = "";
  document.querySelector("#act-opportunity").value = "";
  document.querySelector("#act-when").value = defaultWhen();
  showMessage("");
  await load();
});

load();
