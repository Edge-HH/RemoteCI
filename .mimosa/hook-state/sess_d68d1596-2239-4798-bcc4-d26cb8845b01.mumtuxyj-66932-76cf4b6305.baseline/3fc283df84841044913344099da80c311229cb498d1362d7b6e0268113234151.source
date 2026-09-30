async function copyText(value) {
    if (navigator.clipboard && window.isSecureContext) {
        await navigator.clipboard.writeText(value);
        return;
    }

    // HTTP 局域网部署通常不是安全上下文，保留兼容复制方案。
    const input = document.createElement("textarea");
    input.value = value;
    input.setAttribute("readonly", "");
    input.style.position = "fixed";
    input.style.opacity = "0";
    document.body.appendChild(input);
    input.select();
    const copied = document.execCommand("copy");
    input.remove();
    if (!copied) throw new Error("浏览器拒绝复制");
}

document.addEventListener("click", async event => {
    const button = event.target.closest("[data-copy-value]");
    if (!button) return;

    const icon = button.querySelector("i");
    try {
        await copyText(button.dataset.copyValue);
        button.classList.add("copied");
        button.setAttribute("aria-label", "配对码已复制");
        button.title = "已复制";
        icon?.classList.replace("bi-copy", "bi-check2");
        window.setTimeout(() => {
            button.classList.remove("copied");
            button.setAttribute("aria-label", "复制配对码");
            button.title = "复制配对码";
            icon?.classList.replace("bi-check2", "bi-copy");
        }, 1600);
    } catch {
        button.setAttribute("aria-label", "复制失败，请手动复制");
        button.title = "复制失败，请手动复制";
    }
});
