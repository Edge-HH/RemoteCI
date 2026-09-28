(() => {
    const settingsDialogs = [...document.querySelectorAll("[data-batch-settings]")];
    const targetDialog = document.querySelector("[data-batch-target-dialog]");
    const targetForm = document.querySelector("[data-batch-execute-form]");
    const payload = document.querySelector("[data-batch-settings-payload]");
    const operationInput = document.querySelector("[data-batch-operation]");
    const targetTitle = document.querySelector("[data-batch-target-title]");
    const targetDescription = document.querySelector("[data-batch-target-description]");
    const targetFeedback = document.querySelector("[data-batch-target-feedback]");
    const riskConfirm = document.querySelector("[data-batch-risk-confirm]");
    const voiceSend = document.querySelector("[data-batch-voice-send]");
    const submitButton = document.querySelector("[data-batch-submit]");
    const voiceDialog = document.querySelector("[data-batch-voice-dialog]");
    const voiceForm = document.querySelector("[data-voice-form]");
    const singleControl = document.querySelector("[data-single-control]")?.dataset.singleControl === "true";
    if (!targetDialog || !targetForm || !payload || !operationInput) return;

    let currentOperation = null;
    let currentSettingsDialog = null;

    const openDialog = dialog => {
        if (!dialog) return;
        if (typeof dialog.showModal === "function") dialog.showModal();
        else dialog.setAttribute("open", "");
    };
    const closeDialog = dialog => {
        if (!dialog) return;
        if (typeof dialog.close === "function") dialog.close();
        else dialog.removeAttribute("open");
    };
    const closeNearest = element => closeDialog(element?.closest("dialog"));
    const settingsDialogFor = operation => settingsDialogs.find(dialog => dialog.dataset.batchSettings === operation);

    const addHidden = (name, value) => {
        const input = document.createElement("input");
        input.type = "hidden";
        input.name = name;
        input.value = value ?? "";
        payload.appendChild(input);
    };

    const serializeTimeLayout = dialog => {
        const rows = [...dialog.querySelectorAll("[data-time-layout-rows] .time-layout-row")];
        const points = rows.map(row => ({
            startTime: row.querySelector("[data-time-layout-start]")?.value ?? "",
            endTime: row.querySelector("[data-time-layout-end]")?.value ?? "",
            timeType: Number(row.querySelector("[data-time-layout-type]")?.value ?? 0),
            breakName: row.querySelector("[data-time-layout-break]")?.value?.trim() || null,
        })).filter(point => point.startTime && point.endTime);
        const hidden = dialog.querySelector("[data-time-layout-json]");
        if (hidden) hidden.value = JSON.stringify(points);
    };

    const copySettings = () => {
        payload.innerHTML = "";
        if (!currentSettingsDialog) return;
        if (currentOperation === "UpdateTimeLayout") serializeTimeLayout(currentSettingsDialog);
        currentSettingsDialog.querySelectorAll("input, select, textarea").forEach(field => {
            if (!field.name || field.disabled || field.type === "file") return;
            if (field.type === "checkbox") {
                if (field.dataset.batchBool !== undefined) {
                    addHidden(field.name, field.checked ? "true" : "false");
                } else if (field.checked) {
                    addHidden(field.name, field.value || "true");
                }
                return;
            }
            if (field.tagName === "SELECT" && field.multiple) {
                [...field.selectedOptions].forEach(option => addHidden(field.name, option.value));
                return;
            }
            addHidden(field.name, field.value);
        });
    };

    const countTargets = () =>
        targetDialog.querySelectorAll("input[data-voice-query]:checked:not(:disabled)").length;

    const validateTargets = () => {
        targetFeedback.textContent = "";
        if (!singleControl && countTargets() === 0) {
            targetFeedback.textContent = "请至少选择一个在线设备、班级或分组。";
            return false;
        }
        if (!riskConfirm.hidden) {
            const checkbox = riskConfirm.querySelector('input[type="checkbox"]');
            if (!checkbox?.checked) {
                targetFeedback.textContent = "请先勾选确认，再执行高风险操作。";
                return false;
            }
        }
        return true;
    };

    const setTargetMode = mode => {
        targetDialog.querySelectorAll("[data-batch-target-panel]").forEach(panel => {
            panel.hidden = panel.dataset.batchTargetPanel !== mode;
        });
        targetDialog.querySelectorAll("[data-batch-target-tab]").forEach(tab => {
            tab.classList.toggle("active", tab.dataset.batchTargetTab === mode);
        });
    };

    const openTargetDialog = (operation, title, risk, settingsDialog) => {
        currentOperation = operation;
        currentSettingsDialog = settingsDialog;
        operationInput.value = operation;
        targetForm.action = `${location.pathname}?handler=${singleControl ? "SingleExecute" : "Execute"}`;
        targetTitle.textContent = title || "选择设备";
        const voice = operation === "VoiceMessage";
        targetDescription.textContent = voice
            ? "选择要广播语音的班级、分组或具体设备。"
            : `选择要执行“${title || "该功能"}”的班级、分组或具体设备。`;
        targetFeedback.textContent = "";
        riskConfirm.hidden = !risk;
        const riskCheckbox = riskConfirm.querySelector('input[type="checkbox"]');
        if (riskCheckbox) riskCheckbox.checked = false;
        // 语音消息此时已经录完音，这里只负责选目标，发送由录音弹窗触发。
        if (voiceSend) voiceSend.hidden = !voice;
        submitButton.hidden = voice;
        if (settingsDialog) closeDialog(settingsDialog);
        openDialog(targetDialog);
        setTargetMode("classes");
        if (singleControl) {
            targetDialog.querySelector("[data-batch-target-panel=devices]")?.setAttribute("hidden", "");
            targetDialog.querySelector("[data-batch-target-panel=classes]")?.classList.add("single-control-target");
            targetDialog.querySelector("[data-batch-target-description]").textContent = "确认后执行于当前班级，无需选择目标。";
        }
    };

    const addTimeLayoutRow = dialog => {
        const template = document.getElementById("time-layout-row-template");
        const row = template?.content?.firstElementChild?.cloneNode(true);
        if (!row) return;
        dialog.querySelector("[data-time-layout-rows]")?.appendChild(row);
    };


    const openOperation = (operation, title, risk) => {
        if (singleControl && ["ClearNotifications", "RefreshSoftwareInventory"].includes(operation)) {
            operationInput.value = operation;
            targetForm.action = `${location.pathname}?handler=SingleExecute`;
            payload.innerHTML = "";
            targetForm.submit();
            return;
        }
        // 语音消息先录制再选目标，因此直接打开录音弹窗。
        if (operation === "VoiceMessage") {
            currentOperation = operation;
            currentSettingsDialog = null;
            const voiceHint = document.querySelector("[data-batch-voice-targets]");
            if (voiceHint) voiceHint.textContent = singleControl
                ? "录制并试听后直接发送到当前班级。"
                : "先录制语音，再选择要广播的班级或设备。";
            openDialog(voiceDialog);
            return;
        }
        if (!settingsDialogFor(operation)) {
            openTargetDialog(operation, title, risk, null);
            return;
        }
        currentOperation = operation;
        currentSettingsDialog = settingsDialogFor(operation);
        openDialog(currentSettingsDialog);
        if (operation === "UpdateTimeLayout" && currentSettingsDialog.querySelectorAll("[data-time-layout-rows] .time-layout-row").length === 0) {
            addTimeLayoutRow(currentSettingsDialog);
        }
        currentSettingsDialog.querySelector("input:not([type=hidden]), select, textarea")?.focus();
    };

    const findTarget = (event, selector) => event.target.closest(selector);
    const handleOpenClick = event => {
        const button = findTarget(event, "[data-batch-open]");
        if (!button) return false;
        openOperation(button.dataset.batchOpen, button.dataset.batchTitle, button.dataset.batchRisk === "true");
        return true;
    };
    const handleCloseClick = event => {
        const button = findTarget(event, "[data-batch-close]");
        if (!button) return false;
        closeNearest(button);
        return true;
    };
    const handleNextClick = event => {
        const button = findTarget(event, "[data-batch-next]");
        const dialog = button?.closest("dialog");
        if (!dialog) return false;
        currentOperation = dialog.dataset.batchSettings;
        currentSettingsDialog = dialog;
        copySettings();
        openTargetDialog(currentOperation, dialog.dataset.batchTitle, dialog.dataset.batchRisk === "true", dialog);
        return true;
    };
    const handleBackClick = event => {
        if (!findTarget(event, "[data-batch-back]")) return false;
        closeDialog(targetDialog);
        if (currentOperation === "VoiceMessage") openDialog(voiceDialog);
        else if (currentSettingsDialog) openDialog(currentSettingsDialog);
        return true;
    };
    const handleVoiceTargetClick = event => {
        if (!findTarget(event, "[data-voice-next-to-targets]")) return false;
        if (singleControl) voiceForm?.requestSubmit();
        else {
            closeDialog(voiceDialog);
            openTargetDialog("VoiceMessage", "语音消息", false, null);
        }
        return true;
    };
    const handleVoiceSendClick = event => {
        if (!findTarget(event, "[data-batch-voice-send]") || !validateTargets()) return false;
        closeDialog(targetDialog);
        openDialog(voiceDialog);
        voiceForm?.requestSubmit();
        return true;
    };
    const handleTargetTabClick = event => {
        const tab = findTarget(event, "[data-batch-target-tab]");
        if (!tab) return false;
        setTargetMode(tab.dataset.batchTargetTab);
        return true;
    };
    const handleSelectOnlineClick = event => {
        if (!findTarget(event, "[data-batch-select-online]")) return false;
        const boxes = [...targetDialog.querySelectorAll('[data-batch-target-panel="devices"] input[name="SelectedConnectionIds"]:not(:disabled)')];
        const allChecked = boxes.length > 0 && boxes.every(box => box.checked);
        boxes.forEach(box => { box.checked = !allChecked; });
        return true;
    };
    const handleTimeLayoutClick = event => {
        const add = findTarget(event, "[data-time-layout-add]");
        if (add) {
            addTimeLayoutRow(add.closest("dialog"));
            return true;
        }
        const remove = findTarget(event, "[data-time-layout-remove]");
        if (remove) {
            remove.closest(".time-layout-row")?.remove();
            return true;
        }
        return false;
    };
    const clickHandlers = [handleOpenClick, handleCloseClick, handleNextClick, handleBackClick,
        handleVoiceTargetClick, handleVoiceSendClick, handleTargetTabClick, handleSelectOnlineClick,
        handleTimeLayoutClick];
    document.addEventListener("click", event => {
        if (clickHandlers.some(handler => handler(event))) return;
        if (event.target.matches("dialog.batch-dialog")) closeDialog(event.target);
    });

    targetForm.addEventListener("submit", event => {
        if (!validateTargets()) event.preventDefault();
    });

    document.querySelectorAll("[data-profile-file]").forEach(input => {
        input.addEventListener("change", async () => {
            const file = input.files?.[0];
            if (!file) return;
            const textarea = input.closest("dialog")?.querySelector('textarea[name="ProfileJson"]');
            if (!textarea) return;
            textarea.value = await file.text();
        });
    });

    // 集控配置文件：选择 ManagementPreset.json 后填入 JSON 文本框，再由表单下发。
    document.querySelectorAll("[data-management-preset-file]").forEach(input => {
        input.addEventListener("change", async () => {
            const file = input.files?.[0];
            if (!file) return;
            const textarea = input.closest("dialog")?.querySelector('textarea[name="ManagementPresetJson"]');
            if (!textarea) return;
            textarea.value = await file.text();
        });
    });
})();
