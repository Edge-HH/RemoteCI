(() => {
    // 插件市场列表与 CIPX 上传。勾选结果仍是 name="PluginIds" / name="PluginPackageTransferIds" 的复选框，
    // 由 batch-control.js 在“下一步”时统一复制到执行表单。
    const token = () => document.querySelector('[data-batch-execute-form] [name="__RequestVerificationToken"]')?.value;
    const handlerUrl = (handler, params = {}) => {
        const url = new URL(location.pathname, window.location.origin);
        url.searchParams.set("handler", handler);
        Object.entries(params).forEach(([key, value]) => url.searchParams.set(key, value));
        return url;
    };
    const formatCount = value => value >= 10000 ? `${(value / 10000).toFixed(1)} 万` : String(value ?? 0);
    const element = (tag, className, text) => {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    };

    const market = document.querySelector("[data-plugin-market]");
    if (market) {
        const list = market.querySelector("[data-plugin-market-list]");
        const status = market.querySelector("[data-plugin-market-status]");
        const search = market.querySelector("[data-plugin-market-search]");
        const sort = market.querySelector("[data-plugin-market-sort]");
        let installed = {};
        try { installed = JSON.parse(market.dataset.installed || "{}"); } catch { installed = {}; }
        const installedCount = id => Object.entries(installed)
            .find(([key]) => key.toLowerCase() === id.toLowerCase())?.[1] ?? 0;
        let plugins = [];
        let loading = false;
        let loaded = false;

        const render = () => {
            const checked = new Set([...list.querySelectorAll('input[name="PluginIds"]:checked')].map(input => input.value));
            const query = (search.value || "").trim().toLowerCase();
            const sorted = [...plugins].sort((a, b) => sort.value === "stars"
                ? b.starsCount - a.starsCount || b.downloadCount - a.downloadCount
                : sort.value === "name"
                    ? a.name.localeCompare(b.name, "zh-CN")
                    : b.downloadCount - a.downloadCount);
            list.innerHTML = "";
            let shown = 0;
            sorted.forEach(plugin => {
                const label = element("label", "check batch-plugin-option plugin-market-item");
                const matches = !query || [plugin.name, plugin.id, plugin.author, plugin.description]
                    .some(value => (value || "").toLowerCase().includes(query));
                // 不匹配的条目只隐藏，已勾选的选择保留。
                label.hidden = !matches;
                if (matches) shown++;
                const input = element("input");
                input.type = "checkbox";
                input.name = "PluginIds";
                input.value = plugin.id;
                input.checked = checked.has(plugin.id);
                const body = element("span");
                const title = element("strong", null, plugin.name);
                const count = installedCount(plugin.id);
                if (count > 0) title.append(" ", element("span", "status-pill", `已安装 · ${count} 台`));
                body.append(title, element("code", null, plugin.id));
                const meta = [`v${plugin.version}`, plugin.author, `下载 ${formatCount(plugin.downloadCount)}`, `★ ${formatCount(plugin.starsCount)}`]
                    .filter(Boolean).join(" · ");
                body.append(element("small", null, meta));
                if (plugin.description) body.append(element("small", "plugin-market-description", plugin.description));
                if (plugin.dependencies?.length) body.append(element("small", null, `必选依赖：${plugin.dependencies.join("、")}`));
                label.append(input, body);
                list.append(label);
            });
            updateStatus(shown, query);
        };

        const updateStatus = (shown = list.querySelectorAll(".plugin-market-item:not([hidden])").length,
            query = (search.value || "").trim()) => {
            const selected = list.querySelectorAll('input[name="PluginIds"]:checked').length;
            status.textContent = plugins.length === 0
                ? "插件市场暂无可用插件。"
                : `共 ${plugins.length} 个插件${query ? `，匹配 ${shown} 个` : ""}${selected ? `，已选 ${selected} 个` : ""}。`;
        };

        const load = async refresh => {
            if (loading) return;
            loading = true;
            status.textContent = refresh ? "正在刷新插件市场…" : "正在加载插件市场…";
            try {
                const response = await fetch(handlerUrl("PluginMarket", refresh ? { refresh: "true" } : {}), { credentials: "same-origin" });
                if (!response.ok || response.redirected) throw new Error("会话失效，请刷新页面后重试");
                const result = await response.json();
                if (!result.success) throw new Error(result.message || "读取插件市场失败");
                plugins = result.plugins || [];
                loaded = true;
                render();
            } catch (error) {
                status.textContent = `${error.message}。仍可在下方手动输入插件 Id。`;
            } finally {
                loading = false;
            }
        };

        search.addEventListener("input", render);
        sort.addEventListener("change", render);
        list.addEventListener("change", () => updateStatus());
        market.querySelector("[data-plugin-market-refresh]")?.addEventListener("click", () => load(true));
        document.addEventListener("click", event => {
            if (event.target.closest('[data-batch-open="InstallPlugins"]') && !loaded) void load(false);
        });
    }

    const packageInput = document.querySelector("[data-plugin-package-file]");
    if (packageInput) {
        const dialog = packageInput.closest("dialog");
        const status = dialog.querySelector("[data-plugin-package-status]");
        const list = dialog.querySelector("[data-plugin-package-list]");

        const upload = file => new Promise((resolve, reject) => {
            const body = new FormData();
            body.append("file", file);
            const xhr = new XMLHttpRequest();
            xhr.open("POST", handlerUrl("UploadPluginPackage"));
            xhr.withCredentials = true;
            const csrf = token();
            if (csrf) xhr.setRequestHeader("X-CSRF-TOKEN", csrf);
            xhr.upload.onprogress = event => {
                if (event.lengthComputable)
                    status.textContent = `正在上传 ${file.name}：${Math.floor(event.loaded * 100 / event.total)}%`;
            };
            xhr.onload = () => {
                if (xhr.status === 413) { reject(new Error("插件包超过服务器允许的大小")); return; }
                try {
                    const result = JSON.parse(xhr.responseText);
                    if (result.success) resolve(result.package);
                    else reject(new Error(result.message || "上传失败"));
                } catch {
                    reject(new Error("会话失效或上传失败，请刷新后重试"));
                }
            };
            xhr.onerror = () => reject(new Error("网络错误，上传中断"));
            xhr.send(body);
        });

        const addPackage = item => {
            // 同一插件只保留最后上传的包。
            list.querySelectorAll("[data-plugin-package-id]").forEach(existing => {
                if (existing.dataset.pluginPackageId.toLowerCase() === item.pluginId.toLowerCase()) existing.remove();
            });
            const label = element("label", "check batch-plugin-option");
            label.dataset.pluginPackageId = item.pluginId;
            const input = element("input");
            input.type = "checkbox";
            input.name = "PluginPackageTransferIds";
            input.value = item.transferId;
            input.checked = true;
            const body = element("span");
            body.append(element("strong", null, item.name), element("code", null, item.pluginId),
                element("small", null, `版本 ${item.version || "未知"} · ${item.size}`));
            label.append(input, body);
            list.append(label);
            list.hidden = false;
        };

        packageInput.addEventListener("change", async () => {
            const files = [...(packageInput.files || [])];
            if (files.length === 0) return;
            packageInput.disabled = true;
            const failures = [];
            let uploaded = 0;
            for (const file of files) {
                try {
                    addPackage(await upload(file));
                    uploaded++;
                } catch (error) {
                    failures.push(`${file.name}：${error.message}`);
                }
            }
            packageInput.value = "";
            packageInput.disabled = false;
            status.textContent = failures.length === 0
                ? `已上传 ${uploaded} 个插件包，勾选要安装的插件包后继续。`
                : `已上传 ${uploaded} 个，失败 ${failures.length} 个：${failures.join("；")}`;
        });
    }
})();
