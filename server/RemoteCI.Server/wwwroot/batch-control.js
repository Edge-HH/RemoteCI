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

    document.addEventListener("click", event => {
        const openButton = event.target.closest("[data-batch-open]");
        if (openButton) {
            openOperation(openButton.dataset.batchOpen, openButton.dataset.batchTitle, openButton.dataset.batchRisk === "true");
            return;
        }

        const closeButton = event.target.closest("[data-batch-close]");
        if (closeButton) {
            closeNearest(closeButton);
            return;
        }

        const nextButton = event.target.closest("[data-batch-next]");
        if (nextButton) {
            const dialog = nextButton.closest("dialog");
            if (dialog) {
                currentOperation = dialog.dataset.batchSettings;
                currentSettingsDialog = dialog;
                copySettings();
                openTargetDialog(currentOperation, dialog.dataset.batchTitle, dialog.dataset.batchRisk === "true", dialog);
            }
            return;
        }

        const backButton = event.target.closest("[data-batch-back]");
        if (backButton) {
            closeDialog(targetDialog);
            // 语音消息的“上一步”回到录音弹窗，其他功能回到参数弹窗。
            if (currentOperation === "VoiceMessage") openDialog(voiceDialog);
            else if (currentSettingsDialog) openDialog(currentSettingsDialog);
            return;
        }

        // 录音完成后进入目标选择。
        const voiceToTargets = event.target.closest("[data-voice-next-to-targets]");
        if (voiceToTargets) {
            if (singleControl) {
                voiceForm?.requestSubmit();
                return;
            }
            closeDialog(voiceDialog);
            openTargetDialog("VoiceMessage", "语音消息", false, null);
            return;
        }

        // 目标确认后回到录音弹窗发送：录音仍在 voice-message.js 的内存里，状态也显示在录音弹窗。
        const voiceSendButton = event.target.closest("[data-batch-voice-send]");
        if (voiceSendButton) {
            if (!validateTargets()) return;
            closeDialog(targetDialog);
            openDialog(voiceDialog);
            voiceForm?.requestSubmit();
            return;
        }

        const tab = event.target.closest("[data-batch-target-tab]");
        if (tab) {
            setTargetMode(tab.dataset.batchTargetTab);
            return;
        }

        const selectOnline = event.target.closest("[data-batch-select-online]");
        if (selectOnline) {
            const boxes = [...targetDialog.querySelectorAll('[data-batch-target-panel="devices"] input[name="SelectedConnectionIds"]:not(:disabled)')];
            const allChecked = boxes.length > 0 && boxes.every(box => box.checked);
            boxes.forEach(box => { box.checked = !allChecked; });
            return;
        }

        const addRow = event.target.closest("[data-time-layout-add]");
        if (addRow) {
            addTimeLayoutRow(addRow.closest("dialog"));
            return;
        }

        const removeRow = event.target.closest("[data-time-layout-remove]");
        if (removeRow) {
            removeRow.closest(".time-layout-row")?.remove();
            return;
        }

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
