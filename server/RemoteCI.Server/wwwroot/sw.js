// RemoteCI 浏览器通知 service worker：接收 Web Push 并显示系统通知，网页关闭后仍然有效。
self.addEventListener("install", () => self.skipWaiting());
const CACHE = "remoteci-shell-v1";
const SHELL = ["/offline.html", "/offline.js", "/mobile.css", "/icons/icon-192.png", "/icons/icon-512.png"];
self.addEventListener("install", event => event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL))));
self.addEventListener("activate", event => event.waitUntil((async () => {
    for (const key of await caches.keys()) if (key.startsWith("remoteci-shell-") && key !== CACHE) await caches.delete(key);
    await self.clients.claim();
})()));
self.addEventListener("fetch", event => {
    if (event.request.method !== "GET" || new URL(event.request.url).origin !== self.location.origin) return;
    const url = new URL(event.request.url);
    if (SHELL.includes(url.pathname)) {
        event.respondWith(caches.open(CACHE).then(async cache => {
            try { const response = await fetch(event.request); if (response.ok) await cache.put(url.pathname, response.clone()); return response; }
            catch { return cache.match(url.pathname); }
        }));
    } else if (event.request.mode === "navigate" && (url.pathname === "/" || url.pathname.startsWith("/Mobile/"))) {
        event.respondWith(fetch(event.request).catch(() => caches.match("/offline.html")));
    }
});
self.addEventListener("message", event => {
    if (event.data?.type === "clear-private") { if (self.navigator.clearAppBadge) event.waitUntil(self.navigator.clearAppBadge()); }
});

self.addEventListener("push", event => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch { data = { title: "RemoteCI", body: event.data?.text() || "" }; }
    const outgoing = ["swap_approved", "swap_rejected", "swap_revoked"].includes(data.kind);
    const url = (data.url || "/Inbox") + (data.kind?.startsWith("swap_") ? (outgoing ? "#outgoing" : "#incoming") : "");
    if (Number.isFinite(data.unread) && self.navigator.setAppBadge) self.navigator.setAppBadge(data.unread).catch(() => {});
    event.waitUntil(self.registration.showNotification(data.title || "RemoteCI", {
        body: data.body || "",
        icon: "/icons/icon-192.png",
        badge: "/icons/icon-192.png",
        tag: data.id || undefined,
        renotify: true,
        requireInteraction: data.kind === "swap_requested" || data.kind === "swap_forced",
        data: { url },
    }));
});

self.addEventListener("notificationclick", event => {
    event.notification.close();
    const target = new URL(event.notification.data?.url || "/Inbox", self.location.origin);
    if (target.origin !== self.location.origin) return;
    const url = target.href;
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
