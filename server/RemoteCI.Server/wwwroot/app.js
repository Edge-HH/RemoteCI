async function copyText(value) {
    if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(value); return; }
    // HTTP 局域网部署通常不是安全上下文，保留兼容复制方案。
    const input = document.createElement("textarea");
    input.value = value; input.setAttribute("readonly", ""); input.style.position = "fixed"; input.style.opacity = "0";
    document.body.appendChild(input); input.select();
    const copied = document.execCommand("copy"); input.remove();
    if (!copied) throw new Error("浏览器拒绝复制");
}
document.addEventListener("submit", event => {
    if (!event.target.action?.includes("/Logout")) return;
    localStorage.removeItem("remoteci-offline-schedule");
    localStorage.removeItem("remoteci-offline-user");
    navigator.serviceWorker?.controller?.postMessage({ type: "clear-private" });
    if ("BroadcastChannel" in window) { const channel = new BroadcastChannel("remoteci-identity"); channel.postMessage("signed-out"); channel.close(); }
});
function closeMobileSidebar() { document.body.classList.remove("sidebar-open"); }

// ---------- 分组权限清单：勾选状态就是最终拥有的权限，人员编辑器以所选角色的默认权限为基准 ----------
function permissionBaseline(checklist, kind) {
    return Number(kind === "management" ? checklist.dataset.baselineManagement : checklist.dataset.baseline) || 0;
}

function permissionInputs(root) {
    return [...root.querySelectorAll('input[type="checkbox"][data-kind]')];
}

function syncPermissionChecklist(checklist) {
    const showBaseline = checklist.dataset.showBaseline === "true";
    const locked = checklist.classList.contains("is-locked");
    checklist.querySelectorAll("[data-permission-group]").forEach(group => {
        const inputs = permissionInputs(group);
        const count = group.querySelector("[data-permission-count]");
        if (count) count.textContent = `${inputs.filter(input => input.checked).length}/${inputs.length}`;
        const toggle = group.querySelector("[data-permission-group-all]");
        const editable = inputs.filter(input => input.dataset.base !== "true");
        if (toggle) {
            toggle.disabled = locked || editable.length === 0;
            toggle.textContent = editable.length && editable.every(input => input.checked) ? "全不选" : "全选";
        }
    });
    let adjusted = 0;
    checklist.querySelectorAll("[data-permission-item]").forEach(item => {
        const input = item.querySelector('input[type="checkbox"][data-kind]');
        if (!input) return;
        const isBase = input.dataset.base === "true";
        const inBaseline = isBase || (permissionBaseline(checklist, input.dataset.kind) & Number(input.value)) !== 0;
        const changed = showBaseline && !locked && !isBase && input.checked !== inBaseline;
        if (changed) adjusted++;
        item.classList.toggle("is-adjusted", changed);
        const flag = item.querySelector("[data-permission-flag]");
        if (flag) flag.textContent = changed ? (input.checked ? "已新增" : "已收回") : "";
    });
    const reset = checklist.querySelector("[data-permission-reset]");
    if (reset) reset.disabled = locked || adjusted === 0;
}

/** 按基准（角色默认权限）重新勾选，相当于清除个人调整。 */
function applyPermissionBaseline(checklist) {
    permissionInputs(checklist).forEach(input => {
        if (input.dataset.base === "true") return;
        input.checked = (permissionBaseline(checklist, input.dataset.kind) & Number(input.value)) !== 0;
    });
    syncPermissionChecklist(checklist);
}

/** 管理员角色固定拥有全部权限：全部勾选并锁定；切回其他角色时解锁。 */
function setPermissionChecklistLocked(checklist, locked) {
    const description = checklist.querySelector("[data-permission-description]");
    if (description && description.dataset.defaultText === undefined && !checklist.classList.contains("is-locked"))
        description.dataset.defaultText = description.textContent;
    checklist.classList.toggle("is-locked", locked);
    permissionInputs(checklist).forEach(input => {
        input.disabled = locked || input.dataset.base === "true";
        if (locked) input.checked = true;
    });
    if (description) {
        description.textContent = locked
            ? "管理员固定拥有全部权限。"
            : description.dataset.defaultText ?? "已按角色自动勾选；勾选或取消即为该账号单独调整，在各班级中以班内角色为基础套用同样的调整。";
    }
    syncPermissionChecklist(checklist);
}

document.addEventListener("change", event => {
    const input = event.target.closest?.('[data-permission-checklist] input[type="checkbox"][data-kind]');
    if (input) syncPermissionChecklist(input.closest("[data-permission-checklist]"));
});

document.addEventListener("click", event => {
    const toggle = event.target.closest?.("[data-permission-group-all]");
    if (toggle) {
        const group = toggle.closest("[data-permission-group]");
        const editable = permissionInputs(group).filter(input => !input.disabled);
        const checkAll = !editable.every(input => input.checked);
        editable.forEach(input => { input.checked = checkAll; });
        syncPermissionChecklist(toggle.closest("[data-permission-checklist]"));
        return;
    }
    const reset = event.target.closest?.("[data-permission-reset]");
    if (reset) applyPermissionBaseline(reset.closest("[data-permission-checklist]"));
});

document.querySelectorAll("[data-permission-checklist]").forEach(syncPermissionChecklist);

function syncRolePermissions(form, roleChanged = false) {
    const roleSelect = form.querySelector("[data-role-select]");
    const checklist = form.querySelector("[data-permission-checklist]");
    const teacherUsernameNote = form.querySelector("[data-teacher-username-note]");
    if (!roleSelect) return;

    const option = roleSelect.selectedOptions[0];
    const isAdmin = option?.dataset.admin === "true" || roleSelect.value === "Admin" || roleSelect.value === "2";
    const isTeacher = option?.dataset.teacher === "true";
    if (checklist) {
        // 切换角色时以新角色的默认权限为基准并自动勾选；打开编辑窗口时保留账号已有的调整。
        if (roleChanged && option) {
            checklist.dataset.baseline = option.dataset.permissions ?? "0";
            checklist.dataset.baselineManagement = option.dataset.management ?? "0";
        }
        setPermissionChecklistLocked(checklist, isAdmin);
        if (roleChanged && !isAdmin) applyPermissionBaseline(checklist);
    }
    if (teacherUsernameNote) teacherUsernameNote.hidden = roleSelect.selectedOptions[0]?.dataset.teacher !== "true";
    // 老师按课表教师名动态绑定班级，不需要在建号时选择班级；隐藏字段时同步解除 required。
    const classField = form.querySelector("[data-create-class-field]");
    const classSelect = form.querySelector("[data-create-class-select]");
    const teacherNote = form.querySelector("[data-create-teacher-note]");
    if (classField) classField.hidden = isAdmin || isTeacher;
    if (classSelect) {
        classSelect.required = !isAdmin && !isTeacher;
        classSelect.disabled = isTeacher;
    }
    if (teacherNote) teacherNote.hidden = !isTeacher;
}

async function handleCopyClick(event) {
    const copyButton = event.target.closest("[data-copy-value]");
    if (!copyButton) return false;

    const icon = copyButton.querySelector("i");
    try {
        await copyText(copyButton.dataset.copyValue);
        copyButton.classList.add("copied");
        copyButton.setAttribute("aria-label", "已复制");
        copyButton.title = "已复制";
        icon?.classList.replace("icon-content-copy", "icon-check");
        window.setTimeout(() => {
            copyButton.classList.remove("copied");
            copyButton.setAttribute("aria-label", "复制");
            copyButton.title = "复制配对码";
            icon?.classList.replace("icon-check", "icon-content-copy");
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

// 自定义课程编辑窗：修改某门课、点了“添加”或保存失败时随页面打开（WebUI 与手机网页共用）。
document.addEventListener("DOMContentLoaded", () => {
    const editor = document.querySelector('[data-personal-editor][data-auto-open="true"]');
    if (editor && !editor.open && typeof editor.showModal === "function") editor.showModal();
});

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

// 手机扫码登录二维码：按需生成，倒计时结束后隐藏，避免一次性票据长期留在页面上。
let mobileLoginTimer = 0;
document.addEventListener("submit", async event => {
    const form = event.target.closest("[data-mobile-login-form]");
    if (!form) return;
    event.preventDefault();
    const card = form.closest(".mobile-login-card");
    const qr = card?.querySelector("[data-mobile-login-qr]");
    const status = form.querySelector("[data-mobile-login-status]");
    const button = form.querySelector("[data-mobile-login-generate]");
    window.clearInterval(mobileLoginTimer);
    button.disabled = true;
    try {
        const response = await fetch(form.action, { method: "POST", body: new FormData(form), credentials: "same-origin" });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const result = await response.json();
        qr.innerHTML = result.svg;
        qr.hidden = false;
        button.lastChild.textContent = " 重新生成";
        let remaining = result.expiresInSeconds;
        const tick = () => {
            if (remaining <= 0) {
                window.clearInterval(mobileLoginTimer);
                qr.hidden = true;
                qr.innerHTML = "";
                status.textContent = "二维码已过期，请重新生成。";
                return;
            }
            status.textContent = `${Math.floor(remaining / 60)}:${String(remaining % 60).padStart(2, "0")} 后失效`;
            remaining -= 1;
        };
        tick();
        mobileLoginTimer = window.setInterval(tick, 1000);
    } catch {
        status.textContent = "生成失败，请刷新页面后重试。";
    } finally {
        button.disabled = false;
    }
});

document.addEventListener("click", async event => {
    if (await handleCopyClick(event)) return;
    if (handleDialogClick(event)) return;
    if (handleSidebarClick(event)) return;
    handlePageActionClick(event);
});

// 管理员为登录页强制了主题时不要再用本地偏好覆盖它；data-login-theme-forced 由布局写入。
const forcedLoginTheme = document.documentElement.dataset.loginThemeForced;
if (forcedLoginTheme) document.documentElement.dataset.theme = forcedLoginTheme;
else {
    const savedTheme = localStorage.getItem("remoteci-theme");
    if (savedTheme === "dark") document.documentElement.dataset.theme = "dark";
}

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
        icon?.classList.toggle("icon-volume-off", muted);
        icon?.classList.toggle("icon-volume-up", !muted);
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

// 课表页直接换课：日期可选今天起 30 天内任意一天，按所选日期填充原节次与同一天的目标节次。
// 手机课表点某节课时派发 schedule-pick 事件（detail: { date, index }）预选该课。
document.querySelectorAll("[data-schedule-change-form]").forEach(form => {
    const days = JSON.parse(form.querySelector("[data-schedule-days]")?.textContent || "[]");
    const mode = form.querySelector("[data-schedule-mode]");
    const dateInput = form.querySelector("[data-schedule-date]");
    const dateHint = form.querySelector("[data-schedule-date-hint]");
    const revision = form.querySelector("[data-schedule-revision]");
    const source = form.querySelector("[data-schedule-source]");
    const target = form.querySelector("[data-schedule-target]");
    const exchangeField = form.querySelector("[data-exchange-field]");
    const replaceField = form.querySelector("[data-replace-field]");
    const targetInput = exchangeField?.querySelector("input, select");
    const replacementInput = replaceField?.querySelector("input, select");
    if (!mode || !dateInput || !source) return;

    const option = (value, text) => {
        const element = document.createElement("option");
        element.value = value;
        element.textContent = text;
        return element;
    };
    const selectedDay = () => days.find(day => day.date === dateInput.value);
    const setHint = text => {
        if (!dateHint) return;
        dateHint.textContent = text;
        dateHint.hidden = !text;
    };

    const fillTargets = () => {
        if (!target) return;
        const previous = target.value;
        const courses = (selectedDay()?.courses || []).filter(course => String(course.index) !== source.value);
        target.replaceChildren(option("", "请选择目标节次"), ...courses.map(course => option(String(course.index), `${course.label} · ${course.subject}`)));
        if (courses.some(course => String(course.index) === previous)) target.value = previous;
    };

    const fillDay = preferredIndex => {
        const day = selectedDay();
        const courses = day?.courses || [];
        if (revision) revision.value = day?.revision ?? "";
        source.replaceChildren(...courses.map(course => option(String(course.index), `${course.label} · ${course.subject}`)));
        if (preferredIndex !== undefined && courses.some(course => String(course.index) === String(preferredIndex)))
            source.value = String(preferredIndex);
        if (!dateInput.value) setHint("");
        else if (!day) setHint(dateInput.max && dateInput.value > dateInput.max
            ? `教室端只同步到 ${dateInput.max} 的课表，更远的日期需要升级教室端插件。`
            : "请选择今天起 30 天内的日期。");
        else setHint(courses.length ? "" : "这一天没有课。");
        fillTargets();
    };

    const syncModeFields = () => {
        const exchange = mode.value === "Exchange" || mode.value === "1";
        if (exchangeField) exchangeField.hidden = !exchange;
        if (replaceField) replaceField.hidden = exchange;
        if (targetInput) targetInput.disabled = !exchange;
        if (replacementInput) replacementInput.disabled = exchange;
    };

    mode.addEventListener("change", syncModeFields);
    dateInput.addEventListener("change", () => fillDay());
    source.addEventListener("change", fillTargets);
    form.addEventListener("schedule-pick", event => {
        const { date, index } = event.detail || {};
        if (date) dateInput.value = date;
        fillDay(index);
    });
    if (!dateInput.value) dateInput.value = days.find(day => day.courses.length)?.date ?? dateInput.min;
    fillDay();
    syncModeFields();
});


document.querySelectorAll("[data-role-form]").forEach(form => {
    const roleSelect = form.querySelector("[data-role-select]");
    roleSelect?.addEventListener("change", () => syncRolePermissions(form, true));
    syncRolePermissions(form);
});

// 登录页与设置新密码的密码框只接受英文字母、数字和英文符号（可见 ASCII 字符）：中文、全角符号、空格等直接过滤并提示。
document.querySelectorAll("[data-ascii-password]").forEach(input => {
    const hint = document.createElement("small");
    hint.className = "form-error";
    hint.textContent = "密码只能输入英文字母、数字和英文符号";
    hint.hidden = true;
    input.insertAdjacentElement("afterend", hint);
    // 被过滤时在输入框下方常驻红字，离开输入框后才收起，避免一闪而过像是按键没反应。
    const strip = () => {
        const cleaned = input.value.replace(/[^!-~]/g, "");
        if (cleaned === input.value) return;
        const caret = Math.max(0, (input.selectionStart ?? cleaned.length) - (input.value.length - cleaned.length));
        input.value = cleaned;
        try { input.setSelectionRange(caret, caret); } catch { }
        hint.hidden = false;
    };
    input.addEventListener("input", event => { if (!event.isComposing) strip(); });
    input.addEventListener("compositionend", strip);
    input.addEventListener("blur", () => { hint.hidden = true; });
    strip();
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


// 自动下载依赖自动检查：关闭自动检查时同时禁用自动下载。
document.querySelectorAll("[data-update-settings-form]").forEach(form => {
    const autoCheck = form.querySelector("[data-update-auto-check]");
    const autoDownload = form.querySelector("[data-update-auto-download]");
    if (!autoCheck || !autoDownload) return;
    const sync = () => {
        autoDownload.disabled = !autoCheck.checked;
        if (!autoCheck.checked) autoDownload.checked = false;
    };
    autoCheck.addEventListener("change", sync);
    sync();
});

// 班级分组树：折叠状态只影响当前页面，不改变服务端选中的分组范围。
document.querySelectorAll("[data-class-tree-toggle]").forEach(button => {
    const item = button.closest(".class-tree-item");
    const children = item?.querySelector(":scope > [data-class-tree-children]");
    if (!children) return;
    button.addEventListener("click", () => {
        const expanded = button.getAttribute("aria-expanded") !== "false";
        button.setAttribute("aria-expanded", expanded ? "false" : "true");
        children.hidden = expanded;
    });
});

// 班级批量管理：选择、范围全选、按操作类型切换目标分组字段。
const classBatchForm = document.querySelector("[data-class-batch-form]");
if (classBatchForm) {
    const operation = classBatchForm.querySelector("[data-batch-operation]");
    const groupField = classBatchForm.querySelector("[data-batch-group-field]");
    const groupSelect = classBatchForm.querySelector("[data-batch-group-select]");
    const submit = classBatchForm.querySelector("[data-batch-submit]");
    const selection = classBatchForm.querySelector("[data-batch-selection]");
    const selectAll = document.querySelector("[data-select-all-classes]");
    const classChecks = () => [...document.querySelectorAll("[data-class-select]")]
        .filter(input => input.form === classBatchForm);

    const needsTargetGroups = () => ["addgroups", "removegroups", "replacegroups"]
        .includes((operation?.value || "").toLowerCase());
    const isGroupOperation = () => needsTargetGroups() || (operation?.value || "").toLowerCase() === "cleargroups";

    const syncGroupField = () => {
        const required = needsTargetGroups();
        if (groupField) groupField.hidden = !isGroupOperation();
        if (groupSelect) {
            groupSelect.disabled = !required;
            groupSelect.required = required && !groupField?.hidden;
        }
    };

    const syncSelection = () => {
        const checkboxes = classChecks();
        const selectable = checkboxes.filter(input => !input.disabled);
        const selected = selectable.filter(input => input.checked);
        if (selection) selection.textContent = `已选 ${selected.length} 个班级`;
        if (submit) submit.disabled = selected.length === 0;
        if (selectAll) {
            selectAll.checked = selectable.length > 0 && selected.length === selectable.length;
            selectAll.indeterminate = selected.length > 0 && selected.length < selectable.length;
        }
    };

    selectAll?.addEventListener("change", () => {
        classChecks().forEach(input => {
            if (!input.disabled) input.checked = selectAll.checked;
        });
        syncSelection();
    });

    classChecks().forEach(input => input.addEventListener("change", syncSelection));
    operation?.addEventListener("change", () => {
        syncGroupField();
        syncSelection();
    });

    classBatchForm.addEventListener("submit", event => {
        const selected = classChecks().filter(input => input.checked && !input.disabled);
        if (selected.length === 0) {
            event.preventDefault();
            alert("请先勾选要操作的班级。");
            return;
        }
        if (needsTargetGroups() && groupSelect && groupSelect.selectedOptions.length === 0) {
            event.preventDefault();
            alert("请选择要批量调整到的目标分组。");
            groupSelect.focus();
            return;
        }
        if ((operation?.value || "").toLowerCase() === "delete" &&
            !confirm("删除选中的班级会同时移除成员关系与插件凭据，需重新配对。确定继续？")) {
            event.preventDefault();
        }
    });

    syncGroupField();
    syncSelection();
}

// 菜单只保留一个展开项，避免树节点和行操作菜单互相遮挡。
document.addEventListener("click", event => {
    document.querySelectorAll(".tree-node-menu[open], .row-menu[open], .class-menu[open], .account-menu[open]").forEach(menu => {
        if (!menu.contains(event.target)) menu.removeAttribute("open");
    });
});

document.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    const openMenu = document.querySelector(".class-menu[open], .account-menu[open]");
    if (!openMenu) return;
    openMenu.removeAttribute("open");
    openMenu.querySelector("summary")?.focus();
});

// 通知正文较长而未开启滚动时，静态正文可能显示不全；只提示，不替用户改选项。
function syncRollingHint(scope) {
    const hint = scope.querySelector("[data-rolling-hint]");
    const message = scope.querySelector("[data-rolling-message]");
    const toggle = scope.querySelector("[data-rolling-toggle]");
    if (!hint || !message || !toggle) return;
    const threshold = Number(hint.dataset.rollingHint) || 30;
    hint.hidden = toggle.checked || [...message.value.trim()].length <= threshold;
}

["input", "change"].forEach(type => document.addEventListener(type, event => {
    const scope = event.target.closest?.("[data-rolling-scope]");
    if (scope) syncRollingHint(scope);
}));

// 侧边栏分组折叠：状态按分组记在本地；含当前页的分组不折叠，避免当前位置被藏起来。
(() => {
    const key = "remoteci-nav-collapsed";
    let collapsed = [];
    try { collapsed = JSON.parse(localStorage.getItem(key) || "[]"); } catch { collapsed = []; }
    document.querySelectorAll("[data-nav-group]").forEach(group => {
        const toggle = group.querySelector("[data-nav-group-toggle]");
        const apply = value => {
            group.classList.toggle("collapsed", value);
            toggle?.setAttribute("aria-expanded", value ? "false" : "true");
        };
        apply(collapsed.includes(group.dataset.navGroup) && !group.querySelector(".nav-link.active"));
        toggle?.addEventListener("click", () => {
            const next = !group.classList.contains("collapsed");
            apply(next);
            collapsed = collapsed.filter(name => name !== group.dataset.navGroup);
            if (next) collapsed.push(group.dataset.navGroup);
            try { localStorage.setItem(key, JSON.stringify(collapsed)); } catch { }
        });
    });
})();
