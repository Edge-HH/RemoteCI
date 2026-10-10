(() => {
    // 批量部分修改：用户改动某个字段的值时自动勾选“修改此项”，避免改了却忘记勾选导致未下发。
    document.addEventListener("input", markApplied);
    document.addEventListener("change", markApplied);

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

    function markApplied(event) {
        const target = event.target;
        if (!(target instanceof HTMLElement) || target.matches("[data-extension-field-apply]")) return;
        const apply = target.closest("[data-extension-field]")?.querySelector("[data-extension-field-apply]");
        if (apply && !apply.checked) apply.checked = true;
    }
})();
