// 电子班牌：时钟按服务端给出的教室端时间每秒推进（不依赖班牌设备的时区设置），内容每 15 秒刷新一次。
(() => {
    const board = document.querySelector('[data-board]');
    if (!board) return;
    const clock = board.querySelector('[data-clock]');
    const content = board.querySelector('[data-board-content]');
    const skew = Number(board.dataset.serverNow) - Date.now();
    const offsetMinutes = Number(board.dataset.offsetMinutes);

    function tick() {
        // 先换算成教室端本地时间，再按 UTC 字段读取，避免受班牌设备自身时区影响。
        const local = new Date(Date.now() + skew + offsetMinutes * 60000);
        const pad = value => String(value).padStart(2, '0');
        clock.textContent = `${pad(local.getUTCHours())}:${pad(local.getUTCMinutes())}`;
    }

    async function refresh() {
        try {
            const response = await fetch(board.dataset.refreshUrl, { cache: 'no-store' });
            if (response.status === 404) {
                content.innerHTML = '<p class="board-offline">班牌链接已被撤销，请联系班主任重新生成。</p>';
                return;
            }
            if (response.ok) content.innerHTML = await response.text();
        } catch {
            // 网络抖动时保留上一次内容。
        }
    }

    tick();
    setInterval(tick, 1000);
    setInterval(refresh, 15000);
    // 长时间运行的班牌每 6 小时整页重载一次，拿到新的日期和服务端版本。
    setTimeout(() => location.reload(), 6 * 3600 * 1000);
})();
