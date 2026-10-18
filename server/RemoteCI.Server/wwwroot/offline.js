(() => {
    try {
        const data = JSON.parse(localStorage.getItem("remoteci-offline-schedule"));
        if (!data || data.user !== localStorage.getItem("remoteci-offline-user")) return;
        document.getElementById("status").textContent = `${data.label || "课表"}：本设备上次同步的只读课表，保存于 ${new Date(data.savedAt).toLocaleString()}。`;
        document.getElementById("schedule").innerHTML = data.html;
    } catch { /* No usable local schedule yet. */ }
})();
