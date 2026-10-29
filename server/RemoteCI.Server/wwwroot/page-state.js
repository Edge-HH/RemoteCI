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
        controlKeys().forEach(({ control, key }) => {
            const item = saved.get(key);
            const selection = control.type === "checkbox" || control.type === "radio" || control.tagName === "SELECT";
            if (!item || (item.submitted && !failed && !selection) || !same(defaultValue(control), item.original)) return;
            if (control.type === "checkbox" || control.type === "radio") control.checked = item.value;
            else if (control.tagName === "SELECT") [...control.options].forEach(option => { option.selected = item.value.includes(option.value); });
            else control.value = item.value;
            control.dispatchEvent(new Event("input", { bubbles: true }));
            control.dispatchEvent(new Event("change", { bubbles: true }));
        });
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
        // 图片、字体加载完成后高度可能变化，再对齐一次。
        window.addEventListener("load", applyScroll, { once: true });
    };

    let state = null;
    try {
        state = JSON.parse(sessionStorage.getItem(storeKey) || "null");
        sessionStorage.removeItem(storeKey);
    } catch { state = null; }
    if (state && Date.now() - state.time < maxAgeMs && samePath(state.path, location.pathname)) {
        if ("scrollRestoration" in history) history.scrollRestoration = "manual";
        document.addEventListener("DOMContentLoaded", () => restore(state), { once: true });
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
