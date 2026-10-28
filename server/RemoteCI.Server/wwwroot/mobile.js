(() => {
    const root = document.documentElement;
    const user = document.body.dataset.mobileUser;
    const classId = document.body.dataset.mobileClass;
    // Only small touch devices get the viewport fallback; resizing a desktop browser does not switch UI.
    if (!root.hasAttribute("data-mobile-ui")) return;
    const standalone = matchMedia("(display-mode: standalone)").matches || navigator.standalone;
    const systemTheme = matchMedia("(prefers-color-scheme: dark)");
    const applyTheme = () => {
        const mode = localStorage.getItem("remoteci-mobile-appearance") || "system";
        root.dataset.theme = mode === "system" ? (systemTheme.matches ? "dark" : "light") : mode;
        document.querySelector('meta[name="theme-color"]').content = getComputedStyle(root).getPropertyValue("--m-bg").trim() || "#F7FBF1";
    };
    systemTheme.addEventListener("change", applyTheme);
    for (const [selector, key, attr] of [["[data-mobile-appearance]", "remoteci-mobile-appearance", null], ["[data-mobile-palette]", "remoteci-mobile-palette", "palette"]]) {
        const select = document.querySelector(selector);
        if (!select) continue;
        select.value = localStorage.getItem(key) || (attr ? "green" : "system");
        select.addEventListener("change", () => { localStorage.setItem(key, select.value); if (attr) root.dataset[attr] = select.value; applyTheme(); });
    }
    applyTheme();
    const syncKeyboard = () => {
        if (!window.visualViewport) return;
        root.style.setProperty("--mobile-viewport-height", `${Math.round(visualViewport.height)}px`);
        document.body.classList.toggle("mobile-keyboard", visualViewport.height < innerHeight * .75 && !!document.activeElement?.matches("input,textarea,select"));
    };
    window.visualViewport?.addEventListener("resize", syncKeyboard);
    document.addEventListener("focusin", syncKeyboard);
    document.addEventListener("focusout", () => requestAnimationFrame(syncKeyboard));
    syncKeyboard();
    if ("serviceWorker" in navigator && isSecureContext) navigator.serviceWorker.register("/sw.js", { scope: "/" }).catch(() => {});
    const install = document.querySelector("[data-install-guide]");
    if (install) install.hidden = !!standalone;
    window.addEventListener("beforeinstallprompt", event => {
        event.preventDefault();
        const button = document.querySelector("[data-install-button]");
        if (!button) return;
        button.hidden = false;
        button.addEventListener("click", async () => { await event.prompt(); if ((await event.userChoice).outcome === "accepted") install.hidden = true; }, { once: true });
        document.querySelector("[data-install-instructions]").textContent = "点击安装，将 RemoteCI 添加到桌面。";
    });
    document.querySelector("[data-mobile-class-select]")?.addEventListener("change", event => event.target.form.requestSubmit());
    document.querySelector("[data-calendar-offset]")?.setAttribute("value", String(-new Date().getTimezoneOffset()));
    document.querySelector("[data-calendar-create]")?.addEventListener("submit", event => {
        if (!confirm("同一日历已有订阅时，生成新链接会使旧链接失效。继续生成？")) event.preventDefault();
    });
    const section = new URL(location.href).searchParams.get("section") || "users";
    document.querySelectorAll("[data-mobile-area]").forEach(element => { element.hidden = element.dataset.mobileArea !== section; });
    const dateTabs = [...document.querySelectorAll("[data-date-tab]")];
    function selectDate(value) {
        dateTabs.forEach(tab => tab.setAttribute("aria-selected", String(tab.dataset.dateTab === value)));
        document.querySelectorAll("[data-schedule-day]").forEach(day => { day.hidden = day.dataset.scheduleDay !== value; });
    }
    dateTabs.forEach(tab => tab.addEventListener("click", () => selectDate(tab.dataset.dateTab)));
    if (dateTabs.length) selectDate(dateTabs[0].dataset.dateTab);
    const dialog = document.querySelector("[data-course-dialog]");
    // 点某节课预选该课；不带日期的“换其他日期的课”按钮保留当前选择，在弹窗里改日期。
    document.querySelectorAll("[data-course-pick]").forEach(course => course.addEventListener("click", () => {
        if (course.dataset.date) {
            dialog.querySelector("[data-schedule-change-form]")?.dispatchEvent(new CustomEvent("schedule-pick", {
                detail: { date: course.dataset.date, index: course.dataset.index },
            }));
        }
        dialog.showModal();
    }));
    document.querySelector("[data-course-close]")?.addEventListener("click", () => dialog.close());
    const state = document.querySelector("[data-today-state]");
    let endsAt = null;
    const setText = (selector, value) => { const element = document.querySelector(selector); if (element) element.textContent = value; };
    function durationMs(value) {
        if (!value) return 0;
        const parts = String(value).split(":");
        return ((Number(parts[0]) * 60 + Number(parts[1])) * 60 + Number(parts[2])) * 1000;
    }
    async function pollState() {
        if (!state || document.hidden) return;
        try {
            const response = await fetch(state.dataset.stateUrl, { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } });
            if (!response.ok || response.redirected) throw Error();
            const data = await response.json();
            const snap = data.snapshot;
            const personal = data.personal;
            if (personal) {
                const active = personal.current, next = personal.next;
                setText("[data-current-stage]", active ? "正在上课" : "待上课");
                setText("[data-current-subject]", active?.course?.subject || next?.course?.subject || "暂无课程");
                setText("[data-current-detail]", active?.className || next?.className || "在我的日程中添加课程");
                setText("[data-next-subject]", next ? `${next.course.subject} · ${next.className}` : "目前没有下一节课程");
                endsAt = active?.endsAt ? Date.now() + (Date.parse(active.endsAt) - Date.parse(data.serverTime)) : next?.startsAt ? Date.now() + (Date.parse(next.startsAt) - Date.parse(data.serverTime)) : null;
            } else {
                const stage = snap?.currentState;
                setText("[data-current-stage]", ({ class: "正在上课", breaking: "课间", after_school: "已放学", prepare_class: "即将上课", 1: "正在上课", 2: "课间", 3: "已放学", 4: "即将上课" })[stage] || "今天");
                setText("[data-current-subject]", snap?.currentSubject || "暂无课程");
                setText("[data-current-detail]", [data.className, snap?.currentTimeLayoutItem].filter(Boolean).join(" · ") || "等待教室端同步");
                setText("[data-next-subject]", snap?.nextClassSubject || "目前没有下一节课程");
                const left = durationMs(snap?.onClassLeftTime || snap?.onBreakingLeftTime);
                endsAt = left ? Date.now() + left - Math.max(0, Date.parse(data.serverTime) - Date.parse(snap.generatedAt)) : null;
            }
            renderCountdown();
            setText("[data-connection-status]", data.online ? "RemoteCI 已连接" : "教室端未连接");
            setText("[data-sync-time]", snap?.generatedAt ? `状态同步于 ${new Date(snap.generatedAt).toLocaleTimeString()}` : "等待教室端同步");
        } catch { setText("[data-connection-status]", "连接暂时不可用"); }
    }
    function renderCountdown() {
        const left = endsAt ? Math.max(0, Math.ceil((endsAt - Date.now()) / 1000)) : null;
        setText("[data-current-countdown]", left === null ? "--:--" : `${String(Math.floor(left / 60)).padStart(2, "0")}:${String(left % 60).padStart(2, "0")}`);
    }
    if (state) {
        void pollState(); setInterval(pollState, 2000);
        setInterval(renderCountdown, 1000);
        document.addEventListener("visibilitychange", () => { if (!document.hidden) void pollState(); });
    }
    // Cache only schedule markup, never authenticated pages or mutation responses.
    const channel = "BroadcastChannel" in window ? new BroadcastChannel("remoteci-identity") : null;
    const clear = () => { localStorage.removeItem("remoteci-offline-schedule"); navigator.serviceWorker?.controller?.postMessage({ type: "clear-private" }); };
    channel?.addEventListener("message", clear);
    if (localStorage.getItem("remoteci-offline-user") !== user) { clear(); localStorage.setItem("remoteci-offline-user", user || ""); channel?.postMessage("identity-changed"); }
    const schedule = document.querySelector("[data-offline-schedule]");
    const watch = document.querySelector("[data-schedule-watch]");
    const refreshSchedule = async () => {
        if (!watch || document.hidden || document.querySelector("dialog[open]") || document.activeElement?.matches("input,textarea,select")) return;
        try {
            const response = await fetch(watch.dataset.scheduleWatch, { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } });
            if (!response.ok || response.redirected) return;
            if ((await response.json()).revision !== watch.dataset.revision) location.reload();
        } catch { /* Refresh again after connectivity returns. */ }
    };
    if (watch) { setInterval(refreshSchedule, 30000); document.addEventListener("visibilitychange", () => { if (!document.hidden) void refreshSchedule(); }); }
    if (schedule && user) {
        const clone = schedule.cloneNode(true);
        clone.querySelectorAll("form,dialog,script").forEach(element => element.remove());
        clone.querySelectorAll("button,a,input").forEach(element => { element.removeAttribute("href"); element.disabled = true; });
        clone.querySelectorAll("[hidden]").forEach(element => element.removeAttribute("hidden"));
        localStorage.setItem("remoteci-offline-schedule", JSON.stringify({ user, classId, label: location.pathname.endsWith("/MySchedule") ? "我的日程" : document.body.dataset.mobileClassName || "班级课表", savedAt: new Date().toISOString(), html: clone.innerHTML }));
    }
    document.addEventListener("submit", event => {
        if (event.target.matches("[data-mobile-logout]") || event.target.action?.includes("/Logout")) { clear(); localStorage.removeItem("remoteci-offline-user"); channel?.postMessage("signed-out"); }
    });
})();
