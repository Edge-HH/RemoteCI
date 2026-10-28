// 换课页：页签切换、班级→日期（今天起 30 天内任选）→节次联动、互换/替换模式切换与强制换课确认。
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

    // 强制换课看过即处理：第一次打开“待我处理”时提交，页签与导航角标改为剩余的待审批数。
    function markForcedSeen() {
        const form = document.querySelector("[data-swap-seen]");
        if (!form) return;
        form.remove();
        fetch(form.action, { method: "POST", credentials: "same-origin", body: new FormData(form), headers: { Accept: "application/json" } })
            .then(response => response.ok ? response.json() : null)
            .then(data => {
                if (!data) return;
                const tabBadge = document.querySelector("[data-swap-pending]");
                if (tabBadge) {
                    const left = Number(tabBadge.dataset.afterSeen) || 0;
                    tabBadge.textContent = String(left);
                    tabBadge.hidden = !left;
                }
                const navBadge = document.querySelector("[data-swap-badge]");
                if (navBadge) {
                    navBadge.textContent = String(data.pending || 0);
                    navBadge.hidden = !data.pending;
                }
            })
            .catch(() => { /* 下次打开再试 */ });
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
            if (name === "incoming") markForcedSeen();
        };
        tabs.forEach(tab => tab.addEventListener("click", () => show(tab.dataset.swapTab)));
        const initial = location.hash.replace("#", "");
        if (["create", "incoming", "outgoing"].includes(initial)) show(initial);
    }

    function setupForm(form) {
        const catalog = JSON.parse(form.querySelector("[data-swap-catalog]").textContent || "{}");
        const classes = catalog.classes || [];
        // 某班某天的课程：catalog 自带今天起 7 天，更远的日期选中后再向服务端读取。
        const days = new Map();
        const dayKey = (classId, date) => `${classId}|${date}`;
        for (const cls of classes) for (const day of cls.days || []) days.set(dayKey(cls.classId, day.date), day);
        const slots = {};
        for (const name of ["source", "target"]) {
            const root = form.querySelector(`[data-swap-slot="${name}"]`);
            slots[name] = {
                root,
                classSelect: root.querySelector("[data-slot-class]"),
                dateInput: root.querySelector("[data-slot-date]"),
                courseSelect: root.querySelector("[data-slot-course]"),
                hint: root.querySelector("[data-slot-hint]"),
                loading: 0,
            };
        }
        const subjectField = form.querySelector("[data-swap-subject]");
        const subjectSelect = subjectField?.querySelector("select");
        const preview = form.querySelector("[data-swap-preview]");
        const mode = () => form.querySelector("[data-swap-mode]:checked")?.value || "Exchange";

        const findClass = id => classes.find(x => x.classId === id);
        const selectedCourse = slot => days.get(dayKey(slot.classSelect.value, slot.dateInput.value))
            ?.courses.find(x => String(x.index) === slot.courseSelect.value);
        const setHint = (slot, text) => {
            if (!slot.hint) return;
            slot.hint.textContent = text || "";
            slot.hint.hidden = !text;
        };

        function fillClasses(slot, preferMine) {
            slot.classSelect.replaceChildren(...classes.map(x => option(x.classId, x.className)));
            // 要换走的课默认落在“我有课”的第一个班级，减少选择步骤。
            const mine = classes.find(x => (x.myDates || []).length > 0);
            if (preferMine && mine) slot.classSelect.value = mine.classId;
            fillDates(slot, preferMine);
        }

        function fillDates(slot, preferMine) {
            const cls = findClass(slot.classSelect.value);
            slot.dateInput.min = cls?.minDate || "";
            slot.dateInput.max = cls?.maxDate || "";
            const current = slot.dateInput.value;
            const inRange = current && cls && current >= cls.minDate && current <= cls.maxDate;
            if (preferMine && cls?.myDates?.length) slot.dateInput.value = cls.myDates[0];
            else if (!inRange) slot.dateInput.value = cls?.minDate || "";
            fillCourses(slot, preferMine);
        }

        async function fillCourses(slot, preferMine) {
            const classId = slot.classSelect.value;
            const date = slot.dateInput.value;
            const cls = findClass(classId);
            const loading = ++slot.loading;
            slot.courseSelect.replaceChildren();
            setHint(slot, "");
            updatePreview();
            if (!cls || !date) return;
            if (date < cls.minDate || date > cls.maxDate) {
                setHint(slot, `请选择 ${formatDate(cls.minDate)} 至 ${formatDate(cls.maxDate)} 之间的日期。`);
                return;
            }
            let day = days.get(dayKey(classId, date));
            if (!day) {
                setHint(slot, "正在读取课程…");
                try {
                    const query = new URLSearchParams({ handler: "Day", classId, date });
                    const response = await fetch(`${location.pathname}?${query}`, { credentials: "same-origin", headers: { Accept: "application/json" } });
                    const data = await response.json().catch(() => null);
                    if (loading !== slot.loading) return;
                    if (!response.ok) { setHint(slot, data?.message || "读取课程失败，请稍后再试。"); return; }
                    day = data;
                    days.set(dayKey(classId, date), day);
                } catch {
                    if (loading === slot.loading) setHint(slot, "读取课程失败，请检查网络后再试。");
                    return;
                }
            }
            const courses = day?.courses || [];
            slot.courseSelect.replaceChildren(...courses.map(c => {
                const time = c.startTime ? ` ${c.startTime}` : "";
                const teacher = c.teacher ? ` · ${c.teacher}` : "";
                return option(String(c.index), `${c.label}${time} ${c.subject}${teacher}${c.mine ? "（我的课）" : ""}`);
            }));
            setHint(slot, courses.length ? "" : `${formatDate(date)} 没有课。`);
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
            for (const select of [slots.source.classSelect, slots.source.dateInput, slots.source.courseSelect])
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
            slot.dateInput.addEventListener("change", () => fillCourses(slot, false));
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
        const acknowledgeInput = form.querySelector("[data-swap-acknowledge]");
        const submitForced = () => {
            forceInput.value = "true";
            submitChecked();
        };
        dialog?.querySelector("[data-swap-force-cancel]")?.addEventListener("click", () => dialog.close());
        dialog?.querySelector("[data-swap-force-confirm]")?.addEventListener("click", () => {
            dialog.close();
            submitForced();
        });

        // 提交前预检冲突（任课冲突、同一节课的其他待审批申请）：有需要确认的冲突时先列出，确认后才带确认标记提交。
        // 预检失败（网络问题等）时直接提交，由服务端兜底校验并提示。
        let checking = false;
        async function submitChecked() {
            if (checking) return;
            checking = true;
            acknowledgeInput.value = "false";
            try {
                const response = await fetch(`${location.pathname}?handler=Preview`, {
                    method: "POST", credentials: "same-origin", body: new FormData(form), headers: { Accept: "application/json" },
                });
                const data = response.ok ? await response.json() : null;
                const blocking = (data?.conflicts || []).filter(x => x.requiresAcknowledgement);
                if (blocking.length > 0) {
                    const lines = blocking.map(x => `· ${x.message}`).join("\n");
                    if (!window.confirm(`这次换课存在以下冲突：\n${lines}\n\n仍然提交？`)) {
                        forceInput.value = "false";
                        return;
                    }
                    acknowledgeInput.value = "true";
                }
            } catch {
                /* 交给服务端校验 */
            } finally {
                checking = false;
            }
            form.submit();
        }
        form.addEventListener("submit", event => {
            if (event.submitter) forceInput.value = "false";
            event.preventDefault();
            submitChecked();
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
