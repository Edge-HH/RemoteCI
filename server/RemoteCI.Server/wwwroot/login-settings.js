// 登录页设置：让预览实时反映主题、背景不透明度、登录卡片位置与刚选择的背景图。
(() => {
    const stage = document.querySelector("[data-login-preview-stage]");
    if (!stage) return;

    const opacityInput = document.querySelector("[data-login-opacity]");
    const positionInputs = [...document.querySelectorAll("[data-login-position]")];
    const themeInputs = [...document.querySelectorAll("[data-login-theme-option]")];
    const fileInput = document.querySelector("[data-login-background-file]");
    const positionAlign = { Left: "start", Center: "center", Right: "end" };

    // 强制主题直接生效；跟随访客偏好时用当前浏览器的主题近似预览。
    const resolveTheme = () => {
        const selected = themeInputs.find(input => input.checked)?.value ?? "Follow";
        if (selected === "Light") return "light";
        if (selected === "Dark") return "dark";
        return document.documentElement.dataset.theme === "light" ? "light" : "dark";
    };

    const applyTheme = () => { stage.dataset.previewTheme = resolveTheme(); };

    const applyOpacity = () => {
        const value = Math.min(100, Math.max(0, Number(opacityInput?.value ?? 100)));
        stage.style.setProperty("--login-preview-opacity", String(value / 100));
    };

    const applyPosition = () => {
        const selected = positionInputs.find(input => input.checked)?.value ?? "Center";
        stage.style.setProperty("--login-preview-align", positionAlign[selected] ?? "center");
    };

    // 新选择的背景图先用本地对象 URL 试看；保存后服务端返回的图片会替换它。
    let previewObjectUrl = null;
    const applyBackgroundFile = () => {
        const file = fileInput?.files?.[0];
        if (previewObjectUrl) { URL.revokeObjectURL(previewObjectUrl); previewObjectUrl = null; }
        if (!file) return;
        previewObjectUrl = URL.createObjectURL(file);
        stage.style.setProperty("--login-preview-image", `url("${previewObjectUrl}")`);
    };

    opacityInput?.addEventListener("input", applyOpacity);
    fileInput?.addEventListener("change", applyBackgroundFile);
    positionInputs.forEach(input => input.addEventListener("change", applyPosition));
    themeInputs.forEach(input => input.addEventListener("change", applyTheme));
    // 顶栏主题按钮会改写 html[data-theme]；跟随偏好时预览要同步刷新。
    new MutationObserver(applyTheme).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });

    applyTheme();
    applyOpacity();
    applyPosition();
})();