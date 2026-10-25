(() => {
    const dialog = document.querySelector("[data-screenshot-dialog]");
    if (!dialog) return;
    const find = name => dialog.querySelector(`[data-screenshot-${name}]`);
    const form = find("form"), capture = find("capture"), status = find("status");
    const preview = find("preview"), image = find("image"), open = find("open"), caption = find("caption"), download = find("download");
    const history = find("history"), list = find("list"), targetName = find("target-name");
    const token = () => form.querySelector('[name="__RequestVerificationToken"]').value;
    let loaded = false;
    // 设备状态页：每张设备卡片带一个截图按钮，共用这个对话框，目标随按钮切换。
    let target = null;

    const formatTime = value => new Date(value).toLocaleString("zh-CN", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit", second: "2-digit" });
    const formatSize = bytes => bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`;

    function show(shot) {
        image.src = shot.url;
        open.href = shot.url;
        download.href = shot.downloadUrl;
        caption.textContent = `${formatTime(shot.capturedAt)} · ${shot.deviceName} · ${shot.width}×${shot.height} · ${formatSize(shot.size)} · ${shot.requestedBy}`;
        preview.hidden = false;
    }

    function reset() {
        preview.hidden = true;
        image.removeAttribute("src");
        status.classList.remove("error");
        status.textContent = "";
    }

    function render(shots) {
        if (!list) return;
        list.replaceChildren(...shots.map(shot => {
            const item = document.createElement("li");
            const button = document.createElement("button");
            button.type = "button";
            button.className = "ghost";
            button.textContent = `${formatTime(shot.capturedAt)} · ${shot.requestedBy}`;
            button.addEventListener("click", () => show(shot));
            item.append(button);
            return item;
        }));
        history.hidden = shots.length === 0;
    }

    async function refresh() {
        if (!dialog.dataset.listUrl) return;
        try {
            const response = await fetch(dialog.dataset.listUrl, { headers: { "X-Requested-With": "XMLHttpRequest" } });
            if (!response.ok) return;
            const shots = await response.json();
            render(shots);
            if (preview.hidden && shots.length > 0) show(shots[0]);
        } catch { /* 列表只是便利功能，失败时保留现有内容。 */ }
    }

    // 对话框打开时才加载历史截图，避免每次进入控制页都请求。
    new MutationObserver(() => {
        if (dialog.open && !loaded) { loaded = true; void refresh(); }
    }).observe(dialog, { attributes: true, attributeFilter: ["open"] });

    // 设备网格会定时整体替换，按钮事件委托在 document 上。
    document.addEventListener("click", event => {
        const button = event.target.closest("[data-screenshot-target]");
        if (!button) return;
        target = { classId: button.dataset.classId, credentialId: button.dataset.credentialId };
        if (targetName) targetName.textContent = button.dataset.targetName || "教室电脑";
        reset();
        if (typeof dialog.showModal === "function") dialog.showModal();
        else dialog.setAttribute("open", "");
    });

    form.addEventListener("submit", async event => {
        event.preventDefault();
        capture.disabled = true;
        status.classList.remove("error");
        status.textContent = "正在截图并上传，通常需要几秒…";
        try {
            const response = await fetch(dialog.dataset.captureUrl, {
                method: "POST",
                headers: { "X-CSRF-TOKEN": token(), "X-Requested-With": "XMLHttpRequest" },
                body: target ? new URLSearchParams(target) : undefined,
            });
            const result = await response.json();
            if (!result.success) throw new Error(result.message || "截图失败");
            status.textContent = result.message;
            show(result.screenshot);
            void refresh();
        } catch (error) {
            status.classList.add("error");
            status.textContent = error instanceof SyntaxError ? "截图失败，请稍后重试" : error.message;
        } finally {
            capture.disabled = false;
        }
    });
})();
