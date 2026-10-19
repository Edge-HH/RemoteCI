// 设备状态页：每 15 秒只替换设备网格；页面在后台时暂停，回到前台立即刷新一次。
(() => {
    const container = document.querySelector('[data-device-grid]');
    if (!container) return;
    const url = container.dataset.refreshUrl;
    let timer = null;
    let busy = false;

    async function refresh() {
        if (busy || document.hidden) return;
        busy = true;
        try {
            const response = await fetch(url, { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } });
            // 登录过期会被重定向到登录页，此时保留旧内容，交给用户下次操作时重新登录。
            if (response.ok && !response.redirected) container.innerHTML = await response.text();
        } catch {
            // 网络抖动时保留上一次的内容，下一个周期再试。
        } finally {
            busy = false;
        }
    }

    function schedule() {
        clearInterval(timer);
        timer = setInterval(refresh, 15000);
    }

    document.addEventListener('visibilitychange', () => {
        if (!document.hidden) refresh();
    });
    schedule();
})();
