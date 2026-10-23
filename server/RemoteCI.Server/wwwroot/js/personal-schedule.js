(() => {
    const form = document.querySelector('[data-personal-course-form]');
    if (!form) return;
    let dirty = false;
    form.addEventListener('input', () => { dirty = true; });
    // 课程时刻按本机时区解释；修改已有课程时保留原时区，避免出差时把课程整体平移。
    const offset = form.querySelector('[data-course-offset]');
    if (offset && offset.value === '') offset.value = String(-new Date().getTimezoneOffset());
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
