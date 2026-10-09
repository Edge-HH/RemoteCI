(() => {
    "use strict";
    const clone = value => JSON.parse(JSON.stringify(value));
    const keyFor = (node, name) => Object.keys(node || {}).find(key => key.toLowerCase() === name.toLowerCase()) || name;
    const read = (node, name, fallback) => node?.[keyFor(node, name)] ?? fallback;
    const write = (node, name, value) => { node[keyFor(node, name)] = value; };
    const dictionary = (doc, section) => read(doc, section, {});
    const entries = (doc, section) => Object.entries(dictionary(doc, section));
    const sameId = (left, right) => String(left || "").toLowerCase() === String(right || "").toLowerCase();
    // ClassIsland 用全零 GUID 表示“无默认科目/空课”，宿主保存的档案里随处可见，不能当作缺失引用。
    const EMPTY_ID = "00000000-0000-0000-0000-000000000000";
    const refOf = value => sameId(value, EMPTY_ID) ? "" : value || "";
    const lookup = (map, id) => map[Object.keys(map).find(key => sameId(key, id))];
    const slots = layout => read(layout, "Layouts", []);
    const isLesson = point => Number(read(point, "TimeType", 0)) === 0;
    const courses = plan => read(plan, "Classes", []);
    const lessonIndex = (layout, index) => slots(layout).slice(0, index).filter(isLesson).length;
    const relatedPlans = (doc, layoutId) => entries(doc, "ClassPlans").filter(([, plan]) => sameId(read(plan, "TimeLayoutId", ""), layoutId));
    const emptyCourse = () => ({ SubjectId: "" });
    const blankProfile = name => ({ Name: name, TimeLayouts: {}, ClassPlans: {}, Subjects: {} });
    const pointTimeField = (point, start) => read(point, start ? "StartTime" : "EndTime", "") !== "" ? (start ? "StartTime" : "EndTime") : Object.keys(point || {}).some(key => key.toLowerCase() === (start ? "startsecond" : "endsecond")) ? (start ? "StartSecond" : "EndSecond") : (start ? "StartTime" : "EndTime");
    const pointTime = (point, start) => read(point, pointTimeField(point, start));
    const timeText = value => {
        if (typeof value === "number") {
            const seconds = Math.round(value);
            return `${String(Math.floor(seconds / 3600)).padStart(2, "0")}:${String(Math.floor(seconds % 3600 / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
        }
        const match = String(value || "").match(/(?:T|^)(\d{2}:\d{2}(?::\d{2})?)/);
        return match ? match[1].padEnd(8, ":00") : "";
    };
    const timeSeconds = value => {
        const text = timeText(value);
        const parts = text.split(":").map(Number);
        return /^\d{2}:\d{2}:\d{2}$/.test(text) && parts[0] < 24 && parts[1] < 60 && parts[2] < 60
            ? parts[0] * 3600 + parts[1] * 60 + parts[2] : NaN;
    };
    const editedTime = (original, text) => {
        const seconds = timeSeconds(text);
        if (typeof original === "number" || original == null) return Number.isFinite(seconds) ? seconds : text;
        // 旧档案使用 DateTime，新版使用一天中的秒数。只改时间，保留旧日期及时区后缀。
        const formatted = text.length === 5 ? `${text}:00` : text;
        return String(original).includes("T") ? String(original).replace(/T\d{2}:\d{2}:\d{2}(?:\.\d+)?/, `T${formatted}`) : formatted;
    };
    const ensureCourses = (plan, count) => {
        const value = courses(plan);
        while (value.length < count) value.push(emptyCourse());
        write(plan, "Classes", value);
        return value;
    };
    const affectedCourses = (doc, layoutId, index) => {
        const layout = dictionary(doc, "TimeLayouts")[layoutId];
        if (!isLesson(slots(layout)[index])) return [];
        const ordinal = lessonIndex(layout, index);
        return relatedPlans(doc, layoutId).filter(([, plan]) => refOf(read(courses(plan)[ordinal], "SubjectId", "")));
    };
    const insertSlot = (doc, layoutId, index, point = { StartTime: "08:00:00", EndTime: "08:45:00", TimeType: 0, DefaultClassId: "", IsHideDefault: false }) => {
        const layout = dictionary(doc, "TimeLayouts")[layoutId];
        const oldCount = slots(layout).filter(isLesson).length;
        const ordinal = lessonIndex(layout, index);
        if (isLesson(point)) relatedPlans(doc, layoutId).forEach(([, plan]) => ensureCourses(plan, oldCount).splice(ordinal, 0, emptyCourse()));
        slots(layout).splice(index, 0, clone(point));
    };
    const removeSlot = (doc, layoutId, index) => {
        const layout = dictionary(doc, "TimeLayouts")[layoutId];
        if (isLesson(slots(layout)[index])) {
            const ordinal = lessonIndex(layout, index);
            relatedPlans(doc, layoutId).forEach(([, plan]) => courses(plan).splice(ordinal, 1));
        }
        slots(layout).splice(index, 1);
    };
    const changeSlotType = (doc, layoutId, index, type) => {
        const layout = dictionary(doc, "TimeLayouts")[layoutId];
        const point = slots(layout)[index];
        const previous = isLesson(point);
        const ordinal = lessonIndex(layout, index);
        const next = Number(type) === 0;
        if (previous !== next) relatedPlans(doc, layoutId).forEach(([, plan]) => {
            const values = ensureCourses(plan, slots(layout).filter(isLesson).length);
            if (next) values.splice(ordinal, 0, emptyCourse()); else values.splice(ordinal, 1);
        });
        write(point, "TimeType", Number(type));
    };
    const moveSlot = (doc, layoutId, index, newIndex) => {
        const layout = dictionary(doc, "TimeLayouts")[layoutId];
        const values = slots(layout);
        if (newIndex < 0 || newIndex >= values.length || index === newIndex) return;
        const point = values[index];
        const ordinal = lessonIndex(layout, index);
        const saved = isLesson(point) ? relatedPlans(doc, layoutId).map(([, plan]) => ({ plan, course: ensureCourses(plan, values.filter(isLesson).length).splice(ordinal, 1)[0] })) : [];
        values.splice(index, 1);
        values.splice(newIndex, 0, point);
        const nextOrdinal = lessonIndex(layout, newIndex);
        saved.forEach(({ plan, course }) => courses(plan).splice(nextOrdinal, 0, course));
    };
    const uid = () => globalThis.crypto?.randomUUID?.() || "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, c => { const r = Math.random() * 16 | 0; return (c === "x" ? r : r & 3 | 8).toString(16); });
    // 临时层（IsOverlay）只在 OrderedSchedules 指定的日期替换当天课表，不属于常规时间表/课表类别。
    const isOverlay = node => read(node, "IsOverlay", false) === true;
    const regularEntries = (doc, section) => entries(doc, section).filter(([, item]) => !isOverlay(item));
    const ordered = doc => { const value = read(doc, "OrderedSchedules", null); if (value && typeof value === "object") return value; write(doc, "OrderedSchedules", {}); return read(doc, "OrderedSchedules"); };
    const dateOfKey = key => String(key).slice(0, 10);
    const dateKey = date => `${date}T00:00:00`;
    const todayText = (now = new Date()) => `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
    const isDateText = value => /^\d{4}-\d{2}-\d{2}$/.test(value || "") && !Number.isNaN(Date.parse(`${value}T00:00:00`));
    const keysOn = (doc, date) => Object.keys(read(doc, "OrderedSchedules", {}) || {}).filter(key => dateOfKey(key) === date);
    const dropDatesOf = (doc, planId) => { const schedule = ordered(doc); Object.keys(schedule).filter(key => sameId(read(schedule[key], "ClassPlanId", ""), planId)).forEach(key => delete schedule[key]); };
    const requireFreeDate = (doc, date, planId = null) => {
        if (!isDateText(date)) throw new Error("请选择有效的日期。");
        if (keysOn(doc, date).some(key => !planId || !sameId(read(ordered(doc)[key], "ClassPlanId", ""), planId))) throw new Error(`${date} 已有临时层或预定课表，每天只能安排一个。`);
    };
    const tempLayers = doc => {
        const plans = dictionary(doc, "ClassPlans");
        const dates = new Map();
        Object.entries(read(doc, "OrderedSchedules", {}) || {}).forEach(([key, value]) => { const id = read(value, "ClassPlanId", ""); const plan = Object.keys(plans).find(planId => sameId(planId, id)); if (plan && !dates.has(plan)) dates.set(plan, dateOfKey(key)); });
        return Object.entries(plans).filter(([, plan]) => isOverlay(plan)).map(([id, plan]) => {
            const sourceId = read(plan, "OverlaySourceId", "") || "";
            return { id, plan, date: dates.get(id) || "", sourceId, source: sourceId ? lookup(plans, sourceId) || null : null };
        }).sort((a, b) => (a.date || "9999").localeCompare(b.date || "9999") || String(read(a.plan, "Name", "")).localeCompare(String(read(b.plan, "Name", ""))));
    };
    const pruneOverlayLayouts = doc => {
        const used = new Set(Object.values(dictionary(doc, "ClassPlans")).map(plan => String(read(plan, "TimeLayoutId", "")).toLowerCase()));
        const layouts = dictionary(doc, "TimeLayouts");
        Object.keys(layouts).filter(id => isOverlay(layouts[id]) && !used.has(id.toLowerCase())).forEach(id => delete layouts[id]);
    };
    const clearPointers = (doc, id) => ["OverlayClassPlanId", "TempClassPlanId"].forEach(field => { if (sameId(read(doc, field, ""), id)) write(doc, field, EMPTY_ID); });
    const markChanged = (doc, planId) => {
        const plan = dictionary(doc, "ClassPlans")[planId];
        if (!isOverlay(plan)) return;
        const source = lookup(dictionary(doc, "ClassPlans"), read(plan, "OverlaySourceId", ""));
        const before = source ? courses(source) : null;
        courses(plan).forEach((course, i) => write(course, "IsChangedClass", !!before && before.length === courses(plan).length && !sameId(refOf(read(course, "SubjectId", "")), refOf(read(before[i], "SubjectId", "")))));
    };
    const createTempLayer = (doc, sourcePlanId, date, separateTime = false) => {
        const plans = dictionary(doc, "ClassPlans");
        const source = plans[sourcePlanId];
        if (!source || isOverlay(source)) throw new Error("请选择一张常规课表作为临时层来源。");
        requireFreeDate(doc, date);
        const plan = clone(source);
        write(plan, "Name", `${read(source, "Name", "课表")}（临时层）`);
        write(plan, "IsOverlay", true);
        write(plan, "OverlaySourceId", sourcePlanId);
        write(plan, "OverlaySetupTime", dateKey(date));
        courses(plan).forEach(course => write(course, "IsChangedClass", false));
        const id = uid();
        plans[id] = plan;
        write(doc, "ClassPlans", plans);
        if (separateTime) separateTempTime(doc, id);
        ordered(doc)[dateKey(date)] = { ClassPlanId: id };
        return id;
    };
    // 与宿主 CreateTempClassPlan(createTempTimeLayout: true) 一致：复制时间表为临时层时间表，不改动常规时间表。
    const separateTempTime = (doc, planId) => {
        const plan = dictionary(doc, "ClassPlans")[planId];
        const layouts = dictionary(doc, "TimeLayouts");
        const layoutId = read(plan, "TimeLayoutId", "");
        const layout = lookup(layouts, layoutId);
        if (!layout) throw new Error("临时层没有有效的时间表。");
        if (isOverlay(layout)) return layoutId;
        const copy = clone(layout);
        write(copy, "Name", `${read(layout, "Name", "时间表")}（临时层）`);
        write(copy, "IsOverlay", true);
        write(copy, "OverlaySourceId", layoutId);
        const id = uid();
        layouts[id] = copy;
        write(doc, "TimeLayouts", layouts);
        write(plan, "TimeLayoutId", id);
        return id;
    };
    const moveTempLayer = (doc, planId, date) => {
        requireFreeDate(doc, date, planId);
        dropDatesOf(doc, planId);
        ordered(doc)[dateKey(date)] = { ClassPlanId: planId };
        write(dictionary(doc, "ClassPlans")[planId], "OverlaySetupTime", dateKey(date));
    };
    const deleteTempLayer = (doc, planId) => {
        dropDatesOf(doc, planId);
        delete dictionary(doc, "ClassPlans")[planId];
        clearPointers(doc, planId);
        pruneOverlayLayouts(doc);
    };
    // 与宿主 CleanExpiredTempClassPlan 一致：去掉早于今天的日期，删除不再被日期引用的临时层及其时间表。
    const cleanExpiredTempLayers = (doc, today = todayText()) => {
        const schedule = ordered(doc);
        Object.keys(schedule).filter(key => dateOfKey(key) < today).forEach(key => delete schedule[key]);
        const referenced = new Set(Object.values(schedule).map(value => String(read(value, "ClassPlanId", "")).toLowerCase()));
        const plans = dictionary(doc, "ClassPlans");
        const removed = Object.keys(plans).filter(id => isOverlay(plans[id]) && !referenced.has(id.toLowerCase()));
        removed.forEach(id => { delete plans[id]; clearPointers(doc, id); });
        pruneOverlayLayouts(doc);
        return removed.length;
    };
    // 删除常规课表时一并移除指向它的预定课表，并清空以它为来源的临时层的来源指针。
    const deletePlan = (doc, planId) => {
        dropDatesOf(doc, planId);
        Object.values(dictionary(doc, "ClassPlans")).filter(plan => sameId(read(plan, "OverlaySourceId", ""), planId)).forEach(plan => write(plan, "OverlaySourceId", null));
        delete dictionary(doc, "ClassPlans")[planId];
        clearPointers(doc, planId);
    };
    const validateProfile = doc => {
        const errors = [];
        const layouts = dictionary(doc, "TimeLayouts");
        const subjects = dictionary(doc, "Subjects");
        const plans = dictionary(doc, "ClassPlans");
        entries(doc, "TimeLayouts").forEach(([id, layout]) => slots(layout).forEach((point, i) => {
            const start = timeSeconds(pointTime(point, true));
            const end = timeSeconds(pointTime(point, false));
            if (!Number.isFinite(start) || !Number.isFinite(end) || start > end) errors.push(`时间表“${read(layout, "Name", id)}”第 ${i + 1} 个时间点的起止时间无效。`);
            const subjectId = refOf(read(point, "DefaultClassId", ""));
            if (subjectId && !lookup(subjects, subjectId)) errors.push(`时间表“${read(layout, "Name", id)}”引用了不存在的默认科目。`);
        }));
        entries(doc, "ClassPlans").forEach(([id, plan]) => {
            const name = read(plan, "Name", id);
            const layout = lookup(layouts, read(plan, "TimeLayoutId", ""));
            if (!layout) errors.push(`课表“${name}”引用的时间表不存在。`);
            else if (courses(plan).length !== slots(layout).filter(isLesson).length) errors.push(`课表“${name}”的课程数与时间表上课时段数不一致。`);
            courses(plan).forEach((course, i) => {
                const subjectId = refOf(read(course, "SubjectId", ""));
                if (subjectId && !lookup(subjects, subjectId)) errors.push(`课表“${name}”第 ${i + 1} 节引用的科目不存在。`);
            });
            const rule = read(plan, "TimeRule", {});
            const day = Number(read(rule, "WeekDay", 0));
            const week = Number(read(rule, "WeekCountDiv", 0));
            const total = Number(read(rule, "WeekCountDivTotal", 2));
            if (!Number.isInteger(day) || day < 0 || day > 6 || !Number.isInteger(total) || total < 1 || !Number.isInteger(week) || week < 0 || week > total) errors.push(`课表“${name}”的星期或轮换周设置无效。`);
        });
        const seen = new Set();
        Object.entries(read(doc, "OrderedSchedules", {}) || {}).forEach(([key, value]) => {
            const date = dateOfKey(key);
            if (seen.has(date)) errors.push(`${date} 安排了多个临时层或预定课表。`);
            seen.add(date);
            if (!lookup(plans, read(value, "ClassPlanId", ""))) errors.push(`${date} 的临时层或预定课表引用了不存在的课表。`);
        });
        return errors;
    };
    globalThis.RemoteCIProfileTransforms = { clone, read, write, blankProfile, pointTimeField, pointTime, timeText, timeSeconds, editedTime, insertSlot, removeSlot, changeSlotType, moveSlot, affectedCourses, validateProfile,
        isOverlay, regularEntries, tempLayers, todayText, createTempLayer, separateTempTime, moveTempLayer, deleteTempLayer, cleanExpiredTempLayers, deletePlan, markChanged };
    if (typeof document === "undefined") return;
    const app = document.querySelector("[data-profile-app]");
    if (!app) return;
    const $ = selector => app.querySelector(selector);
    const $$ = selector => [...app.querySelectorAll(selector)];
    const initial = JSON.parse($("[data-profile-initial]").textContent);
    const state = { ...initial, mode: initial.isAdmin && location.pathname !== "/ClassProfiles" ? "library" : "classes", active: null, section: "TimeLayouts", objectId: null, drafts: new Map(), selected: new Set(), filter: "", applyRecords: [], lastApply: null, preview: null, busy: false };
    const escape = value => String(value ?? "").replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]);
    const feedback = (message, error = false) => {
        const node = $("[data-profile-feedback]");
        node.textContent = message;
        node.classList.toggle("error", error);
        node.hidden = !message;
    };
    const dialogError = (name, message) => { const node = $(`[data-profile-${name}-error]`); node.textContent = message; node.hidden = !message; };
    const request = async (handler, payload, get = false) => {
        const response = await fetch(`${location.pathname}?handler=${handler}`, get ? { credentials: "same-origin" } : { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": $("input[name=__RequestVerificationToken]").value }, body: JSON.stringify(payload) });
        let data;
        try { data = await response.json(); } catch { throw new Error(response.status === 403 ? "没有操作此档案的权限。" : "服务器未返回有效结果，请检查连接后重试。"); }
        // 下发可能全部失败，但仍有逐设备结果；这种情况必须展示结果并允许手动重试。
        if (!response.ok || data.success === false && !Array.isArray(data.results)) throw new Error(data.message || "操作失败，请重试。");
        return data;
    };
    const run = async action => {
        if (state.busy) return;
        state.busy = true;
        app.setAttribute("aria-busy", "true");
        const returnFocus = document.activeElement;
        const controls = $$('input,select,textarea').filter(node => !node.closest('[data-profile-question]')).map(node => ({ node, disabled: node.disabled }));
        controls.forEach(({ node }) => { node.disabled = true; });
        try { await action(); } catch (error) { feedback(error.message || "操作失败。", true); }
        finally { controls.forEach(({ node, disabled }) => { if (node.isConnected) node.disabled = disabled; }); state.busy = false; app.removeAttribute("aria-busy"); if (document.activeElement === document.body && returnFocus?.isConnected) returnFocus.focus(); }
    };
    const recordKey = record => record.classId ? `class:${record.classId}` : `template:${record.id}`;
    const activeDraft = () => state.drafts.get(state.active);
    const draftFrom = record => ({ ...record, doc: JSON.parse(record.profileJson), dirty: false });
    const findClassRecord = classId => state.profiles.find(record => record.classId === classId);
    const classDraft = classId => {
        const key = `class:${classId}`;
        if (!state.drafts.has(key)) {
            const existing = findClassRecord(classId);
            const name = `${state.classes.find(item => item.id === classId)?.name || "班级"}档案`;
            state.drafts.set(key, existing ? draftFrom(existing) : { id: null, classId, sourceTemplateId: null, revision: 0, name, doc: blankProfile(name), dirty: false });
        }
        return state.drafts.get(key);
    };
    const markDirty = () => { const draft = activeDraft(); if (draft) draft.dirty = true; renderStatus(); renderList(); };
    const exportItem = draft => ({ id: draft.id || null, classId: draft.classId || null, sourceTemplateId: draft.sourceTemplateId || null, revision: draft.revision || 0, name: draft.name.trim(), profileJson: JSON.stringify(draft.doc) });
    const options = (list, reference, blank = "", selected = refOf(reference)) => `${blank ? `<option value="">${escape(blank)}</option>` : ""}${list.map(([id, item]) => `<option value="${escape(id)}"${sameId(selected, id) ? " selected" : ""}>${escape(read(item, "Name", id))}</option>`).join("")}${selected && !list.some(([id]) => sameId(id, selected)) ? `<option value="${escape(selected)}" selected>缺失引用：${escape(selected)}</option>` : ""}`;
    const selectProfile = key => {
        state.active = key;
        state.objectId = null;
        if (key?.startsWith("class:")) classDraft(key.slice(6));
        else if (key && !state.drafts.has(key)) {
            const record = state.profiles.find(item => recordKey(item) === key);
            if (record) state.drafts.set(key, draftFrom(record));
        }
        render();
    };
    const renderList = () => {
        const container = $("[data-profile-records]");
        const focusedClass = document.activeElement?.dataset?.profileClassSelect;
        const focusedRecord = document.activeElement?.dataset?.profileOpen;
        const visible = state.mode === "library" ? [...state.drafts.entries()].filter(([key]) => key.startsWith("new:")).map(([key, draft]) => ({ key, name: draft.name, draft })).concat(state.profiles.filter(item => !item.classId).map(item => ({ key: recordKey(item), name: item.name, draft: state.drafts.get(recordKey(item)) }))) : state.classes.map(item => ({ key: `class:${item.id}`, name: item.name, classId: item.id, draft: state.drafts.get(`class:${item.id}`) }));
        const filtered = visible.filter(item => item.name.toLowerCase().includes(state.filter.toLowerCase()));
        container.innerHTML = filtered.length ? filtered.map(item => `<div class="profile-record${item.key === state.active ? " active" : ""}">${state.mode === "classes" && state.isAdmin && location.pathname !== "/ClassProfiles" ? `<label class="profile-record-select"><input type="checkbox" data-profile-class-select="${escape(item.classId)}" aria-label="选择${escape(item.name)}"${state.selected.has(item.classId) ? " checked" : ""} /></label>` : ""}<button type="button" class="ghost" data-profile-open="${escape(item.key)}" aria-pressed="${item.key === state.active}"><strong>${escape(state.mode === "library" ? item.draft?.name || item.name : item.name)}</strong><small>${item.draft?.dirty ? '<span class="profile-dirty">未保存</span>' : item.classId && !findClassRecord(item.classId) ? "尚未建立档案" : "已保存"}</small></button></div>`).join("") : '<p class="muted profile-list-empty">没有匹配的档案或班级。</p>';
        $("[data-profile-selected-count]").textContent = `已选择 ${state.selected.size} 个班级`;
        $("[data-profile-select-all]").checked = state.classes.length > 0 && state.classes.every(item => state.selected.has(item.id));
        if (focusedClass) $$('[data-profile-class-select]').find(node => node.dataset.profileClassSelect === focusedClass)?.focus();
        if (focusedRecord) $$('[data-profile-open]').find(node => node.dataset.profileOpen === focusedRecord)?.focus();
    };
    const renderStatus = () => {
        const draft = activeDraft();
        if (!draft) return;
        const errors = validateProfile(draft.doc);
        if (!draft.name.trim()) errors.unshift("请填写档案名称。");
        $("[data-profile-draft-status]").textContent = draft.dirty ? "有未保存的修改" : draft.id ? "已保存，设备端变更不会自动回写" : "新档案尚未保存";
        $("[data-profile-draft-status]").classList.toggle("profile-dirty", draft.dirty);
        const node = $("[data-profile-validation]");
        node.hidden = !errors.length;
        node.innerHTML = errors.length ? `<strong>保存前需要修正 ${errors.length} 项问题</strong><ul>${errors.map(item => `<li>${escape(item)}</li>`).join("")}</ul>` : "";
        $("[data-profile-revision]").textContent = draft.id ? `修订 ${draft.revision}${draft.sourceTemplateId ? " · 模板的独立副本" : ""}` : "尚未保存";
    };
    const render = () => {
        $("[data-profile-modes]").hidden = !state.isAdmin || location.pathname === "/ClassProfiles";
        $$('[data-profile-mode]').forEach(node => node.setAttribute("aria-pressed", node.dataset.profileMode === state.mode));
        $("[data-profile-list-title]").textContent = state.mode === "library" ? "全局模板" : state.isAdmin ? "班级" : "当前班级";
        $("[data-profile-context]").textContent = state.mode === "library" ? "全局档案作为模板。分配到班级后成为独立副本，后续修改互不影响。" : location.pathname === "/ClassProfiles" ? "仅管理当前班级。离线也可保存服务端档案，保存与设备下发分开进行。" : "切换班级会保留草稿。离线班级也可保存服务端档案，保存与设备下发分开进行。";
        $("[data-profile-select-all-label]").hidden = state.mode !== "classes" || !state.isAdmin || location.pathname === "/ClassProfiles";
        $("[data-profile-batchbar]").hidden = state.mode !== "classes" || !state.isAdmin || location.pathname === "/ClassProfiles";
        const draft = activeDraft();
        $("[data-profile-content]").hidden = !draft;
        $("[data-profile-empty]").hidden = !!draft;
        $("[data-profile-editor-title]").textContent = draft ? state.mode === "classes" ? state.classes.find(item => item.id === draft.classId)?.name || "班级档案" : draft.name : "选择一个档案";
        $$('[data-profile-action="copy"], [data-profile-action="assign"]').forEach(node => node.hidden = state.mode !== "library" || !draft);
        $$('[data-profile-action="export"], [data-profile-action="delete"]').forEach(node => node.disabled = !draft?.id);
        if (!draft) { renderList(); return; }
        $("[data-profile-name]").value = draft.name;
        $$('[data-profile-section]').forEach(node => node.setAttribute("aria-pressed", node.dataset.profileSection === state.section));
        $$('[data-profile-count]').forEach(node => node.textContent = node.dataset.profileCount === "TempLayers" ? tempLayers(draft.doc).length : regularEntries(draft.doc, node.dataset.profileCount).length);
        renderSection();
        renderStatus();
        renderList();
    };
    const renderSection = () => {
        const draft = activeDraft();
        if (!draft) return;
        const doc = draft.doc;
        const container = $("[data-profile-section-content]");
        if (state.section === "Subjects") {
            const list = entries(doc, "Subjects");
            container.innerHTML = `<div class="profile-section-heading"><h3>科目</h3><button type="button" class="ghost" data-profile-add-object>添加科目</button></div>${list.length ? `<div class="profile-table-scroll"><table class="profile-table"><thead><tr><th>名称</th><th>简称</th><th>教师</th><th>操作</th></tr></thead><tbody>${list.map(([id, subject]) => `<tr data-profile-subject="${escape(id)}"><td><input data-profile-subject-field="Name" aria-label="科目名称" value="${escape(read(subject, "Name", ""))}" /></td><td><input data-profile-subject-field="Initial" aria-label="科目简称" value="${escape(read(subject, "Initial", ""))}" /></td><td><input data-profile-subject-field="TeacherName" aria-label="任课教师" value="${escape(read(subject, "TeacherName", ""))}" /></td><td><button type="button" class="ghost danger" data-profile-delete-subject="${escape(id)}" aria-label="删除${escape(read(subject, "Name", "科目"))}">删除</button></td></tr>`).join("")}</tbody></table></div>` : '<p class="muted profile-section-empty">尚无科目。添加后可在时间表和课表中选用。</p>'}`;
            return;
        }
        if (state.section === "TempLayers") { renderTempLayers(doc, container); return; }
        const list = regularEntries(doc, state.section);
        if (!list.some(([id]) => id === state.objectId)) state.objectId = list[0]?.[0] || null;
        const isTime = state.section === "TimeLayouts";
        container.innerHTML = `<div class="profile-section-heading"><label>${isTime ? "时间表" : "课表"}<select data-profile-object-select>${options(list, state.objectId, list.length ? "" : "尚无内容")}</select></label><div class="profile-toolbar-actions"><button type="button" class="ghost" data-profile-add-object>添加${isTime ? "时间表" : "课表"}</button>${state.objectId ? '<button type="button" class="ghost danger" data-profile-delete-object>删除</button>' : ""}</div></div><div data-profile-object-content></div>`;
        if (!state.objectId) { $("[data-profile-object-content]").innerHTML = `<p class="muted profile-section-empty">尚无${isTime ? "时间表" : "课表"}。</p>`; return; }
        const object = dictionary(doc, state.section)[state.objectId];
        const body = $("[data-profile-object-content]");
        const name = `<label class="profile-object-name">${isTime ? "时间表" : "课表"}名称<input data-profile-object-field="Name" value="${escape(read(object, "Name", ""))}" /></label>`;
        if (isTime) { body.innerHTML = `${name}${timeTableHtml(doc, object)}`; return; }
        const rule = read(object, "TimeRule", {});
        const layoutId = read(object, "TimeLayoutId", "");
        const layout = lookup(dictionary(doc, "TimeLayouts"), layoutId);
        body.innerHTML = `${name}<div class="profile-plan-settings"><label>关联时间表<select data-profile-object-field="TimeLayoutId">${options(regularEntries(doc, "TimeLayouts"), layoutId, "选择时间表")}</select></label><label>星期<select data-profile-rule-field="WeekDay">${["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"].map((label, day) => `<option value="${day}"${Number(read(rule, "WeekDay", 0)) === day ? " selected" : ""}>${label}</option>`).join("")}</select></label><label>轮换总周数<input type="number" min="1" max="99" data-profile-rule-field="WeekCountDivTotal" value="${escape(read(rule, "WeekCountDivTotal", 2))}" /></label><label>轮换第几周（0 为每周）<input type="number" min="0" max="99" data-profile-rule-field="WeekCountDiv" value="${escape(read(rule, "WeekCountDiv", 0))}" /></label><label class="check"><input type="checkbox" data-profile-object-field="IsEnabled"${read(object, "IsEnabled", true) ? " checked" : ""} /> 启用此课表</label></div>${lessonsHtml(doc, object, layout)}`;
    };
    const timeTableHtml = (doc, layout) => `<div class="profile-table-scroll"><table class="profile-table profile-time-table"><thead><tr><th>开始</th><th>结束</th><th>类型</th><th>默认科目</th><th>名称（课间/行动）</th><th>显示</th><th>操作</th></tr></thead><tbody>${slots(layout).map((point, i) => `<tr data-profile-slot="${i}"><td><input type="time" step="1" data-profile-slot-field="${pointTimeField(point, true)}" value="${escape(timeText(pointTime(point, true)))}" aria-label="第${i + 1}个时段开始时间" /></td><td><input type="time" step="1" data-profile-slot-field="${pointTimeField(point, false)}" value="${escape(timeText(pointTime(point, false)))}" aria-label="第${i + 1}个时段结束时间" /></td><td><select data-profile-slot-field="TimeType" aria-label="第${i + 1}个时段类型">${["上课", "课间", "分割线", "行动"].map((label, type) => `<option value="${type}"${Number(read(point, "TimeType", 0)) === type ? " selected" : ""}>${label}</option>`).join("")}</select></td><td><select data-profile-slot-field="DefaultClassId" aria-label="第${i + 1}个时段默认科目">${options(entries(doc, "Subjects"), read(point, "DefaultClassId", ""), "无默认科目")}</select></td><td><input data-profile-slot-field="BreakName" value="${escape(read(point, "BreakName", ""))}" aria-label="第${i + 1}个时段显示名称" /></td><td><label class="check"><input type="checkbox" data-profile-slot-field="IsHideDefault"${read(point, "IsHideDefault", false) ? " checked" : ""} /> 默认隐藏</label></td><td><div class="profile-row-actions"><button type="button" class="ghost" data-profile-slot-action="insert" data-index="${i}" aria-label="在第${i + 1}个时段前插入">＋</button><button type="button" class="ghost" data-profile-slot-action="up" data-index="${i}" aria-label="上移第${i + 1}个时段"${i === 0 ? " disabled" : ""}>↑</button><button type="button" class="ghost" data-profile-slot-action="down" data-index="${i}" aria-label="下移第${i + 1}个时段"${i === slots(layout).length - 1 ? " disabled" : ""}>↓</button><button type="button" class="ghost danger" data-profile-slot-action="delete" data-index="${i}" aria-label="删除第${i + 1}个时段">删除</button></div></td></tr>`).join("")}</tbody></table></div><button type="button" class="ghost" data-profile-slot-action="insert" data-index="${slots(layout).length}">添加时间点</button><p class="muted profile-help">上课时段增删及移动会同步调整关联课表；新时段的课程留空。未编辑字段和附加配置会完整保留。</p>`;
    const lessonsHtml = (doc, plan, layout, overlay = false) => layout ? `<div class="profile-table-scroll"><table class="profile-table"><thead><tr><th>节次</th><th>时间</th><th>科目</th></tr></thead><tbody>${slots(layout).filter(isLesson).map((point, i) => `<tr><th scope="row">第 ${i + 1} 节</th><td>${escape(timeText(pointTime(point, true)))} – ${escape(timeText(pointTime(point, false)))}</td><td><select data-profile-course="${i}" aria-label="第${i + 1}节科目">${options(entries(doc, "Subjects"), read(courses(plan)[i], "SubjectId", ""), "留空")}</select>${overlay && read(courses(plan)[i], "IsChangedClass", false) ? '<small class="profile-changed">已换课</small>' : ""}</td></tr>`).join("")}</tbody></table></div>` : '<p class="muted profile-section-empty">请选择有效的时间表后安排课程。</p>';
    const tempStatus = date => !date ? "未安排日期" : date < todayText() ? "已过期" : date === todayText() ? "今天生效" : "待生效";
    const renderTempLayers = (doc, container) => {
        const list = tempLayers(doc);
        if (!list.some(item => item.id === state.objectId)) state.objectId = (list.find(item => item.date >= todayText()) || list[0])?.id || null;
        const expired = list.filter(item => !item.date || item.date < todayText()).length;
        container.innerHTML = `<div class="profile-section-heading"><label>临时层<select data-profile-object-select>${list.length ? list.map(item => `<option value="${escape(item.id)}"${item.id === state.objectId ? " selected" : ""}>${escape(item.date || "未安排日期")} · ${escape(read(item.plan, "Name", "临时层"))}（${tempStatus(item.date)}）</option>`).join("") : '<option value="">尚无临时层</option>'}</select></label><div class="profile-toolbar-actions"><button type="button" class="ghost" data-profile-action="new-temp-layer">新建临时层</button>${expired ? `<button type="button" class="ghost" data-profile-action="clean-temp-layers">清理已过期（${expired}）</button>` : ""}${state.objectId ? '<button type="button" class="ghost danger" data-profile-delete-object>删除</button>' : ""}</div></div><div data-profile-object-content></div>`;
        const body = $("[data-profile-object-content]");
        if (!state.objectId) { body.innerHTML = '<p class="muted profile-section-empty">尚无临时层。临时层只在指定日期替换当天的课表，可从常规课表新建，也可从设备收集后在此编辑。</p>'; return; }
        const item = list.find(entry => entry.id === state.objectId);
        const layouts = dictionary(doc, "TimeLayouts");
        const layoutId = read(item.plan, "TimeLayoutId", "");
        const layout = lookup(layouts, layoutId);
        const ownLayout = !!layout && isOverlay(layout);
        const choices = regularEntries(doc, "TimeLayouts").concat(ownLayout ? [[Object.keys(layouts).find(id => sameId(id, layoutId)), layout]] : []);
        body.innerHTML = `<div class="profile-plan-settings"><label>临时层名称<input data-profile-object-field="Name" value="${escape(read(item.plan, "Name", ""))}" /></label><label>生效日期<input type="date" data-profile-temp-date value="${escape(item.date)}" required /></label><label>当天时间表<select data-profile-object-field="TimeLayoutId">${options(choices, layoutId, "选择时间表")}</select></label><p class="muted profile-temp-meta">${escape(tempStatus(item.date))} · 来源课表：${escape(item.source ? read(item.source, "Name", "未命名课表") : "无")}</p></div>${layout && !ownLayout ? '<p class="muted profile-help">当天作息与所选常规时间表相同。<button type="button" class="ghost profile-inline-button" data-profile-action="separate-temp-time">单独调整当天时间</button>会复制一份仅此临时层使用的时间表，不影响常规时间表。</p>' : ""}${ownLayout ? `<h4 class="profile-subheading">当天时间表（仅此临时层使用）</h4>${timeTableHtml(doc, layout)}` : ""}<h4 class="profile-subheading">当天课程</h4>${lessonsHtml(doc, item.plan, layout, true)}<p class="muted profile-help">临时层只在生效日期替换当天课表，标注“已换课”的节次与来源课表不同。下发到设备时请选择“作为临时层下发”。</p>`;
    };
    // 临时层标签编辑的是 ClassPlans 中的临时层课表；时段表格编辑的是它当前使用的时间表。
    const sectionKey = () => state.section === "TempLayers" ? "ClassPlans" : state.section;
    const slotLayoutId = () => {
        if (state.section !== "TempLayers") return state.objectId;
        const doc = activeDraft().doc;
        const layoutId = read(dictionary(doc, "ClassPlans")[state.objectId], "TimeLayoutId", "");
        return Object.keys(dictionary(doc, "TimeLayouts")).find(id => sameId(id, layoutId));
    };
    const collectClasses = async (classIds, asTemplate = false) => {
        if (!asTemplate) {
            const dirty = classIds.map(id => state.drafts.get(`class:${id}`)).filter(draft => draft?.dirty);
            if (dirty.length && !await ask(`收集结果会替换 ${dirty.length} 个班级未保存的草稿。继续？`, { confirmText: "收集并替换草稿" })) return;
        }
        const result = await request("Collect", { classIds });
        renderResults(result, "collect");
        let last = null;
        for (const item of result.results.filter(entry => entry.success)) {
            const doc = JSON.parse(item.profileJson);
            if (asTemplate) {
                const key = `new:${uid()}`;
                const name = `${item.className}档案（收集）`;
                write(doc, "Name", name);
                state.drafts.set(key, { id: null, classId: null, name, revision: 0, doc, dirty: true });
                last = key;
            } else {
                // 保留服务端档案 ID 与修订号：保存时照常校验修订号，收集结果不会悄悄覆盖他人的新版本。
                const draft = classDraft(item.classId);
                write(doc, "Name", draft.name);
                draft.doc = doc;
                draft.dirty = true;
                last = `class:${item.classId}`;
            }
        }
        if (last && (asTemplate || classIds.length === 1 || !activeDraft())) { state.active = last; state.objectId = null; }
        feedback(result.message || "收集已完成。", !result.success);
        render();
    };
    const saveDrafts = async drafts => {
        if (!drafts.length) throw new Error("请先选择档案或班级。");
        const errors = drafts.flatMap(draft => [...(!draft.name.trim() ? ["请填写档案名称。"] : []), ...validateProfile(draft.doc)].map(error => `${draft.name}：${error}`));
        if (errors.length) throw new Error(errors[0]);
        const result = await request("Save", { items: drafts.map(exportItem) });
        for (const record of result.profiles) {
            const draft = drafts.find(item => item.classId ? item.classId === record.classId : item.id === record.id || !item.id && item.name.trim() === record.name);
            const oldKey = draft && [...state.drafts].find(([, value]) => value === draft)?.[0];
            state.profiles = state.profiles.filter(item => item.id !== record.id && !(record.classId && item.classId === record.classId));
            state.profiles.push(record);
            const nextKey = recordKey(record);
            if (oldKey) state.drafts.delete(oldKey);
            state.drafts.set(nextKey, draftFrom(record));
            if (state.active === oldKey) state.active = nextKey;
        }
        feedback(result.message || `已保存 ${result.profiles.length} 份档案。`);
        render();
    };
    const selectedDrafts = () => [...state.selected].map(classDraft);
    const requireSaved = drafts => {
        if (!drafts.length) throw new Error("请先选择班级或档案。");
        if (drafts.some(draft => !draft.id || draft.dirty)) throw new Error("请先保存所有要下发的档案。下发只使用已保存的确定版本。");
        return drafts.map(draft => state.profiles.find(record => record.id === draft.id));
    };
    const openDialog = node => { node.profileReturnFocus = document.activeElement; node.showModal(); node.querySelector("input:not([type=hidden]):not(:disabled),textarea:not(:disabled),button:not(:disabled)")?.focus(); };
    const closeDialog = node => node?.close();
    const ask = (message, { title = "确认操作", value, confirmText = "确认" } = {}) => new Promise(resolve => {
        const dialog = $("[data-profile-question]");
        const input = $("[data-profile-question-input]");
        const inputMode = typeof value === "string";
        $("[data-profile-question-title]").textContent = title;
        $("[data-profile-question-message]").textContent = message;
        $("[data-profile-question-label]").hidden = !inputMode;
        $("[data-profile-question-input-label]").textContent = title;
        $("[data-profile-question-accept]").textContent = confirmText;
        input.disabled = !inputMode;
        input.required = inputMode;
        input.value = value || "";
        input.setCustomValidity("");
        dialog.returnValue = "";
        dialog.addEventListener("close", () => resolve(dialog.returnValue === "accept" ? inputMode ? input.value.trim() : true : inputMode ? null : false), { once: true });
        openDialog(dialog);
        if (inputMode) { input.focus(); input.select(); }
        else $("[data-profile-question-accept]").focus();
    });
    const renderTargets = () => {
        const fixed = !state.isAdmin || location.pathname === "/ClassProfiles";
        const profileClasses = state.applyRecords.filter(record => record.classId).map(record => record.classId);
        const availableClasses = profileClasses.length ? state.classes.filter(item => profileClasses.includes(item.id)) : state.classes;
        const availableDevices = state.devices.filter(item => availableClasses.some(classroom => classroom.id === item.classId));
        const target = $("[data-profile-targets]");
        if (fixed) { target.innerHTML = '<p class="notice">目标固定为当前班级，使用最早接入的在线设备。</p>'; return; }
        target.innerHTML = `<fieldset><legend>目标班级（每班最早接入的在线设备）</legend><div class="broadcast-targets">${availableClasses.map(item => `<label class="check"><input type="checkbox" data-profile-target-class value="${escape(item.id)}"${profileClasses.includes(item.id) ? " checked" : ""} /> ${escape(item.name)}</label>`).join("")}</div></fieldset>${state.groups.length ? `<fieldset><legend>目标分组</legend><div class="broadcast-targets">${state.groups.map(item => `<label class="check"><input type="checkbox" data-profile-target-group value="${escape(item.id)}" /> ${escape(item.name)}</label>`).join("")}</div></fieldset>` : ""}<details><summary>选择具体设备（可与班级目标同时选择）</summary><div class="profile-device-targets">${availableDevices.length ? availableDevices.map(item => `<label class="check"><input type="checkbox" data-profile-target-device value="${escape(item.connectionId)}"${item.connectionId ? "" : " disabled"} /> <span>${escape(state.classes.find(classroom => classroom.id === item.classId)?.name)} / ${escape(item.deviceName)} <small class="muted">${item.online ? "在线" : "离线，请选择班级目标"}${item.capabilities?.includes("profile.apply") ? item.capabilities.includes("profile.temp-layer") ? "" : " · 下发临时层需升级插件" : " · 需升级插件"}</small></span></label>`).join("") : '<p class="muted">尚无设备。离线班级可保存档案，下发会显示独立失败结果。</p>'}</div></details>`;
    };
    const openApply = records => {
        state.applyRecords = records;
        $("[data-profile-apply-form]").reset();
        $("[data-profile-import-name-label]").hidden = true;
        $("[data-profile-replace-warning]").hidden = true;
        dialogError("apply", "");
        $("[data-profile-apply-summary]").textContent = records.map(record => `${record.name}（修订 ${record.revision}）`).join("、");
        const multiple = records.length > 1;
        state.applyMultiple = multiple;
        $("[data-profile-object-picker]").hidden = multiple;
        $("[data-profile-apply-objects]").innerHTML = multiple ? "" : ["TimeLayouts", "ClassPlans", "Subjects"].map((section, index) => {
            const doc = JSON.parse(records[0].profileJson);
            return `<fieldset data-profile-object-section="${1 << index}"><legend>${["时间表", "课表", "科目"][index]}</legend><div class="broadcast-targets">${regularEntries(doc, section).map(([id, item]) => `<label class="check"><input type="checkbox" data-profile-apply-object="${section}" value="${escape(id)}" checked /> ${escape(read(item, "Name", id))}</label>`).join("")}</div></fieldset>`;
        }).join("");
        const upcoming = multiple ? [] : tempLayers(JSON.parse(records[0].profileJson)).filter(item => item.date && item.date >= todayText());
        $("[data-profile-temp-options]").innerHTML = multiple ? '<p class="muted">将下发各班档案中全部未过期的临时层；没有临时层的班级会单独返回失败。</p>' : upcoming.length ? upcoming.map(item => `<label class="check"><input type="checkbox" data-profile-apply-temp value="${escape(item.id)}" checked /> ${escape(item.date)} · ${escape(read(item.plan, "Name", "临时层"))}</label>`).join("") : '<p class="muted">此档案没有今天及以后的临时层。可在“临时层”标签中新建。</p>';
        renderTargets();
        updateApplyMode();
        openDialog($("[data-profile-apply]"));
    };
    const applySections = () => $$('[data-profile-apply-section]:checked').reduce((bits, node) => bits | Number(node.value), 0);
    const updateApplyMode = () => {
        const mode = Number($("input[name=applyMode]:checked")?.value || 0);
        $("[data-profile-import-name-label]").hidden = mode !== 3;
        $("[data-profile-import-name]").required = mode === 3;
        $("[data-profile-replace-warning]").hidden = mode !== 2;
        $("[data-profile-apply-sections-field]").hidden = mode === 4;
        $("[data-profile-object-picker]").hidden = mode === 4 || state.applyMultiple;
        $("[data-profile-temp-picker]").hidden = mode !== 4;
        const bits = applySections();
        $("[data-profile-replace-summary]").textContent = `将清空并替换设备当前档案中的：${["时间表", "课表", "科目"].filter((_, i) => bits & 1 << i).join("、") || "尚未选择类别"}。`;
        $$('[data-profile-object-section]').forEach(node => node.hidden = !(bits & Number(node.dataset.profileObjectSection)));
    };
    const renderResults = (result, kind = "apply") => {
        $("[data-profile-results]").hidden = false;
        $("[data-profile-results-title]").textContent = kind === "collect" ? "收集结果" : "下发结果";
        $("[data-profile-result-list]").innerHTML = result.results.map(item => `<li><i class="bi ${item.success ? "bi-check-circle ok" : "bi-x-circle bad"}" aria-hidden="true"></i><span><strong>${escape(item.targetName || `${item.className}${item.deviceName ? ` / ${item.deviceName}` : ""}`)}</strong>：${escape(item.message)}</span></li>`).join("");
        $("[data-profile-action=retry]").hidden = kind === "collect";
        if (kind === "collect") return;
        $("[data-profile-action=retry]").disabled = !result.results.some(item => !item.success);
        state.lastResults = result.results;
    };
    const applyPayload = () => {
        const mode = Number($("input[name=applyMode]:checked")?.value || 0);
        const sections = mode === 4 ? 0 : applySections();
        if (!mode) throw new Error("请选择一种应用方式。");
        if (mode !== 4 && !sections) throw new Error("请选择至少一种下发类别。");
        if (mode === 2 && !$("[data-profile-confirm-replace]").checked) throw new Error("请确认清空并替换所选类别。");
        if (mode === 3 && !$("[data-profile-import-name]").value.trim()) throw new Error("请填写设备档案名称。");
        const fixed = !state.isAdmin || location.pathname === "/ClassProfiles";
        const values = selector => $$(selector).filter(node => node.checked).map(node => node.value);
        const payload = { items: state.applyRecords.map(record => ({ id: record.id, revision: record.revision })), mode, sections, importProfileName: $("[data-profile-import-name]").value.trim() || null, restartAfter: $("[data-profile-restart]").checked, confirmReplace: $("[data-profile-confirm-replace]").checked, classIds: fixed ? [state.currentClassId] : values("[data-profile-target-class]"), groupIds: fixed ? [] : values("[data-profile-target-group]"), connectionIds: fixed ? [] : values("[data-profile-target-device]") };
        if (!payload.classIds.length && !payload.groupIds.length && !payload.connectionIds.length) throw new Error("请选择目标班级、分组或设备。");
        if (mode === 4) {
            payload.replaceExistingTempLayers = $("[data-profile-replace-temp]").checked;
            if (state.applyRecords.length === 1) {
                payload.tempLayerIds = values("[data-profile-apply-temp]");
                if (!payload.tempLayerIds.length) throw new Error("请至少选择一个今天及以后的临时层。");
            }
            return payload;
        }
        if (state.applyRecords.length === 1) ["TimeLayouts", "ClassPlans", "Subjects"].forEach((section, i) => {
            if (sections & 1 << i) {
                const ids = values(`[data-profile-apply-object="${section}"]`);
                const count = regularEntries(JSON.parse(state.applyRecords[0].profileJson), section).length;
                if (count && !ids.length) throw new Error(`请为${["时间表", "课表", "科目"][i]}选择至少一个对象，或取消此类别。`);
                payload[["timeLayoutIds", "classPlanIds", "subjectIds"][i]] = ids;
            }
        });
        return payload;
    };
    const deleteObject = async (section, id) => {
        const draft = activeDraft();
        if (section === "TempLayers") {
            const layer = tempLayers(draft.doc).find(item => item.id === id);
            if (!await ask(`删除${layer?.date ? ` ${layer.date} 的` : ""}临时层“${read(layer?.plan, "Name", "临时层")}”？保存后生效，已下发到设备的临时层不受影响。`, { confirmText: "删除" })) return;
            deleteTempLayer(draft.doc, id);
            state.objectId = null;
            markDirty();
            render();
            return;
        }
        if (section === "TimeLayouts" && relatedPlans(draft.doc, id).length) throw new Error("这张时间表仍被课表或临时层引用，请先修改或删除关联课表。");
        if (section === "Subjects") {
            const referenced = entries(draft.doc, "TimeLayouts").some(([, layout]) => slots(layout).some(point => sameId(read(point, "DefaultClassId", ""), id))) || entries(draft.doc, "ClassPlans").some(([, plan]) => courses(plan).some(course => sameId(read(course, "SubjectId", ""), id)));
            if (referenced) throw new Error("此科目仍被时间表或课表引用，请先移除这些引用。");
        }
        const scheduled = section === "ClassPlans" && Object.values(read(draft.doc, "OrderedSchedules", {}) || {}).some(value => sameId(read(value, "ClassPlanId", ""), id));
        const sourced = section === "ClassPlans" && tempLayers(draft.doc).some(item => sameId(item.sourceId, id));
        if (!await ask(`删除“${read(dictionary(draft.doc, section)[id], "Name", "此对象")}”？${scheduled ? "指向它的预定课表会一并删除。" : ""}${sourced ? "以它为来源的临时层会保留，但不再标注来源。" : ""}保存后生效。`, { confirmText: "删除" })) return;
        if (section === "ClassPlans") deletePlan(draft.doc, id); else delete dictionary(draft.doc, section)[id];
        state.objectId = null;
        markDirty();
        render();
    };
    const actions = {
        collect: async () => {
            if (state.mode === "library") {
                $("[data-profile-collect-classes]").innerHTML = state.classes.length ? state.classes.map((item, index) => `<label class="check"><input type="radio" name="collectClass" data-profile-collect-class value="${escape(item.id)}"${index === 0 ? " checked" : ""} /> ${escape(item.name)} <small class="muted">${state.devices.some(device => device.classId === item.id && device.online) ? "在线" : "离线"}</small></label>`).join("") : '<p class="muted">尚无班级。</p>';
                dialogError("collect", "");
                openDialog($("[data-profile-collect]"));
                return;
            }
            const draft = activeDraft();
            if (!draft?.classId) throw new Error("请先选择班级。");
            await collectClasses([draft.classId]);
        },
        "collect-selected": () => {
            if (!state.selected.size) throw new Error("请先勾选要收集的班级。");
            return collectClasses([...state.selected]);
        },
        "confirm-collect": async () => {
            const classId = $$("[data-profile-collect-class]").find(node => node.checked)?.value;
            if (!classId) { dialogError("collect", "请选择一个班级。"); return; }
            try { await collectClasses([classId], true); closeDialog($("[data-profile-collect]")); }
            catch (error) { dialogError("collect", error.message); }
        },
        "new-temp-layer": () => {
            const draft = activeDraft();
            const plans = regularEntries(draft.doc, "ClassPlans");
            if (!plans.length) throw new Error("请先添加常规课表，再从课表新建临时层。");
            $("[data-profile-temp-source]").innerHTML = options(plans, plans[0][0]);
            $("[data-profile-temp-new-date]").value = todayText();
            $("[data-profile-temp-separate]").checked = false;
            dialogError("templayer", "");
            openDialog($("[data-profile-templayer]"));
        },
        "confirm-temp-layer": () => {
            const draft = activeDraft();
            try {
                const id = createTempLayer(draft.doc, $("[data-profile-temp-source]").value, $("[data-profile-temp-new-date]").value, $("[data-profile-temp-separate]").checked);
                closeDialog($("[data-profile-templayer]"));
                state.section = "TempLayers";
                state.objectId = id;
                markDirty();
                render();
            } catch (error) { dialogError("templayer", error.message); }
        },
        "clean-temp-layers": async () => {
            if (!await ask("清理早于今天和没有安排日期的临时层？保存后生效，设备上的临时层不受影响。", { confirmText: "清理" })) return;
            const removed = cleanExpiredTempLayers(activeDraft().doc);
            state.objectId = null;
            markDirty();
            render();
            feedback(`已清理 ${removed} 个临时层，保存后生效。`);
        },
        "separate-temp-time": () => { separateTempTime(activeDraft().doc, state.objectId); markDirty(); renderSection(); },
        new: async () => {
            if (state.mode === "classes") {
                const draft = activeDraft() || (state.classes[0] && classDraft(state.classes[0].id));
                if (!draft) throw new Error("尚无可管理的班级。");
                if ((draft.id || draft.dirty) && !await ask("新建空档案会替换当前班级草稿中的所有内容。继续？", { confirmText: "新建空档案" })) return;
                draft.doc = blankProfile(draft.name); draft.dirty = true;
                selectProfile(`class:${draft.classId}`);
            } else {
                const name = await ask("新建一份空白模板，保存后可分配给班级。", { title: "新档案名称", value: "新档案", confirmText: "新建" });
                if (!name?.trim()) return;
                const key = `new:${uid()}`;
                state.drafts.set(key, { id: null, classId: null, name: name.trim(), revision: 0, doc: blankProfile(name.trim()), dirty: true });
                selectProfile(key);
            }
        },
        upload: () => { state.preview = null; $("[data-profile-upload-json]").value = ""; $("[data-profile-file]").value = ""; $("[data-profile-preview]").hidden = true; $("[data-profile-action=confirm-upload]").disabled = true; dialogError("upload", ""); openDialog($("[data-profile-upload]")); },
        preview: async () => {
            dialogError("upload", ""); state.preview = null; $("[data-profile-action=confirm-upload]").disabled = true;
            try {
                const profileJson = $("[data-profile-upload-json]").value;
                if (new TextEncoder().encode(profileJson).length > 5 * 1024 * 1024) throw new Error("每份档案文件最大 5 MB。");
                const result = await request("Preview", { profileJson });
                const preview = result.preview;
                const node = $("[data-profile-preview]");
                node.hidden = false;
                node.innerHTML = `<h3>${escape(preview.name || "未命名档案")}</h3><dl class="profile-preview-counts"><div><dt>时间表</dt><dd>${preview.timeLayoutCount}</dd></div><div><dt>课表</dt><dd>${preview.classPlanCount}</dd></div><div><dt>科目</dt><dd>${preview.subjectCount}</dd></div></dl>${preview.errors.length ? `<div class="profile-validation"><strong>引用或数据错误</strong><ul>${preview.errors.map(error => `<li>${escape(error)}</li>`).join("")}</ul></div>` : '<p class="muted">解析成功。确认后保存到服务端档案库。</p>'}`;
                if (!preview.errors.length) { state.preview = result; $("[data-profile-action=confirm-upload]").disabled = false; }
            } catch (error) { dialogError("upload", error.message); }
        },
        "confirm-upload": async () => {
            if (!state.preview) return;
            let draft;
            if (state.mode === "classes") {
                draft = activeDraft();
                if (!draft) throw new Error("请先选择班级。");
                if ((draft.id || draft.dirty) && !await ask("上传内容将替换当前班级档案。确认保存？", { confirmText: "替换并保存" })) return;
                draft.doc = JSON.parse(state.preview.profileJson);
                draft.name = state.preview.preview.name || draft.name;
                draft.dirty = true;
            } else {
                const name = state.preview.preview.name || "上传档案";
                const key = `new:${uid()}`;
                draft = { id: null, classId: null, name, revision: 0, doc: JSON.parse(state.preview.profileJson), dirty: true };
                state.drafts.set(key, draft); state.active = key;
            }
            try { await saveDrafts([draft]); closeDialog($("[data-profile-upload]")); }
            catch (error) { dialogError("upload", error.message); render(); }
        },
        save: () => saveDrafts(activeDraft() ? [activeDraft()] : []),
        "save-selected": () => saveDrafts(selectedDrafts()),
        export: () => { const draft = activeDraft(); if (draft?.id) location.href = `${location.pathname}?handler=Export&id=${encodeURIComponent(draft.id)}`; },
        copy: async () => {
            const draft = activeDraft();
            if (!draft) return;
            if (draft.dirty || !draft.id) throw new Error("请先保存档案，再复制模板。");
            const name = await ask("复制这份已保存模板，保留其所有内容和对象 ID。", { title: "副本名称", value: `${draft.name} 副本`, confirmText: "复制" });
            if (!name?.trim()) return;
            const result = await request("Copy", { id: draft.id, revision: draft.revision, name: name.trim() });
            const refreshed = await request("Data", null, true);
            state.profiles = refreshed.profiles;
            const copy = state.profiles.find(item => !item.classId && item.name === name.trim() && item.id !== draft.id);
            if (copy) selectProfile(recordKey(copy)); else render();
            feedback(result.message || "已复制模板。");
        },
        delete: async () => {
            const draft = activeDraft();
            if (!draft?.id || !await ask(`删除“${draft.name}”？${draft.classId ? "设备中的档案保留。" : "已分配到班级的副本保留。"}`, { confirmText: "删除" })) return;
            await request("Delete", { id: draft.id, revision: draft.revision });
            state.profiles = state.profiles.filter(record => record.id !== draft.id);
            state.drafts.delete(state.active);
            selectProfile(draft.classId ? `class:${draft.classId}` : state.profiles.find(record => !record.classId) ? recordKey(state.profiles.find(record => !record.classId)) : null);
            feedback("已删除服务端档案。");
        },
        reload: async () => {
            if (activeDraft()?.dirty && !await ask("重新加载会丢弃当前档案的未保存修改。继续？", { confirmText: "重新加载" })) return;
            const result = await request("Data", null, true);
            state.profiles = result.profiles; state.drafts.delete(state.active); selectProfile(state.active);
            feedback("已加载最新的服务端版本。");
        },
        "copy-to-selected": async () => {
            const current = activeDraft();
            const targets = selectedDrafts().filter(item => item !== current);
            if (!current || !targets.length) throw new Error("请选择其他目标班级。");
            if (!await ask(`将当前内容复制到 ${targets.length} 个班级草稿，覆盖其原有内容。继续？`, { confirmText: "复制到所选班" })) return;
            targets.forEach(draft => { draft.doc = clone(current.doc); draft.name = current.name; draft.sourceTemplateId = current.sourceTemplateId || null; draft.dirty = true; });
            render(); feedback(`已复制到 ${targets.length} 个班级草稿，请统一保存。`);
        },
        assign: () => {
            requireSaved([activeDraft()]);
            $("[data-profile-assign-classes]").innerHTML = state.classes.map(item => `<label class="check"><input type="checkbox" data-profile-assign-class value="${escape(item.id)}" /> ${escape(item.name)}${findClassRecord(item.id) ? "（替换已有档案）" : ""}</label>`).join("");
            dialogError("assign", ""); openDialog($("[data-profile-assign]"));
        },
        "confirm-assign": async () => {
            const targets = $$('[data-profile-assign-class]:checked').map(node => classDraft(node.value));
            if (!targets.length) { dialogError("assign", "请选择至少一个班级。"); return; }
            if (targets.some(draft => draft.id || draft.dirty) && !await ask("分配会替换所选班级的档案与当前草稿。继续？", { confirmText: "替换并分配" })) return;
            const source = activeDraft();
            targets.forEach(draft => { draft.doc = clone(source.doc); draft.name = source.name; draft.sourceTemplateId = source.id; draft.dirty = true; });
            try { await saveDrafts(targets); closeDialog($("[data-profile-assign]")); } catch (error) { dialogError("assign", error.message); }
        },
        apply: () => openApply(requireSaved(activeDraft() ? [activeDraft()] : [])),
        "apply-selected": () => openApply(requireSaved(selectedDrafts())),
        retry: async () => {
            if (!state.lastApply) return;
            const failed = state.lastResults.filter(item => !item.success);
            const payload = { ...state.lastApply, classIds: [...new Set(failed.filter(item => !item.connectionId).map(item => item.classId).filter(Boolean))], groupIds: [], connectionIds: [...new Set(failed.map(item => item.connectionId).filter(Boolean))] };
            if (!payload.classIds.length && !payload.connectionIds.length) throw new Error("无法确定失败目标，请重新选择目标下发。");
            const result = await request("Apply", payload); renderResults(result); feedback(result.message || "重试已完成。");
        },
    };
    app.addEventListener("click", event => {
        const button = event.target.closest("button");
        if (!button) return;
        if (button.hasAttribute("data-profile-close")) { closeDialog(button.closest("dialog")); return; }
        if (button.closest("[data-profile-question]")) return;
        if (state.busy) { event.preventDefault(); return; }
        if (button.dataset.profileMode) {
            state.mode = button.dataset.profileMode; state.filter = ""; $("[data-profile-search]").value = "";
            selectProfile(state.mode === "classes" ? state.classes[0] ? `class:${state.classes[0].id}` : null : state.profiles.find(record => !record.classId) ? recordKey(state.profiles.find(record => !record.classId)) : null); return;
        }
        if (button.dataset.profileOpen) { selectProfile(button.dataset.profileOpen); return; }
        if (button.dataset.profileSection) { state.section = button.dataset.profileSection; state.objectId = null; render(); return; }
        if (button.dataset.profileAction) { void run(actions[button.dataset.profileAction] || (() => {})); return; }
        if (button.hasAttribute("data-profile-add-object")) {
            const draft = activeDraft();
            const map = dictionary(draft.doc, state.section);
            const id = uid();
            if (state.section === "Subjects") map[id] = { Name: "新科目", Initial: "", TeacherName: "" };
            else if (state.section === "TimeLayouts") map[id] = { Name: "新时间表", Layouts: [] };
            else { const layoutId = regularEntries(draft.doc, "TimeLayouts")[0]?.[0] || ""; map[id] = { Name: "新课表", TimeLayoutId: layoutId, TimeRule: { WeekDay: 1, WeekCountDiv: 0, WeekCountDivTotal: 2 }, IsEnabled: true, Classes: layoutId ? slots(dictionary(draft.doc, "TimeLayouts")[layoutId]).filter(isLesson).map(emptyCourse) : [] }; }
            write(draft.doc, state.section, map); state.objectId = id; markDirty(); render(); return;
        }
        if (button.hasAttribute("data-profile-delete-object") || button.dataset.profileDeleteSubject) { void run(() => deleteObject(button.dataset.profileDeleteSubject ? "Subjects" : state.section, button.dataset.profileDeleteSubject || state.objectId)); return; }
        if (button.dataset.profileSlotAction) {
            void run(async () => {
                const draft = activeDraft(); const index = Number(button.dataset.index);
                const action = button.dataset.profileSlotAction;
                const layoutId = slotLayoutId();
                if (action === "delete") {
                    const affected = affectedCourses(draft.doc, layoutId, index);
                    if (affected.length && !await ask(`此时段在 ${affected.length} 张课表中已安排课程。删除时段会同时删除对应课程，继续？`, { confirmText: "删除时段和课程" })) return;
                    removeSlot(draft.doc, layoutId, index);
                } else if (action === "insert") insertSlot(draft.doc, layoutId, index);
                else moveSlot(draft.doc, layoutId, index, index + (action === "up" ? -1 : 1));
                if (state.section === "TempLayers") markChanged(draft.doc, state.objectId);
                markDirty(); render();
            });
        }
    });
    app.addEventListener("input", event => {
        const node = event.target;
        if (node.hasAttribute("data-profile-search")) { state.filter = node.value; renderList(); return; }
        const draft = activeDraft();
        if (!draft) return;
        if (node.hasAttribute("data-profile-name")) { draft.name = node.value; write(draft.doc, "Name", node.value); markDirty(); return; }
        if (node.dataset.profileSubjectField) { write(dictionary(draft.doc, "Subjects")[node.closest("[data-profile-subject]").dataset.profileSubject], node.dataset.profileSubjectField, node.value); markDirty(); return; }
        if (node.dataset.profileObjectField === "Name") { write(dictionary(draft.doc, sectionKey())[state.objectId], "Name", node.value); markDirty(); return; }
        if (["StartTime", "EndTime", "StartSecond", "EndSecond"].includes(node.dataset.profileSlotField)) { const point = slots(dictionary(draft.doc, "TimeLayouts")[slotLayoutId()])[Number(node.closest("[data-profile-slot]").dataset.profileSlot)]; write(point, node.dataset.profileSlotField, editedTime(read(point, node.dataset.profileSlotField), node.value)); markDirty(); return; }
        if (node.dataset.profileSlotField === "BreakName") { const point = slots(dictionary(draft.doc, "TimeLayouts")[slotLayoutId()])[Number(node.closest("[data-profile-slot]").dataset.profileSlot)]; write(point, "BreakName", node.value); markDirty(); return; }
        if (node.dataset.profileRuleField) { const plan = dictionary(draft.doc, "ClassPlans")[state.objectId]; const rule = read(plan, "TimeRule", {}); write(rule, node.dataset.profileRuleField, Number(node.value)); write(plan, "TimeRule", rule); markDirty(); }
    });
    app.addEventListener("change", event => {
        const node = event.target;
        if (state.busy && !node.closest("[data-profile-question]")) return;
        if (node.dataset.profileClassSelect) { if (node.checked) state.selected.add(node.dataset.profileClassSelect); else state.selected.delete(node.dataset.profileClassSelect); renderList(); return; }
        if (node.hasAttribute("data-profile-select-all")) { state.selected = new Set(node.checked ? state.classes.map(item => item.id) : []); renderList(); return; }
        if (node.hasAttribute("data-profile-object-select")) { state.objectId = node.value; renderSection(); return; }
        if (node.name === "applyMode" || node.hasAttribute("data-profile-apply-section")) { updateApplyMode(); return; }
        if (node.hasAttribute("data-profile-file")) {
            void run(async () => { const file = node.files?.[0]; if (!file) return; if (file.size > 5 * 1024 * 1024) { dialogError("upload", "每份档案文件最大 5 MB。"); return; } $("[data-profile-upload-json]").value = await file.text(); state.preview = null; $("[data-profile-preview]").hidden = true; $("[data-profile-action=confirm-upload]").disabled = true; }); return;
        }
        if (node.hasAttribute("data-profile-upload-json")) { state.preview = null; $("[data-profile-action=confirm-upload]").disabled = true; return; }
        const draft = activeDraft(); if (!draft) return;
        if (node.hasAttribute("data-profile-temp-date")) {
            try { moveTempLayer(draft.doc, state.objectId, node.value); markDirty(); } catch (error) { feedback(error.message, true); }
            renderSection(); $("[data-profile-temp-date]")?.focus(); return;
        }
        if (node.dataset.profileSlotField) {
            const index = Number(node.closest("[data-profile-slot]").dataset.profileSlot);
            const layoutId = slotLayoutId();
            const point = slots(dictionary(draft.doc, "TimeLayouts")[layoutId])[index];
            if (node.dataset.profileSlotField === "TimeType") {
                void run(async () => {
                    const affected = affectedCourses(draft.doc, layoutId, index);
                    if (Number(node.value) !== 0 && affected.length && !await ask(`改为非上课时段会删除 ${affected.length} 张课表中的对应课程。继续？`, { confirmText: "变更类型并删除课程" })) { renderSection(); $(`[data-profile-slot="${index}"] [data-profile-slot-field="TimeType"]`)?.focus(); return; }
                    changeSlotType(draft.doc, layoutId, index, Number(node.value)); if (state.section === "TempLayers") markChanged(draft.doc, state.objectId); renderSection();
                    $(`[data-profile-slot="${index}"] [data-profile-slot-field="TimeType"]`)?.focus();
                    markDirty();
                });
                return;
            } else if (!["StartTime", "EndTime", "StartSecond", "EndSecond"].includes(node.dataset.profileSlotField)) write(point, node.dataset.profileSlotField, node.type === "checkbox" ? node.checked : node.value);
            markDirty(); return;
        }
        if (node.dataset.profileObjectField === "TimeLayoutId") {
            void run(async () => {
                const plan = dictionary(draft.doc, "ClassPlans")[state.objectId];
                if (courses(plan).some(course => refOf(read(course, "SubjectId", ""))) && !await ask("更换时间表后课程按原节次保留；超出新时间表的课程将被移除。继续？", { confirmText: "更换时间表" })) { renderSection(); $("[data-profile-object-field=TimeLayoutId]")?.focus(); return; }
                const layout = dictionary(draft.doc, "TimeLayouts")[node.value];
                const count = layout ? slots(layout).filter(isLesson).length : 0;
                const values = ensureCourses(plan, count); values.splice(count);
                write(plan, "TimeLayoutId", node.value);
                if (isOverlay(plan)) { pruneOverlayLayouts(draft.doc); markChanged(draft.doc, state.objectId); }
                markDirty(); renderSection(); $("[data-profile-object-field=TimeLayoutId]")?.focus();
            });
            return;
        }
        if (node.dataset.profileObjectField === "IsEnabled") { write(dictionary(draft.doc, "ClassPlans")[state.objectId], "IsEnabled", node.checked); markDirty(); return; }
        if (node.hasAttribute("data-profile-course")) {
            const plan = dictionary(draft.doc, "ClassPlans")[state.objectId]; const ordinal = Number(node.dataset.profileCourse);
            const values = ensureCourses(plan, ordinal + 1); write(values[ordinal], "SubjectId", node.value); markDirty();
            if (isOverlay(plan)) { markChanged(draft.doc, state.objectId); renderSection(); $(`[data-profile-course="${ordinal}"]`)?.focus(); }
        }
    });
    $("[data-profile-apply-form]").addEventListener("submit", event => {
        event.preventDefault();
        void run(async () => {
            try { const payload = applyPayload(); dialogError("apply", ""); const result = await request("Apply", payload); state.lastApply = payload; renderResults(result); closeDialog($("[data-profile-apply]")); feedback(result.message || "下发已完成，请查看逐设备结果。"); }
            catch (error) { dialogError("apply", error.message); }
        });
    });
    $("[data-profile-question-form]").addEventListener("submit", event => {
        event.preventDefault();
        const input = $("[data-profile-question-input]");
        if (!input.disabled && !input.value.trim()) { input.setCustomValidity("请填写名称。"); input.reportValidity(); return; }
        $("[data-profile-question]").close("accept");
    });
    $("[data-profile-question-input]").addEventListener("input", event => event.target.setCustomValidity(""));
    window.addEventListener("beforeunload", event => { if ([...state.drafts.values()].some(draft => draft.dirty)) { event.preventDefault(); event.returnValue = ""; } });
    $("[data-profile-upload-json]").addEventListener("input", () => { state.preview = null; $("[data-profile-action=confirm-upload]").disabled = true; $("[data-profile-preview]").hidden = true; });
    // native dialog 提供焦点约束与 Esc 关闭；关闭时将焦点还给打开它的按钮。
    $$('dialog').forEach(node => { node.addEventListener("click", event => { if (event.target === node) node.close(); }); node.addEventListener("close", () => { if (node.profileReturnFocus?.isConnected) node.profileReturnFocus.focus(); }); });
    state.profiles ||= []; state.classes ||= []; state.groups ||= []; state.devices ||= [];
    const first = state.mode === "library" ? state.profiles.find(record => !record.classId) : state.classes.find(item => item.id === state.currentClassId) || state.classes[0];
    selectProfile(first ? state.mode === "library" ? recordKey(first) : `class:${first.id}` : null);
})();
