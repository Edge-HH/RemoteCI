(() => {
    // 登录页的“手机扫码”：生成二维码 → 每 2 秒查询状态 → 手机确认后由本浏览器换取登录会话。
    const panel = document.querySelector("[data-login-panel='qr']");
    if (!panel) return;
    const tabs = [...document.querySelectorAll("[data-login-tab]")];
    const panels = [...document.querySelectorAll("[data-login-panel]")];
    const image = panel.querySelector("[data-qr-image]");
    const overlay = panel.querySelector("[data-qr-overlay]");
    const overlayIcon = panel.querySelector("[data-qr-overlay-icon]");
    const overlayTitle = panel.querySelector("[data-qr-overlay-title]");
    const overlayText = panel.querySelector("[data-qr-overlay-text]");
    const refresh = panel.querySelector("[data-qr-refresh]");
    const statusText = panel.querySelector("[data-qr-status-text]");
    const remember = panel.querySelector("[data-qr-remember]");
    const storageKey = "remoteci-login-tab";
    let challenge = null;
    let timer = null;
    let busy = false;

    const csrf = () => document.querySelector("input[name='__RequestVerificationToken']")?.value || "";

    async function post(url, data) {
        const response = await fetch(url, {
            method: "POST",
            headers: { "X-CSRF-TOKEN": csrf(), "Content-Type": "application/x-www-form-urlencoded" },
            body: new URLSearchParams(data || {}),
            credentials: "same-origin",
        });
        const json = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(json.error || `请求失败（HTTP ${response.status}）`);
        return json;
    }

    function showOverlay(icon, title, text, canRefresh) {
        overlay.hidden = false;
        overlayIcon.className = `bi ${icon}`;
        overlayTitle.textContent = title;
        overlayText.textContent = text || "";
        refresh.hidden = !canRefresh;
    }

    function stop() {
        if (timer) clearTimeout(timer);
        timer = null;
    }

    async function create() {
        stop();
        challenge = null;
        overlay.hidden = true;
        image.classList.add("loading");
        statusText.textContent = "正在生成二维码…";
        try {
            const result = await post(panel.dataset.qrCreate);
            challenge = { code: result.code, pollToken: result.pollToken, expiresAt: Date.now() + result.expiresInSeconds * 1000 };
            image.innerHTML = result.svg;
            statusText.textContent = "请用 RemoteCI 手机 App 扫描二维码。";
            schedule();
        } catch (error) {
            showOverlay("bi-exclamation-circle", "二维码生成失败", error.message, true);
            statusText.textContent = "";
        } finally {
            image.classList.remove("loading");
        }
    }

    function schedule() {
        stop();
        if (!challenge || panel.hidden || document.hidden) return;
        timer = setTimeout(poll, 2000);
    }

    async function poll() {
        if (!challenge || busy) return;
        if (Date.now() >= challenge.expiresAt) {
            expire();
            return;
        }
        busy = true;
        try {
            const status = await post(panel.dataset.qrStatus, { code: challenge.code, pollToken: challenge.pollToken });
            switch (status.state) {
                case "pending":
                    schedule();
                    break;
                case "scanned":
                    showOverlay("bi-phone", "已扫描", `请在手机上确认登录${status.scannedBy ? `（${status.scannedBy}）` : ""}`, false);
                    statusText.textContent = "等待手机确认…";
                    schedule();
                    break;
                case "approved":
                    showOverlay("bi-check-circle", "已确认", "正在登录…", false);
                    await complete();
                    break;
                case "denied":
                    challenge = null;
                    showOverlay("bi-x-circle", "已在手机上取消", "需要时可以重新生成二维码。", true);
                    statusText.textContent = "";
                    break;
                default:
                    expire();
            }
        } catch (error) {
            statusText.textContent = `${error.message}，稍后自动重试。`;
            schedule();
        } finally {
            busy = false;
        }
    }

    function expire() {
        challenge = null;
        stop();
        showOverlay("bi-arrow-clockwise", "二维码已过期", "点击刷新后重新扫码。", true);
        statusText.textContent = "";
    }

    async function complete() {
        try {
            const result = await post(panel.dataset.qrComplete, {
                code: challenge.code,
                pollToken: challenge.pollToken,
                rememberMe: remember?.checked ? "true" : "false",
            });
            window.location.assign(result.redirectUrl || "/");
        } catch (error) {
            challenge = null;
            showOverlay("bi-exclamation-circle", "登录失败", error.message, true);
            statusText.textContent = "";
        }
    }

    function select(name, persist) {
        tabs.forEach(tab => tab.setAttribute("aria-selected", String(tab.dataset.loginTab === name)));
        panels.forEach(item => { item.hidden = item.dataset.loginPanel !== name; });
        if (persist) {
            try { localStorage.setItem(storageKey, name); } catch { /* 私密模式下不记忆 */ }
        }
        if (name === "qr") {
            if (!challenge) create();
            else schedule();
        } else {
            stop();
        }
    }

    tabs.forEach(tab => tab.addEventListener("click", () => select(tab.dataset.loginTab, true)));
    refresh.addEventListener("click", () => create());
    document.addEventListener("visibilitychange", () => {
        if (!document.hidden && !panel.hidden) {
            if (challenge && Date.now() >= challenge.expiresAt) expire();
            else schedule();
        }
    });

    let initial = "password";
    try { initial = localStorage.getItem(storageKey) === "qr" ? "qr" : "password"; } catch { /* 使用默认 */ }
    // 账号密码校验失败回到本页时停留在密码页，便于查看错误。
    if (document.querySelector(".validation li")) initial = "password";
    select(initial, false);
})();
