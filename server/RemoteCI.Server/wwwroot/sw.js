// RemoteCI 浏览器通知 service worker：接收 Web Push 并显示系统通知，网页关闭后仍然有效。
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));

self.addEventListener("push", event => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch { data = { title: "RemoteCI", body: event.data?.text() || "" }; }
    const outgoing = ["swap_approved", "swap_rejected", "swap_revoked"].includes(data.kind);
    const url = (data.url || "/SwapRequests") + (outgoing ? "#outgoing" : "#incoming");
    event.waitUntil(self.registration.showNotification(data.title || "RemoteCI", {
        body: data.body || "",
        icon: "/favicon.svg",
        badge: "/favicon.svg",
        tag: data.id || undefined,
        renotify: true,
        requireInteraction: data.kind === "swap_requested" || data.kind === "swap_forced",
        data: { url },
    }));
});

self.addEventListener("notificationclick", event => {
    event.notification.close();
    const url = new URL(event.notification.data?.url || "/SwapRequests", self.location.origin).href;
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
        const existing = windows.find(client => client.url.startsWith(self.location.origin));
        if (existing) {
            await existing.focus();
            if ("navigate" in existing) await existing.navigate(url);
            return;
        }
        await self.clients.openWindow(url);
    })());
});
