(() => {
    const form = document.querySelector('[data-personal-course-form]');
    if (!form) return;
    let dirty = false;
    form.addEventListener('input', () => { dirty = true; });
    // 保留用户选择日期的实际偏移（含夏令时），不用服务器或教室端时区解释私人安排。
    const pairs = ['start', 'end'].map(key => [form.querySelector(`[data-course-${key}]`), form.querySelector(`[data-course-${key}-iso]`)]);
    const pad = n => String(n).padStart(2, '0');
    for (const [input, hidden] of pairs) {
        if (hidden.value) {
            const date = new Date(hidden.value);
            if (!isNaN(date)) input.value = `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
        }
    }
    form.addEventListener('submit', () => {
        for (const [input, hidden] of pairs) {
            const date = new Date(input.value);
            const offset = -date.getTimezoneOffset();
            hidden.value = `${input.value.slice(0, 16)}:00.0000000${offset < 0 ? '-' : '+'}${pad(Math.floor(Math.abs(offset) / 60))}:${pad(Math.abs(offset) % 60)}`;
        }
    });
    let previous;
    const refresh = async () => {
        if (document.hidden || dirty) return;
        try {
            const response = await fetch('/MySchedule?handler=Courses', { headers: { Accept: 'application/json' } });
            if (!response.ok || !response.headers.get('content-type')?.includes('application/json')) return;
            const data = JSON.stringify(await response.json());
            if (previous !== undefined && previous !== data) location.reload();
            previous = data;
        } catch { /* 网络恢复后下次轮询补齐。 */ }
    };
    refresh();
    setInterval(refresh, 30000);
})();
