// 课表与日程的显示设置：开关保存在本机 localStorage（与 _ScheduleDisplayPrefs.cshtml 的键一致），
// 学科颜色经页面处理程序按账号保存，保存后就地更新同名学科色块的颜色变量。
(() => {
    const prefsKey = "remoteci-schedule-display";
    const viewKey = "remoteci-myschedule-view";
    const root = document.documentElement;
    const defaults = { teacher: true, cls: true, location: true, merge: true, weekend: "auto" };

    const readPrefs = () => {
        try { return { ...defaults, ...JSON.parse(localStorage.getItem(prefsKey) || "{}") }; } catch { return { ...defaults }; }
    };
    const apply = (prefs) => {
        root.dataset.sgTeacher = prefs.teacher ? "on" : "off";
        root.dataset.sgClass = prefs.cls ? "on" : "off";
        root.dataset.sgLocation = prefs.location ? "on" : "off";
        root.dataset.sgMerge = prefs.merge ? "on" : "off";
        root.dataset.sgWeekend = prefs.weekend === "always" || prefs.weekend === "never" ? prefs.weekend : "auto";
    };

    const prefs = readPrefs();
    apply(prefs);
    document.querySelectorAll("[data-sg-pref]").forEach((input) => {
        const key = input.dataset.sgPref;
        if (input.type === "checkbox") input.checked = prefs[key] !== false;
        else input.value = prefs[key];
        input.addEventListener("change", () => {
            prefs[key] = input.type === "checkbox" ? input.checked : input.value;
            try { localStorage.setItem(prefsKey, JSON.stringify(prefs)); } catch { }
            apply(prefs);
        });
    });

    // 我的日程：列表 / 表格切换，默认表格。
    const viewButtons = document.querySelectorAll("[data-my-view-button]");
    const setView = (view) => {
        if (view === "list") root.dataset.myView = "list"; else delete root.dataset.myView;
        viewButtons.forEach((button) => button.setAttribute("aria-pressed", button.dataset.myViewButton === view ? "true" : "false"));
    };
    if (viewButtons.length) {
        setView(root.dataset.myView === "list" ? "list" : "table");
        viewButtons.forEach((button) => button.addEventListener("click", () => {
            const view = button.dataset.myViewButton;
            try { localStorage.setItem(viewKey, view); } catch { }
            setView(view);
        }));
    }

    const styleOf = (colors) =>
        `--sc-bg:${colors.light.background};--sc-fg:${colors.light.foreground};--sc-bg-d:${colors.dark.background};--sc-fg-d:${colors.dark.foreground}`;
    const recolor = (element, style) => {
        style.split(";").forEach((pair) => {
            const [name, value] = pair.split(":");
            element.style.setProperty(name, value);
        });
    };

    document.querySelectorAll("[data-sg-color-form]").forEach((form) => {
        form.addEventListener("submit", async (event) => {
            event.preventDefault();
            const data = new FormData(form);
            if (event.submitter?.name) data.set(event.submitter.name, event.submitter.value);
            form.setAttribute("aria-busy", "true");
            try {
                const response = await fetch(form.action, { method: "POST", body: data, headers: { Accept: "application/json" } });
                const result = await response.json().catch(() => null);
                if (!response.ok || !result) throw new Error(result?.message || "保存学科颜色失败");
                const style = styleOf(result);
                document.querySelectorAll("[data-subject]").forEach((element) => {
                    if (element.dataset.subject === result.name) recolor(element, style);
                });
                form.querySelectorAll(".sg-swatch").forEach((swatch) =>
                    swatch.setAttribute("aria-pressed", swatch.value.toUpperCase() === result.color.toUpperCase() ? "true" : "false"));
                form.querySelector("[data-sg-reset]").disabled = !result.custom;
                form.querySelector("[data-sg-custom]").hidden = !result.custom;
            } catch (error) {
                alert(error.message);
            } finally {
                form.removeAttribute("aria-busy");
            }
        });
    });
})();
