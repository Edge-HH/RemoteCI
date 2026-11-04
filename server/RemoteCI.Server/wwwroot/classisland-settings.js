// 远程修改 ClassIsland 设置的弹窗逻辑：
// 单班模式打开时自动读取设备当前设置并预填，只提交相对当前值修改过的项；
// 批量模式从空白开始，只提交明确填写过的项，未修改的设置保持各设备当前值。
// 收集结果由 batch-control.js 通过 dialog.cisCollect()/cisModifiedCount() 取用。
(() => {
    const dialog = document.querySelector("[data-cis-dialog]");
    if (!dialog) return;
    const status = dialog.querySelector("[data-cis-status]");
    const readUrl = dialog.dataset.cisReadUrl || null;
    const single = Boolean(readUrl);
    if (single) dialog.classList.add("cis-single");
    const navItems = [...dialog.querySelectorAll("[data-cis-nav-item]")];
    const pages = new Map([...dialog.querySelectorAll("[data-cis-page]")].map(node => [node.dataset.cisPage, node]));
    const rows = [...dialog.querySelectorAll("[data-cis-row]")];
    let snapshot = {};

    const setStatus = text => { if (status) status.textContent = text; };
    const rowsByKey = new Map();
    rows.forEach(row => rowsByKey.set(row.dataset.cisRow, row));

    // ---------- 分组导航 ----------
    const setPage = id => {
        navItems.forEach(item => item.classList.toggle("active", item.dataset.cisNavItem === id));
        pages.forEach((node, key) => { node.hidden = key !== id; });
    };
    navItems.forEach(item => item.addEventListener("click", () => setPage(item.dataset.cisNavItem)));

    // ---------- 字段读写 ----------
    // 双份控件的类型（布尔/颜色在单班与批量模式下呈现不同控件），按 data-cis-variant 区分。
    const fieldsFor = key => [...dialog.querySelectorAll(`[data-cis-key="${CSS.escape(key)}"]`)];
    const variantField = (key, variant) =>
        fieldsFor(key).find(field => field.dataset.cisVariant === variant) ||
        fieldsFor(key).find(field => !field.dataset.cisVariant);

    const readField = field => {
        if (!field || field.disabled) return null;
        if (field.type === "checkbox") return field.checked;
        const value = field.value;
        if (value === null || value === "") return null;
        switch (field.dataset.cisType) {
            case "boolean":
                return value === "true";
            case "integer": case "enum": {
                const parsed = Number(value);
                return Number.isFinite(parsed) ? Math.round(parsed) : null;
            }
            case "double": {
                const parsed = Number(value);
                return Number.isFinite(parsed) ? parsed : null;
            }
            case "stringList": {
                const items = value.split(/\r?\n/).map(item => item.trim()).filter(Boolean);
                return items.length > 0 ? items : null;
            }
            default:
                return value;
        }
    };

    const writeField = (field, value) => {
        if (!field) return;
        if (field.type === "checkbox") {
            field.checked = Boolean(value);
            return;
        }
        if (value === null || value === undefined) {
            field.value = "";
            return;
        }
        if (field.dataset.cisType === "stringList" && Array.isArray(value)) {
            field.value = value.join("\n");
            return;
        }
        if (field.dataset.cisType === "color") {
            field.value = normalizeColor(value) ?? "";
            return;
        }
        const text = String(value);
        // 枚举里没有的取值（设备返回了目录之外的下标）时不选中，避免提交意外的值。
        if (field.tagName === "SELECT" && ![...field.options].some(option => option.value === text)) {
            field.value = "";
            return;
        }
        field.value = text;
    };

    const normalizeColor = value => {
        if (typeof value !== "string") return null;
        let hex = value.trim();
        if (!/^#[0-9a-fA-F]{3,8}$/.test(hex)) return null;
        hex = hex.slice(1);
        if (hex.length === 3 || hex.length === 4) hex = [...hex].map(ch => ch + ch).join("");
        if (hex.length === 8) hex = hex.slice(2); // 输入 #AARRGGBB 时丢弃透明度，网页颜色选择器不支持。
        return `#${hex.toLowerCase()}`;
    };

    const sameValue = (a, b) => {
        if (Array.isArray(a) || Array.isArray(b)) return JSON.stringify(a ?? []) === JSON.stringify(b ?? []);
        if (typeof a === "number" || typeof b === "number") return Number(a) === Number(b);
        if (typeof a === "boolean" || typeof b === "boolean") return Boolean(a) === Boolean(b);
        return a === b;
    };

    // ---------- 修改标记 ----------
    const updateModifiedCount = () => {
        const modified = collectValues();
        const count = Object.keys(modified).length;
        rows.forEach(row => row.classList.remove("cis-dirty"));
        Object.keys(modified).forEach(key => rowsByKey.get(key)?.classList.add("cis-dirty"));
        if (status && single) {
            setStatus(count > 0
                ? `已修改 ${count} 项设置，提交时只发送这些设置项。`
                : readError ? "" : "设置与设备当前值一致。");
        } else if (status) {
            setStatus(count > 0
                ? `已填写 ${count} 项设置，提交时只发送这些设置项，未填写的保持设备当前值。`
                : "尚未修改任何设置项；只提交修改过的设置项。");
        }
        return count;
    };

    let readError = false;
    const collectValues = () => {
        const values = {};
        if (!single) {
            rows.forEach(row => {
                const key = row.dataset.cisRow;
                const value = readField(variantField(key, "batch"));
                if (value !== null) values[key] = value;
            });
            return values;
        }
        rows.forEach(row => {
            const key = row.dataset.cisRow;
            const value = readField(variantField(key, "single"));
            if (value === null || value === undefined) return;
            if (!(key in snapshot) || !sameValue(value, snapshot[key])) values[key] = value;
        });
        return values;
    };

    dialog.addEventListener("input", () => updateModifiedCount());
    dialog.addEventListener("change", () => updateModifiedCount());

    // ---------- 单班：读取并预填当前设置 ----------
    const applySnapshot = values => {
        snapshot = values ?? {};
        readError = false;
        rows.forEach(row => {
            const key = row.dataset.cisRow;
            const field = variantField(key, "single");
            if (!field) return;
            row.classList.remove("cis-unavailable");
            if (field.disabled) field.disabled = false;
            if (!(key in snapshot)) {
                // 设备没有报告该项（宿主缺失属性或读取失败）：置灰并禁止提交，避免误发送默认值。
                row.classList.add("cis-unavailable");
                field.disabled = true;
                writeField(field, null);
                return;
            }
            writeField(field, snapshot[key]);
        });
        updateModifiedCount();
    };

    const reread = async () => {
        if (!readUrl) return;
        setStatus("正在读取设备当前设置…");
        try {
            const response = await fetch(readUrl, { credentials: "same-origin" });
            if (!response.ok || response.redirected) throw new Error("读取失败");
            const result = await response.json();
            if (!result.success) throw new Error(result.message || "读取失败");
            applySnapshot(result.values ?? {});
            setStatus(`已读取设备当前设置（${Object.keys(result.values ?? {}).length} 项）。修改后只会提交改动的项。`);
        } catch (error) {
            readError = true;
            setStatus(`读取设备当前设置失败：${error.message}`);
        }
    };

    dialog.querySelector("[data-cis-reread]")?.addEventListener("click", () => { void reread(); });

    // 重置：单班回到设备当前值，批量清空全部填写。
    dialog.querySelector("[data-cis-reset]")?.addEventListener("click", () => {
        if (single) applySnapshot(snapshot);
        else {
            dialog.querySelectorAll("[data-cis-key]").forEach(field => {
                if (field.type === "checkbox") field.checked = false;
                else field.value = "";
            });
            updateModifiedCount();
        }
    });

    // ---------- 打开弹窗时初始化 ----------
    // <dialog> 打开没有专用事件，观察 open 属性变化；每次打开都重新读取，保证预填是设备当前值。
    new MutationObserver(() => {
        if (!dialog.open) return;
        if (single) void reread();
        else {
            snapshot = {};
            updateModifiedCount();
        }
        setPage(navItems[0]?.dataset.cisNavItem);
    }).observe(dialog, { attributes: true, attributeFilter: ["open"] });

    // ---------- 供 batch-control.js 调用 ----------
    dialog.cisCollect = () => {
        const values = collectValues();
        return Object.keys(values).length > 0 ? { values } : null;
    };
    dialog.cisModifiedCount = () => Object.keys(collectValues()).length;
    dialog.cisReread = () => { void reread(); };
})();
