// 布局在正文之后同步加载本脚本，并用 rel=expect 推迟首次绘制，因此 tab 切换和状态恢复都发生在页面第一次显示之前，
// 不会先闪出默认状态再跳到原位置。

// 页面内 tab：[data-page-tabs] 中带 data-tab-link 的链接切换同页 [data-tab] 区块，其他链接是跨页 tab。
// 打开顺序：地址栏锚点（tab 名或区块内元素 id）> 本页上次停留的 tab > 第一个 tab；表单提交后重定向回来仍停在原 tab。
(() => {
    const nav = document.querySelector("[data-page-tabs]");
    if (!nav) return;
    const links = [...nav.querySelectorAll("[data-tab-link]")];
    if (links.length === 0) return;
    const storeKey = `remoteci-tab:${location.pathname.toLowerCase()}`;
    const names = links.map(link => link.dataset.tabLink);
    const show = (name, remember) => {
        links.forEach(link => {
            const active = link.dataset.tabLink === name;
            link.classList.toggle("active", active);
            if (active) link.setAttribute("aria-current", "page"); else link.removeAttribute("aria-current");
        });
        document.querySelectorAll("[data-tab]").forEach(section => { section.hidden = section.dataset.tab !== name; });
        if (remember) { try { sessionStorage.setItem(storeKey, name); } catch { } }
    };
    const fromHash = () => {
        const hash = decodeURIComponent(location.hash.slice(1));
        if (!hash) return null;
        if (names.includes(hash)) return hash;
        const owner = document.getElementById(hash)?.closest("[data-tab]");
        return owner && names.includes(owner.dataset.tab) ? owner.dataset.tab : null;
    };
    let stored = null;
    try { stored = sessionStorage.getItem(storeKey); } catch { }
    const initial = fromHash() ?? (names.includes(stored) ? stored : names[0]);
    show(initial, true);
    if (location.hash && !names.includes(decodeURIComponent(location.hash.slice(1)))) document.getElementById(decodeURIComponent(location.hash.slice(1)))?.scrollIntoView();
    links.forEach(link => link.addEventListener("click", event => {
        event.preventDefault();
        show(link.dataset.tabLink, true);
        history.replaceState(null, "", `#${link.dataset.tabLink}`);
    }));
    window.addEventListener("hashchange", () => { const name = fromHash(); if (name) show(name, true); });
})();

// 提交表单后服务端会重定向回同一页并重新渲染：提交前记下滚动位置、展开的折叠区和其他表单里尚未保存的选择，
// 回到同一页面时恢复，避免点一个按钮就丢掉勾选、下拉选择并跳回页面顶部。
(() => {
    const storeKey = "remoteci-page-state";
    const maxAgeMs = 60_000;
    const samePath = (a, b) => a.toLowerCase().replace(/\/+$/, "") === b.toLowerCase().replace(/\/+$/, "");

    const scrollers = () => [document.scrollingElement, ...document.querySelectorAll("[data-sidebar] .side-nav, .user-edit-dialog-shell, .schedule-table-wrap, .schedule-grid, .user-account-table-wrap")]
        .filter(Boolean);
    const scrollerKey = (element, index) => element === document.scrollingElement ? "document" : `${element.className}#${index}`;

    // 表单身份：提交地址加隐藏字段（行内表单常以隐藏的 id 区分），同一身份多次出现时再加序号。
    const formKey = form => {
        if (!form) return "none";
        const hidden = [...form.querySelectorAll('input[type="hidden"]')]
            .filter(input => input.name && input.name !== "__RequestVerificationToken")
            .map(input => `${input.name}=${input.value}`).join("&");
        return `${form.getAttribute("action") || location.pathname}?${form.id || ""}|${hidden}`;
    };
    const controlKeys = () => {
        const counts = new Map();
        return [...document.querySelectorAll("input, select, textarea")].flatMap(control => {
            if (!control.name && !control.id) return [];
            if (control.type === "hidden" || control.type === "password" || control.type === "file" || control.type === "submit" || control.type === "button") return [];
            const base = `${formKey(control.form)}|${control.name || control.id}|${control.type === "checkbox" || control.type === "radio" ? control.value : ""}`;
            const index = counts.get(base) ?? 0;
            counts.set(base, index + 1);
            return [{ control, key: `${base}#${index}` }];
        });
    };
    const currentValue = control => control.type === "checkbox" || control.type === "radio"
        ? control.checked
        : control.tagName === "SELECT" ? [...control.options].filter(option => option.selected).map(option => option.value) : control.value;
    const defaultValue = control => control.type === "checkbox" || control.type === "radio"
        ? control.defaultChecked
        : control.tagName === "SELECT" ? [...control.options].filter(option => option.defaultSelected).map(option => option.value) : control.defaultValue;
    const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

    const save = submittedForm => {
        const controls = controlKeys()
            .filter(({ control }) => !same(currentValue(control), defaultValue(control)))
            .map(({ control, key }) => ({
                key,
                value: currentValue(control),
                original: defaultValue(control),
                submitted: !!submittedForm && control.form === submittedForm,
            }));
        const state = {
            path: location.pathname,
            time: Date.now(),
            scroll: scrollers().map((element, index) => ({ key: scrollerKey(element, index), top: element.scrollTop, left: element.scrollLeft })),
            details: [...document.querySelectorAll("details[id], main details")].map((element, index) => ({ key: element.id || `details#${index}`, open: element.open })),
            controls,
        };
        try { sessionStorage.setItem(storeKey, JSON.stringify(state)); } catch { }
    };

    const restore = state => {
        // 服务端重新渲染的默认值与提交前一致，说明这项没有被保存，才把用户的选择放回去；
        // 被提交的表单成功后只保留勾选和下拉选择（例如批量操作的目标），输入的文字只在页面报错时恢复。
        const failed = !!document.querySelector(".flash.error, .validation li, .notice.warn, .field-validation-error");
        const saved = new Map(state.controls.map(item => [item.key, item]));
        const restored = [];
        controlKeys().forEach(({ control, key }) => {
            const item = saved.get(key);
            const selection = control.type === "checkbox" || control.type === "radio" || control.tagName === "SELECT";
            if (!item || (item.submitted && !failed && !selection) || !same(defaultValue(control), item.original)) return;
            if (control.type === "checkbox" || control.type === "radio") control.checked = item.value;
            else if (control.tagName === "SELECT") [...control.options].forEach(option => { option.selected = item.value.includes(option.value); });
            else control.value = item.value;
            restored.push(control);
        });
        // app.js 等延迟脚本在 DOMContentLoaded 前才挂上监听，等它们就绪后再通知联动（权限勾选、显隐切换等）。
        document.addEventListener("DOMContentLoaded", () => restored.forEach(control => {
            control.dispatchEvent(new Event("input", { bubbles: true }));
            control.dispatchEvent(new Event("change", { bubbles: true }));
        }), { once: true });
        const details = new Map(state.details.map(item => [item.key, item.open]));
        [...document.querySelectorAll("details[id], main details")].forEach((element, index) => {
            const open = details.get(element.id || `details#${index}`);
            if (open !== undefined && !element.matches(".account-menu, .class-menu, .row-menu, .tree-node-menu")) element.open = open;
        });
        const applyScroll = () => {
            const positions = new Map(state.scroll.map(item => [item.key, item]));
            scrollers().forEach((element, index) => {
                const item = positions.get(scrollerKey(element, index));
                if (item) element.scrollTo({ top: item.top, left: item.left, behavior: "instant" });
            });
        };
        applyScroll();
        // 延迟脚本渲染的内容、图片和字体会改变高度，就绪后各再对齐一次。
        document.addEventListener("DOMContentLoaded", applyScroll, { once: true });
        window.addEventListener("load", applyScroll, { once: true });
    };

    let state = null;
    try {
        state = JSON.parse(sessionStorage.getItem(storeKey) || "null");
        sessionStorage.removeItem(storeKey);
    } catch { state = null; }
    if (state && Date.now() - state.time < maxAgeMs && samePath(state.path, location.pathname)) {
        if ("scrollRestoration" in history) history.scrollRestoration = "manual";
        restore(state);
    }

    // 只在真正离开页面时记录：提交表单、脚本调用 form.submit()、顶栏刷新按钮。
    // 挂在 window 冒泡阶段，表单和 document 上的处理（确认框、前端校验、改用 fetch）都已决定是否阻止提交。
    window.addEventListener("submit", event => {
        if (!event.defaultPrevented) save(event.target);
    });
    const nativeSubmit = HTMLFormElement.prototype.submit;
    HTMLFormElement.prototype.submit = function () {
        save(this);
        return nativeSubmit.call(this);
    };
    document.addEventListener("click", event => {
        if (event.target.closest?.("[data-page-refresh]")) save(null);
    }, true);
})();
