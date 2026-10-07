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

    function markApplied(event) {
        const target = event.target;
        if (!(target instanceof HTMLElement) || target.matches("[data-extension-field-apply]")) return;
        const apply = target.closest("[data-extension-field]")?.querySelector("[data-extension-field-apply]");
        if (apply && !apply.checked) apply.checked = true;
    }
})();
