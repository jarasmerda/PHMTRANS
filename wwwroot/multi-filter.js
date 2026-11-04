(function () {
  function rawValues(param, splitCommas) {
    const values = new URLSearchParams(location.search)
      .getAll(param)
      .map((value) => value.trim())
      .filter((value) => value.length > 0);
    if (!splitCommas) return values;
    return values.flatMap((value) => value.split(",")).map((value) => value.trim()).filter((value) => value.length > 0);
  }

  function canonical(value) {
    if (value == null || value === "*" || (Array.isArray(value) && value.length === 0)) return "*";
    return [...value].sort().join("\n");
  }

  window.bindStatusFilter = function (root, options) {
    const toggle = root.querySelector(".status-filter-toggle");
    const panel = root.querySelector(".status-filter-panel");
    const all = root.querySelector("[data-status-all]");
    let boxes = [...root.querySelectorAll("[data-status-value]")];
    const noun = options.noun || { few: "stavy", many: "stavů" };
    const exclude = options.excludeByDefault || [];
    const splitCommas = options.dynamic !== true;
    let pinned;

    function chosen() {
      return boxes.filter((box) => box.checked).map((box) => box.value);
    }

    function labels() {
      return boxes.filter((box) => box.checked).map((box) => box.dataset.label || box.value);
    }

    function summary() {
      const values = labels();
      if (all.checked || values.length === 0) return options.allLabel || "Vše";
      if (values.length === 1) return values[0];
      if (values.length === 2) return values.join(", ");
      const word = values.length >= 5 ? noun.many : noun.few;
      return `${values.length} ${word}`;
    }

    function paint() {
      toggle.textContent = summary();
    }

    function applyDefault() {
      if (!exclude.length) {
        all.checked = true;
        boxes.forEach((box) => { box.checked = false; });
        return;
      }
      all.checked = false;
      boxes.forEach((box) => { box.checked = !exclude.includes(box.value); });
    }

    function applyWanted(wanted) {
      if (wanted === "*" || (wanted == null && !exclude.length)) {
        all.checked = true;
        boxes.forEach((box) => { box.checked = false; });
        return;
      }
      if (wanted == null) {
        applyDefault();
        return;
      }
      const known = options.known || boxes.map((box) => box.value);
      const present = wanted.filter((value) => known.includes(value) && boxes.some((box) => box.value === value));
      if (!present.length) {
        if (options.dynamic && boxes.length === 0) {
          all.checked = false;
          return;
        }
        applyDefault();
        return;
      }
      all.checked = false;
      boxes.forEach((box) => { box.checked = present.includes(box.value); });
    }

    function readQuery() {
      const raw = rawValues(options.param, splitCommas);
      const wanted = !raw.length ? null : (raw.some((value) => value === "*" || value === "Vše") ? "*" : raw);
      if (options.dynamic) pinned = wanted;
      applyWanted(wanted);
      paint();
    }

    function writeQuery() {
      const params = new URLSearchParams(location.search);
      params.delete(options.param);
      if (all.checked) params.append(options.param, "*");
      else for (const value of chosen()) params.append(options.param, value);
      const query = params.toString();
      history.replaceState(null, "", `${location.pathname}${query ? `?${query}` : ""}${location.hash}`);
    }

    function publish() {
      pinned = undefined;
      paint();
      writeQuery();
      options.onChange();
    }

    function bindBox(box) {
      box.addEventListener("change", () => {
        if (box.checked) all.checked = false;
        if (!chosen().length) all.checked = true;
        publish();
      });
    }

    all.addEventListener("change", () => {
      if (all.checked) boxes.forEach((box) => { box.checked = false; });
      else if (!chosen().length) all.checked = true;
      publish();
    });
    for (const box of boxes) bindBox(box);

    toggle.addEventListener("click", () => {
      const open = panel.hidden;
      panel.hidden = !open;
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });
    document.addEventListener("click", (event) => {
      if (!root.contains(event.target)) {
        panel.hidden = true;
        toggle.setAttribute("aria-expanded", "false");
      }
    });

    function values() {
      if (options.dynamic && pinned !== undefined && boxes.length === 0 && Array.isArray(pinned))
        return pinned.slice();
      if (all.checked) return ["*"];
      const selected = chosen();
      return selected.length ? selected : ["*"];
    }

    function setChoices(items) {
      const wanted = pinned !== undefined ? pinned : (all.checked ? "*" : chosen());
      for (const box of boxes) box.closest("label")?.remove();
      for (const item of items) {
        const label = document.createElement("label");
        const input = document.createElement("input");
        input.type = "checkbox";
        input.setAttribute("data-status-value", "");
        input.value = item.value;
        if (item.label) input.dataset.label = item.label;
        label.append(input, document.createTextNode(` ${item.label ?? item.value}`));
        panel.append(label);
        bindBox(input);
      }
      boxes = [...root.querySelectorAll("[data-status-value]")];
      pinned = undefined;
      applyWanted(wanted);
      paint();
      const now = all.checked ? "*" : chosen();
      const changed = canonical(wanted) !== canonical(now);
      if (changed) writeQuery();
      return changed;
    }

    function setValues(wanted) {
      pinned = undefined;
      if (wanted == null || wanted === "*" || wanted === "") applyWanted("*");
      else applyWanted(Array.isArray(wanted) ? wanted : [wanted]);
      publish();
    }

    readQuery();
    return {
      values,
      isAll() { return values().includes("*"); },
      setChoices,
      setValues
    };
  };

  window.createMultiFilter = window.bindStatusFilter;
})();
