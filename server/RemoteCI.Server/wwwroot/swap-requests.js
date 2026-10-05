// 换课页：页签切换、班级→日期→节次联动、互换/替换模式切换与强制换课确认。
(() => {
    const weekdays = "日一二三四五六";

    function formatDate(value) {
        const [y, m, d] = value.split("-").map(Number);
        if (!y) return value;
        const date = new Date(y, m - 1, d);
        return `${m}月${d}日 周${weekdays[date.getDay()]}`;
    }

    function option(value, text, data = {}) {
        const element = document.createElement("option");
        element.value = value;
        element.textContent = text;
        Object.assign(element.dataset, data);
        return element;
    }

    function setupTabs() {
        const tabs = document.querySelectorAll("[data-swap-tab]");
        const show = name => {
            tabs.forEach(tab => {
                const active = tab.dataset.swapTab === name;
                tab.classList.toggle("active", active);
                tab.setAttribute("aria-selected", active ? "true" : "false");
            });
            document.querySelectorAll("[data-swap-panel]").forEach(panel => { panel.hidden = panel.dataset.swapPanel !== name; });
            try { history.replaceState(null, "", `#${name}`); } catch { /* 忽略 */ }
        };
        tabs.forEach(tab => tab.addEventListener("click", () => show(tab.dataset.swapTab)));
        const initial = location.hash.replace("#", "");
        if (["create", "incoming", "outgoing"].includes(initial)) show(initial);
    }

    function setupForm(form) {
        const catalog = JSON.parse(form.querySelector("[data-swap-catalog]").textContent || "{}");
        const classes = catalog.classes || [];
        const slots = {};
        for (const name of ["source", "target"]) {
            const root = form.querySelector(`[data-swap-slot="${name}"]`);
            slots[name] = {
                root,
                classSelect: root.querySelector("[data-slot-class]"),
                dateSelect: root.querySelector("[data-slot-date]"),
                courseSelect: root.querySelector("[data-slot-course]"),
            };
        }
        const subjectField = form.querySelector("[data-swap-subject]");
        const subjectSelect = subjectField?.querySelector("select");
        const preview = form.querySelector("[data-swap-preview]");
        const mode = () => form.querySelector("[data-swap-mode]:checked")?.value || "Exchange";

        const findClass = id => classes.find(x => x.classId === id);
        const findDay = (cls, date) => cls?.days.find(x => x.date === date);
        const selectedCourse = slot => {
            const day = findDay(findClass(slot.classSelect.value), slot.dateSelect.value);
            return day?.courses.find(x => String(x.index) === slot.courseSelect.value);
        };

        function fillClasses(slot, preferMine) {
            slot.classSelect.replaceChildren(...classes.map(x => option(x.classId, x.className)));
            // 要换走的课默认落在“我有课”的第一个班级，减少选择步骤。
            const mine = classes.find(x => x.days.some(d => d.courses.some(c => c.mine)));
            if (preferMine && mine) slot.classSelect.value = mine.classId;
            fillDates(slot, preferMine);
        }

        function fillDates(slot, preferMine) {
            const cls = findClass(slot.classSelect.value);
            const days = cls?.days || [];
            slot.dateSelect.replaceChildren(...days.map(x => option(x.date, formatDate(x.date))));
            const mineDay = days.find(d => d.courses.some(c => c.mine));
            if (preferMine && mineDay) slot.dateSelect.value = mineDay.date;
            fillCourses(slot, preferMine);
        }

        function fillCourses(slot, preferMine) {
            const day = findDay(findClass(slot.classSelect.value), slot.dateSelect.value);
            const courses = day?.courses || [];
            slot.courseSelect.replaceChildren(...courses.map(c => {
                const time = c.startTime ? ` ${c.startTime}` : "";
                const teacher = c.teacher ? ` · ${c.teacher}` : "";
                return option(String(c.index), `${c.label}${time} ${c.subject}${teacher}${c.mine ? "（我的课）" : ""}`);
            }));
            const mine = courses.find(c => c.mine);
            if (preferMine && mine) slot.courseSelect.value = String(mine.index);
            updatePreview();
        }

        function updatePreview() {
            if (!preview) return;
            const target = selectedCourse(slots.target);
            if (mode() === "Replace") {
                preview.textContent = target && subjectSelect?.value
                    ? `目标课 ${target.label} ${target.subject} 将临时改为你上的 ${subjectSelect.value}。`
                    : "";
                return;
            }
            const source = selectedCourse(slots.source);
            if (!source || !target) { preview.textContent = ""; return; }
            preview.textContent = !source.mine && !target.mine
                ? "两节课中至少要有一节是你的课。"
                : `${source.label} ${source.subject} 与 ${target.label} ${target.subject} 将临时互换。`;
        }

        function syncMode() {
            const replace = mode() === "Replace";
            slots.source.root.hidden = replace;
            for (const select of [slots.source.classSelect, slots.source.dateSelect, slots.source.courseSelect])
                select.disabled = replace;
            if (subjectField && subjectSelect) {
                subjectField.hidden = !replace;
                subjectSelect.disabled = !replace;
                subjectSelect.required = replace;
            }
            updatePreview();
        }

        for (const [name, slot] of Object.entries(slots)) {
            fillClasses(slot, name === "source");
            slot.classSelect.addEventListener("change", () => fillDates(slot, false));
            slot.dateSelect.addEventListener("change", () => fillCourses(slot, false));
            slot.courseSelect.addEventListener("change", updatePreview);
        }
        subjectSelect?.addEventListener("change", updatePreview);
        form.querySelectorAll("[data-swap-mode]").forEach(input => input.addEventListener("change", syncMode));
        syncMode();

        // 强制换课：先弹出确认窗口，确定后才以 force=true 提交。
        const forceInput = form.querySelector("[data-swap-force]");
        const dialog = document.querySelector("[data-swap-force-dialog]");
        form.querySelector("[data-swap-force-open]")?.addEventListener("click", () => {
            if (!form.reportValidity()) return;
            if (dialog && typeof dialog.showModal === "function") dialog.showModal();
            else if (window.confirm("仅在需要紧急换课时使用，请提前与对方沟通并达成一致。")) submitForced();
        });
        const submitForced = () => {
            forceInput.value = "true";
            form.submit();
        };
        dialog?.querySelector("[data-swap-force-cancel]")?.addEventListener("click", () => dialog.close());
        dialog?.querySelector("[data-swap-force-confirm]")?.addEventListener("click", () => {
            dialog.close();
            submitForced();
        });
        form.addEventListener("submit", event => {
            if (event.submitter) forceInput.value = "false";
        });
    }

    function setupConfirms() {
        document.querySelectorAll("form[data-confirm]").forEach(form => form.addEventListener("submit", event => {
            if (!window.confirm(form.dataset.confirm)) event.preventDefault();
        }));
    }

    function init() {
        setupTabs();
        const form = document.querySelector("[data-swap-form]");
        if (form) setupForm(form);
        setupConfirms();
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
    else init();
})();
