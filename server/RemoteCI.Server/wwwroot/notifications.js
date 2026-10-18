// 个人通知：顶栏铃铛未读数轮询、新通知页内提示，以及通知页上的浏览器通知（Web Push）开关。
// REST /api 只认 Bearer，网页用 Cookie 调用 /Inbox 的页面处理器。
(() => {
    const bell = document.querySelector("[data-notification-bell]");
    if (!bell) return;
    const unreadBadge = bell.querySelector("[data-unread-badge]");
    const swapBadge = document.querySelector("[data-swap-badge]");
    const toasts = document.querySelector("[data-toast-stack]");
    const pollMs = document.documentElement.hasAttribute("data-mobile-ui") ? 15000 : 30000;
    let since = new Date().toISOString();

    function setBadge(element, count, cap) {
        if (!element) return;
        element.hidden = !count;
        element.textContent = cap && count > cap ? `${cap}+` : String(count || 0);
    }

    function toast(item) {
        if (!toasts) return;
        const link = document.createElement("a");
        link.className = "toast";
        link.href = item.kind?.startsWith("personal_course_") ? "/MySchedule" : "/SwapRequests" + (item.kind === "swap_approved" || item.kind === "swap_rejected" || item.kind === "swap_revoked" ? "#outgoing" : "#incoming");
        const title = document.createElement("strong");
        title.textContent = item.title;
        const body = document.createElement("span");
        body.textContent = item.body;
        link.append(title, body);
        toasts.append(link);
        window.setTimeout(() => link.classList.add("leaving"), 9000);
        window.setTimeout(() => link.remove(), 9600);
    }

    async function poll() {
        if (document.hidden) return;
        try {
            const url = `${bell.dataset.unreadUrl}&after=${encodeURIComponent(since)}`;
            const response = await fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" } });
            if (!response.ok || response.redirected) return;
            const data = await response.json();
            setBadge(unreadBadge, data.unread, 99);
            if (navigator.setAppBadge) { if (data.unread) navigator.setAppBadge(data.unread).catch(() => {}); else navigator.clearAppBadge?.().catch(() => {}); }
            setBadge(swapBadge, data.pendingSwaps);
            if (data.serverTime) since = data.serverTime;
            // 浏览器通知已开启时由 service worker 弹出系统通知，页内仍补一个轻提示。
            (data.items || []).slice().reverse().forEach(toast);
        } catch {
            // 网络抖动时等待下一轮。
        }
    }

    void poll();
    window.setInterval(poll, pollMs);
    document.addEventListener("visibilitychange", () => { if (!document.hidden) poll(); });

    // ---------- 通知页：浏览器通知开关 ----------
    const settings = document.querySelector("[data-push-settings]");
    if (!settings) return;
    const status = settings.querySelector("[data-push-status]");
    const enable = settings.querySelector("[data-push-enable]");
    const disable = settings.querySelector("[data-push-disable]");
    const token = settings.querySelector('input[name="__RequestVerificationToken"]')?.value || "";
    const ios = /iPhone|iPad|iPod/.test(navigator.userAgent);
    const installed = matchMedia("(display-mode: standalone)").matches || navigator.standalone;
    const guide = settings.querySelector("[data-ios-push-guide]");
    if (guide) guide.hidden = !ios || installed;
    const supported = "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;

    function keyBytes(base64Url) {
        const padded = (base64Url + "===".slice((base64Url.length + 3) % 4)).replace(/-/g, "+").replace(/_/g, "/");
        return Uint8Array.from(atob(padded), c => c.charCodeAt(0));
    }

    function encode(buffer) {
        return btoa(String.fromCharCode(...new Uint8Array(buffer))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
    }

    async function post(url, subscription) {
        const json = subscription.toJSON();
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token, Accept: "application/json" },
            body: JSON.stringify({
                endpoint: subscription.endpoint,
                p256dh: json.keys?.p256dh || encode(subscription.getKey("p256dh")),
                auth: json.keys?.auth || encode(subscription.getKey("auth")),
                eventMask: settings.querySelector("[data-push-event]") ? [...settings.querySelectorAll("[data-push-event]:checked")].reduce((mask, input) => mask | Number(input.value), 0) : null,
            }),
        });
        if (!response.ok) throw new Error((await response.json().catch(() => ({}))).message || "保存订阅失败");
    }

    async function refresh() {
        if (!window.isSecureContext) {
            status.textContent = "浏览器通知需要通过 HTTPS（或本机 localhost）访问 WebUI；当前为 HTTP，仅能在打开网页时看到提醒。";
            return;
        }
        if (!supported || ios && !installed) {
            status.textContent = ios && !installed ? "请先添加到主屏幕，再从桌面图标打开以启用通知。" : "当前浏览器不支持网页推送通知。";
            return;
        }
        if (Notification.permission === "denied") {
            status.textContent = "浏览器已拒绝本站通知，请在浏览器的网站设置中允许通知后再开启。";
            enable.hidden = true;
            disable.hidden = true;
            return;
        }
        const registration = await navigator.serviceWorker.getRegistration("/");
        const subscription = await registration?.pushManager.getSubscription();
        if (subscription) await post(settings.dataset.subscribeUrl, subscription);
        enable.hidden = !!subscription;
        disable.hidden = !subscription;
        status.textContent = subscription
            ? "本浏览器已开启通知：关闭网页后仍会在系统通知中心收到课程与换课提醒。"
            : "开启后，即使关闭网页也会在系统通知中心收到课程与换课提醒。";
    }

    enable?.addEventListener("click", async () => {
        enable.disabled = true;
        try {
            const permission = await Notification.requestPermission();
            if (permission !== "granted") { await refresh(); return; }
            const registration = await navigator.serviceWorker.register("/sw.js", { scope: "/" });
            await navigator.serviceWorker.ready;
            const subscription = await registration.pushManager.subscribe({
                userVisibleOnly: true,
                applicationServerKey: keyBytes(settings.dataset.vapidKey),
            });
            await post(settings.dataset.subscribeUrl, subscription);
        } catch (error) {
            status.textContent = `开启失败：${error.message || error}`;
            enable.disabled = false;
            return;
        }
        enable.disabled = false;
        await refresh();
    });

    disable?.addEventListener("click", async () => {
        const registration = await navigator.serviceWorker.getRegistration("/");
        const subscription = await registration?.pushManager.getSubscription();
        if (subscription) {
            await post(settings.dataset.unsubscribeUrl, subscription).catch(() => {});
            await subscription.unsubscribe();
        }
        await refresh();
    });

    document.querySelectorAll("[data-inbox-read]").forEach(link => link.addEventListener("click", () => {
        const body = new URLSearchParams({ id: link.dataset.inboxRead, __RequestVerificationToken: token });
        navigator.sendBeacon?.("/Inbox?handler=Read", body);
    }));

    settings.querySelectorAll("[data-push-event]").forEach(input => input.addEventListener("change", async () => {
        try {
            const registration = await navigator.serviceWorker.getRegistration("/");
            const subscription = await registration?.pushManager.getSubscription();
            if (subscription) { await post(settings.dataset.subscribeUrl, subscription); status.textContent = "通知分类已保存"; }
            else status.textContent = "开启通知后生效";
        } catch { status.textContent = "保存失败，请重试"; }
    }));
    settings.querySelector("[data-push-test]")?.addEventListener("click", async () => {
        try {
            const response = await fetch(location.pathname + "?handler=Test", { method: "POST", credentials: "same-origin", headers: { "X-CSRF-TOKEN": token, Accept: "application/json" } });
            if (!response.ok) throw Error((await response.json()).message || "发送失败");
            status.textContent = "测试通知已排队，请查看系统通知中心";
        } catch(error) { status.textContent = error.message || "发送失败"; }
    });
    refresh().catch(error => { status.textContent = `通知状态同步失败：${error.message || error}`; });
})();
