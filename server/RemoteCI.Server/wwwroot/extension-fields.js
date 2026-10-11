(() => {
    // 批量部分修改：用户改动某个字段的值时自动勾选“修改此项”，避免改了却忘记勾选导致未下发。
    document.addEventListener("input", onFieldEdit);
    document.addEventListener("change", onFieldEdit);

    // 目标选择的“全选”按钮：再次点击时在全选与全不选之间切换。
    document.addEventListener("click", event => {
        const button = event.target instanceof Element ? event.target.closest("[data-check-all]") : null;
        if (!button) return;
        const boxes = [...document.querySelectorAll(`${button.dataset.checkAll} input[type="checkbox"]:not(:disabled)`)];
        const allChecked = boxes.length > 0 && boxes.every(box => box.checked);
        boxes.forEach(box => { box.checked = !allChecked; });
    });

    // 下发范围切换：按班级/分组与按具体设备二选一，切走时清空被隐藏面板的勾选，避免误发到看不见的目标。
    document.addEventListener("click", event => {
        const tab = event.target instanceof Element ? event.target.closest("[data-scope-tab]") : null;
        if (!tab) return;
        const form = tab.closest("form");
        if (!form) return;
        form.querySelectorAll("[data-scope-tab]").forEach(item => item.classList.toggle("active", item === tab));
        form.querySelectorAll("[data-scope-panel]").forEach(panel => {
            const visible = panel.dataset.scopePanel === tab.dataset.scopeTab;
            panel.hidden = !visible;
            if (!visible) panel.querySelectorAll("input[type=checkbox]").forEach(box => { box.checked = false; });
        });
    });

    // 设置卡片展开 / 收起：点击标题行（卡片总开关除外）或键盘 Enter / 空格。
    document.addEventListener("click", event => {
        if (!(event.target instanceof Element) || event.target.closest("[data-card-toggle-slot]")) return;
        const head = event.target.closest("[data-card-expand]");
        if (head) toggleCard(head);
    });
    document.addEventListener("keydown", event => {
        if (event.key !== "Enter" && event.key !== " ") return;
        const head = event.target instanceof Element ? event.target.closest("[data-card-expand]") : null;
        if (!head || event.target !== head) return;
        event.preventDefault();
        toggleCard(head);
    });

    // 色卡：保留当前透明度，只替换 RGB。
    document.addEventListener("click", event => {
        const chip = event.target instanceof Element ? event.target.closest("[data-color-swatch]") : null;
        if (!chip) return;
        const root = chip.closest("[data-extension-color]");
        if (!root) return;
        const alpha = Number(root.querySelector("[data-color-alpha]")?.value ?? 255);
        setColorText(root, hex2(alpha) + chip.dataset.colorSwatch.slice(1).toUpperCase());
        chip.closest("details")?.removeAttribute("open");
    });

    // 收起的卡片里若有未通过校验的必填项，浏览器无法聚焦隐藏控件，会静默拦截提交：先展开所在卡片。
    document.addEventListener("invalid", event => {
        const body = event.target instanceof Element ? event.target.closest(".extension-card-body[hidden]") : null;
        const head = body ? document.querySelector(`[data-card-expand="${body.id}"]`) : null;
        if (head) toggleCard(head, true);
    }, true);

    document.querySelectorAll("[data-extension-fields]").forEach(evaluateConditions);

    function onFieldEdit(event) {
        const target = event.target;
        if (!(target instanceof HTMLElement) || target.matches("[data-extension-field-apply]")) return;
        const field = target.closest("[data-extension-field]");
        if (!field) return;

        if (target.matches("[data-extension-switch]")) {
            const hidden = field.querySelector("input[type=hidden][data-extension-value]");
            if (hidden) hidden.value = target.checked ? "true" : "false";
            const text = field.querySelector("[data-switch-text]");
            if (text) text.textContent = target.checked ? "开" : "关";
        } else if (target.matches("[data-extension-range]")) {
            const number = field.querySelector("input[type=number][data-extension-value]");
            if (number) number.value = target.value;
        } else if (target.matches("input[type=number][data-extension-value]")) {
            const range = field.querySelector("[data-extension-range]");
            if (range && target.value !== "") range.value = target.value;
        } else if (target.matches("[data-color-picker], [data-color-alpha]")) {
            const root = target.closest("[data-extension-color]");
            const picker = root.querySelector("[data-color-picker]");
            const alpha = root.querySelector("[data-color-alpha]");
            setColorText(root, hex2(Number(alpha.value)) + picker.value.slice(1).toUpperCase());
        } else if (target.matches("[data-color-text]")) {
            syncColorControls(target.closest("[data-extension-color]"));
        }

        const apply = field.querySelector("[data-extension-field-apply]");
        if (apply && !apply.checked) apply.checked = true;
        const container = field.closest("[data-extension-fields]");
        if (container) evaluateConditions(container);
    }

    function toggleCard(head, forceOpen) {
        const body = document.getElementById(head.dataset.cardExpand);
        if (!body) return;
        const open = forceOpen ?? body.hidden;
        body.hidden = !open;
        head.setAttribute("aria-expanded", open ? "true" : "false");
        head.closest("[data-extension-card]")?.classList.toggle("is-open", open);
    }

    /** 按其它字段的当前值重新计算字段显隐 / 可编辑，再据此隐藏空卡片与空分区。 */
    function evaluateConditions(container) {
        const fields = [...container.querySelectorAll("[data-extension-field]")];
        const values = new Map(fields.map(field => [field.dataset.key, readValue(field)]));
        const lookup = key => values.get(key) ?? "";
        for (const field of fields) {
            field.hidden = !matchesAll(field.dataset.visibleWhen, lookup);
            const enabled = matchesAll(field.dataset.enabledWhen, lookup);
            field.inert = !enabled;
            field.classList.toggle("is-disabled", !enabled);
        }
        container.querySelectorAll("[data-extension-card]").forEach(card => {
            card.hidden = [...card.querySelectorAll("[data-extension-field]")].every(field => field.hidden);
        });
        container.querySelectorAll("[data-extension-section]").forEach(section => {
            section.hidden = [...section.querySelectorAll("[data-extension-card]")].every(card => card.hidden);
        });
    }

    function readValue(field) {
        const input = field.querySelector("[data-extension-value]");
        return input ? String(input.value ?? "") : "";
    }

    function matchesAll(json, lookup) {
        if (!json) return true;
        let conditions;
        try { conditions = JSON.parse(json); } catch { return true; }
        return conditions.every(condition => {
            const value = String(lookup(condition.key)).trim();
            const hit = (condition.values ?? []).some(candidate => valueEquals(String(candidate), value));
            return condition.not ? !hit : hit;
        });
    }

    function valueEquals(candidate, value) {
        if (candidate.toLowerCase() === value.toLowerCase()) return true;
        const a = Number(candidate), b = Number(value);
        return candidate !== "" && value !== "" && Number.isFinite(a) && Number.isFinite(b) && a === b;
    }

    function setColorText(root, argbWithoutHash) {
        const text = root.querySelector("[data-color-text]");
        text.value = "#" + argbWithoutHash;
        syncColorControls(root);
        text.dispatchEvent(new Event("change", { bubbles: true }));
    }

    /** 文本框是唯一提交值；取色器、透明度滑块与预览色块都从它派生。 */
    function syncColorControls(root) {
        if (!root) return;
        const color = parseColor(root.querySelector("[data-color-text]").value);
        root.classList.toggle("is-invalid", !color);
        if (!color) return;
        root.querySelector("[data-color-picker]").value = "#" + hex2(color.r) + hex2(color.g) + hex2(color.b);
        root.querySelector("[data-color-alpha]").value = String(color.a);
        root.querySelector("[data-color-preview]").style.backgroundColor =
            `rgba(${color.r}, ${color.g}, ${color.b}, ${(color.a / 255).toFixed(3)})`;
    }

    function parseColor(text) {
        let hex = String(text ?? "").trim();
        if (!/^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$/i.test(hex)) return null;
        hex = hex.slice(1);
        if (hex.length <= 4) hex = [...hex].map(c => c + c).join("");
        if (hex.length === 6) hex = "FF" + hex;
        const n = parseInt(hex, 16);
        return { a: (n >>> 24) & 255, r: (n >>> 16) & 255, g: (n >>> 8) & 255, b: n & 255 };
    }

    function hex2(n) {
        return Math.max(0, Math.min(255, Math.round(n))).toString(16).padStart(2, "0").toUpperCase();
    }
})();
