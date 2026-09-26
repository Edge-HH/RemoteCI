async function copyText(value) {
    if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(value); return; }
    // HTTP 局域网部署通常不是安全上下文，保留兼容复制方案。
    const input = document.createElement("textarea");
    input.value = value; input.setAttribute("readonly", ""); input.style.position = "fixed"; input.style.opacity = "0";
    document.body.appendChild(input); input.select();
    const copied = document.execCommand("copy"); input.remove();
    if (!copied) throw new Error("浏览器拒绝复制");
}
function closeMobileSidebar() { document.body.classList.remove("sidebar-open"); }

function syncRolePermissions(form) {
    const roleSelect = form.querySelector("[data-role-select]");
    const permissions = form.querySelector("[data-role-permissions]");
    const adminNote = form.querySelector("[data-admin-permission-note]");
    if (!roleSelect) return;

    const isAdmin = roleSelect.selectedOptions[0]?.dataset.admin === "true" || roleSelect.value === "Admin" || roleSelect.value === "2";
    if (permissions) {
        permissions.hidden = isAdmin;
        permissions.querySelectorAll('input[type="checkbox"]').forEach(input => { input.disabled = isAdmin; });
    }
    if (adminNote) adminNote.hidden = !isAdmin;
}

async function handleCopyClick(event) {
    const copyButton = event.target.closest("[data-copy-value]");
    if (!copyButton) return false;

    const icon = copyButton.querySelector("i");
    try {
        await copyText(copyButton.dataset.copyValue);
        copyButton.classList.add("copied");
        copyButton.setAttribute("aria-label", "配对码已复制");
        copyButton.title = "已复制";
        icon?.classList.replace("bi-copy", "bi-check2");
        window.setTimeout(() => {
            copyButton.classList.remove("copied");
            copyButton.setAttribute("aria-label", "复制配对码");
            copyButton.title = "复制配对码";
            icon?.classList.replace("bi-check2", "bi-copy");
        }, 1600);
    } catch {
        copyButton.setAttribute("aria-label", "复制失败，请手动复制");
        copyButton.title = "复制失败，请手动复制";
    }
    return true;
}

function openEditDialog(button) {
    const dialog = document.getElementById(button.dataset.userEditOpen);
    if (!dialog) return;
    const form = dialog.querySelector("[data-role-form]");
    if (form) syncRolePermissions(form);
    if (typeof dialog.showModal === "function") dialog.showModal();
    else dialog.setAttribute("open", "");
}

function closeEditDialog(dialog) {
    if (!dialog) return;
    if (typeof dialog.close === "function") dialog.close();
    else dialog.removeAttribute("open");
}

function handleDialogClick(event) {
    const openButton = event.target.closest("[data-user-edit-open]");
    if (openButton) {
        openEditDialog(openButton);
        return true;
    }

    const closeButton = event.target.closest("[data-user-edit-close]");
    if (closeButton) {
        closeEditDialog(closeButton.closest("[data-user-edit-dialog]"));
        return true;
    }

    if (!event.target.matches("[data-user-edit-dialog]")) return false;
    closeEditDialog(event.target);
    return true;
}

function handleSidebarClick(event) {
    if (event.target.closest("[data-sidebar-toggle]")) {
        if (window.matchMedia("(max-width: 820px)").matches) document.body.classList.toggle("sidebar-open");
        else {
            document.body.classList.toggle("sidebar-collapsed");
            localStorage.setItem("remoteci-sidebar-collapsed", document.body.classList.contains("sidebar-collapsed") ? "1" : "0");
        }
        return true;
    }
    if (!event.target.closest("[data-sidebar-backdrop]")) return false;
    closeMobileSidebar();
    return true;
}

function handlePageActionClick(event) {
    if (event.target.closest("[data-theme-toggle]")) {
        const nextTheme = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
        document.documentElement.dataset.theme = nextTheme;
        localStorage.setItem("remoteci-theme", nextTheme);
        return true;
    }
    if (!event.target.closest("[data-page-refresh]")) return false;
    window.location.reload();
    return true;
}

document.addEventListener("click", async event => {
    if (await handleCopyClick(event)) return;
    if (handleDialogClick(event)) return;
    if (handleSidebarClick(event)) return;
    handlePageActionClick(event);
});

const savedTheme = localStorage.getItem("remoteci-theme");
if (savedTheme === "dark") document.documentElement.dataset.theme = "dark";
if (localStorage.getItem("remoteci-sidebar-collapsed") === "1" && !window.matchMedia("(max-width: 820px)").matches) document.body.classList.add("sidebar-collapsed");

const searchInput = document.querySelector("[data-app-search]");
const searchFeedback = document.querySelector("[data-search-feedback]");
const searchEntries = [...document.querySelectorAll("a[data-search-label]")];
searchInput?.addEventListener("input", () => {
    const query = searchInput.value.trim().toLocaleLowerCase("zh-CN");
    if (!query) { searchFeedback?.classList.remove("visible"); return; }
    const match = searchEntries.find(entry => `${entry.textContent} ${entry.dataset.searchLabel}`.toLocaleLowerCase("zh-CN").includes(query));
    if (searchFeedback) { searchFeedback.textContent = match ? `按 Enter 打开：${match.textContent.trim().replace(/\s+/g, " ")}` : "未找到匹配页面或功能"; searchFeedback.classList.add("visible"); }
});
searchInput?.addEventListener("keydown", event => {
    if (event.key !== "Enter") return; event.preventDefault();
    const query = searchInput.value.trim().toLocaleLowerCase("zh-CN");
    const match = searchEntries.find(entry => `${entry.textContent} ${entry.dataset.searchLabel}`.toLocaleLowerCase("zh-CN").includes(query));
    if (match) window.location.href = match.href;
});
window.addEventListener("resize", () => { if (!window.matchMedia("(max-width: 820px)").matches) closeMobileSidebar(); });

document.querySelectorAll("[data-schedule-pull-form]").forEach(form => {
    form.addEventListener("submit", () => {
        const button = form.querySelector("[data-schedule-pull-button]"); const progress = form.querySelector("[data-schedule-pull-progress]");
        if (button) { button.disabled = true; button.textContent = "正在拉取…"; }
        if (progress) progress.hidden = false;
    });
});

document.querySelectorAll("[data-volume-form]").forEach(form => {
    const slider = form.querySelector("[data-volume-slider]");
    const output = form.querySelector("[data-volume-output]");
    const feedback = form.querySelector("[data-volume-feedback]");
    const summary = document.querySelector("[data-volume-summary]");
    const icon = document.querySelector("[data-volume-icon]");
    const muteForm = document.querySelector("[data-mute-form]");
    const muteInput = muteForm?.querySelector('input[name="muted"]');
    const muteButton = muteForm?.querySelector("[data-mute-button]");
    if (!slider) return;

    let previousValue = Number(slider.value);
    let timer = 0;
    let queuedRequest = null;
    let sending = false;

    const updateMutedUi = muted => {
        slider.dataset.muted = muted ? "true" : "false";
        if (muteInput) muteInput.value = muted ? "false" : "true";
        if (muteButton) muteButton.textContent = muted ? "取消静音" : "静音";
        icon?.classList.toggle("bi-volume-mute", muted);
        icon?.classList.toggle("bi-volume-up", !muted);
    };

    const drainQueue = async () => {
        if (sending) return;
        sending = true;
        while (queuedRequest) {
            const request = queuedRequest;
            queuedRequest = null;
            if (feedback) { feedback.textContent = "正在应用…"; feedback.classList.remove("error"); }
            const data = new FormData(form);
            data.set("VolumeLevel", String(request.value));
            data.set("unmute", request.unmute ? "true" : "false");
            try {
                const response = await fetch(form.action, {
                    method: "POST",
                    body: data,
                    headers: { "X-Requested-With": "XMLHttpRequest", "Accept": "application/json" },
                });
                const payload = await response.json();
                if (!payload.success) throw new Error(payload.message || "音量设置失败");
                if (request.unmute || payload.unmuted) updateMutedUi(false);
                const muted = slider.dataset.muted === "true";
                if (summary) summary.textContent = `当前音量 ${request.value}% · ${muted ? "已静音" : "未静音"}`;
                if (feedback) feedback.textContent = payload.message || "音量已应用";
            } catch (error) {
                if (feedback) {
                    feedback.textContent = error instanceof Error ? error.message : "音量设置失败";
                    feedback.classList.add("error");
                }
            }
        }
        sending = false;
    };

    const queueVolume = immediate => {
        const nextValue = Number(slider.value);
        const shouldUnmute = slider.dataset.muted === "true" && nextValue > previousValue;
        previousValue = nextValue;
        queuedRequest = {
            value: nextValue,
            unmute: shouldUnmute || queuedRequest?.unmute === true,
        };
        window.clearTimeout(timer);
        if (immediate) void drainQueue();
        else timer = window.setTimeout(() => void drainQueue(), 100);
    };

    slider.addEventListener("input", () => {
        if (output) output.textContent = `${slider.value}%`;
        queueVolume(false);
    });
    slider.addEventListener("change", () => queueVolume(true));
});

document.querySelectorAll("[data-schedule-change-form]").forEach(form => {
    const mode = form.querySelector("[data-schedule-mode]");
    const source = form.querySelector("[data-schedule-source]");
    const target = form.querySelector("[data-schedule-target]");
    const sourceDate = form.querySelector("[data-schedule-source-date]");
    const sourceRevision = form.querySelector("[data-schedule-source-revision]");
    const sourceIndex = form.querySelector("[data-schedule-source-index]");
    const exchangeField = form.querySelector("[data-exchange-field]");
    const replaceField = form.querySelector("[data-replace-field]");
    const targetInput = exchangeField?.querySelector("input, select");
    const replacementInput = replaceField?.querySelector("input, select");
    if (!mode) return;

    const syncSourceFields = () => {
        const selected = source?.selectedOptions[0];
        if (sourceDate) sourceDate.value = selected?.dataset.date ?? "";
        if (sourceRevision) sourceRevision.value = selected?.dataset.revision ?? "";
        if (sourceIndex) sourceIndex.value = selected?.dataset.index ?? "";
    };

    const syncTargetOptions = () => {
        if (!source || !target) return;
        const selectedSource = source.selectedOptions[0];
        [...target.options].forEach(option => {
            if (!option.value) return;
            const available = option.dataset.date === selectedSource?.dataset.date &&
                option.dataset.index !== selectedSource?.dataset.index;
            option.hidden = !available;
            option.disabled = !available;
        });
        if (target.selectedOptions[0]?.disabled) target.value = "";
    };

    const syncModeFields = () => {
        const exchange = mode.value === "Exchange" || mode.value === "1";
        if (exchangeField) exchangeField.hidden = !exchange;
        if (replaceField) replaceField.hidden = exchange;
        if (targetInput) targetInput.disabled = !exchange;
        if (replacementInput) replacementInput.disabled = exchange;
        if (exchange) syncTargetOptions();
    };

    mode.addEventListener("change", syncModeFields);
    source?.addEventListener("change", () => {
        syncSourceFields();
        if (target) target.value = "";
        syncTargetOptions();
    });
    syncSourceFields();
    syncModeFields();
});


document.querySelectorAll("[data-role-form]").forEach(form => {
    const roleSelect = form.querySelector("[data-role-select]");
    roleSelect?.addEventListener("change", () => syncRolePermissions(form));
    syncRolePermissions(form);
});

document.querySelectorAll("[data-password-confirmation]").forEach(form => {
    const newPassword = form.querySelector("[data-new-password]");
    const confirmation = form.querySelector("[data-confirm-password]");
    if (!newPassword || !confirmation) return;

    // 浏览器即时提示用于减少误操作，服务端仍有不可绕过的一致性校验。
    const validateMatch = () => confirmation.setCustomValidity(
        confirmation.value === newPassword.value ? "" : "两次输入的新密码不一致。",
    );
    newPassword.addEventListener("input", validateMatch);
    confirmation.addEventListener("input", validateMatch);
    form.addEventListener("submit", event => {
        validateMatch();
        if (form.checkValidity()) return;
        event.preventDefault();
        form.reportValidity();
    });
});


document.querySelectorAll("[data-backup-settings-form]").forEach(form => {
    const cadence = form.querySelector("[data-backup-cadence]");
    const timeField = form.querySelector("[data-backup-time]");
    const weekdayField = form.querySelector("[data-backup-weekday]");
    const timeInput = timeField?.querySelector("input");
    const weekdayInput = weekdayField?.querySelector("select");
    if (!cadence) return;

    const syncBackupFields = () => {
        const hourly = cadence.value === "Hourly" || cadence.value === "1";
        const weekly = cadence.value === "Weekly" || cadence.value === "3";
        if (timeField) timeField.hidden = hourly;
        if (timeInput) timeInput.disabled = hourly;
        if (weekdayField) weekdayField.hidden = !weekly;
        if (weekdayInput) weekdayInput.disabled = !weekly;
    };

    cadence.addEventListener("change", syncBackupFields);
    syncBackupFields();
});
