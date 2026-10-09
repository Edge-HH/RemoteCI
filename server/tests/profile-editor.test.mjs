import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const sandbox = {};
vm.runInNewContext(readFileSync(new URL("../RemoteCI.Server/wwwroot/profiles.js", import.meta.url), "utf8"), sandbox);
const transform = sandbox.RemoteCIProfileTransforms;
const fixture = () => ({
    Name: "测试档案", ExtraPlugin: { keep: true },
    TimeLayouts: { layout: { Name: "时间表", AttachedSettings: { plugin: "keep" }, Layouts: [
        { StartTime: "08:00:00", EndTime: "08:40:00", TimeType: 0, Other: "first" },
        { StartTime: "08:40:00", EndTime: "08:50:00", TimeType: 1 },
        { StartTime: "08:50:00", EndTime: "09:30:00", TimeType: 0, Other: "second" },
    ] } },
    ClassPlans: {
        monday: { Name: "周一", TimeLayoutId: "layout", TimeRule: { WeekDay: 1, WeekCountDiv: 0, WeekCountDivTotal: 2, Custom: 7 }, Classes: [{ SubjectId: "math", Extra: "one" }, { SubjectId: "lang", Extra: "two" }] },
        tuesday: { Name: "周二", TimeLayoutId: "layout", Classes: [{ SubjectId: "lang" }, { SubjectId: "math" }] },
        other: { Name: "其他", TimeLayoutId: "other", Classes: [{ SubjectId: "math" }] },
    },
    Subjects: { math: { Name: "数学", Initial: "数", TeacherName: "王老师", Custom: { retained: true } }, lang: { Name: "语文" } },
});
const subjects = plan => Array.from(plan.Classes, course => course.SubjectId);

test("插入上课时段只在关联课表对应位置留空，保留课程及附加字段", () => {
    const doc = fixture();
    transform.insertSlot(doc, "layout", 2);
    assert.deepEqual(subjects(doc.ClassPlans.monday), ["math", "", "lang"]);
    assert.deepEqual(subjects(doc.ClassPlans.tuesday), ["lang", "", "math"]);
    assert.deepEqual(subjects(doc.ClassPlans.other), ["math"]);
    assert.equal(doc.ClassPlans.monday.Classes[2].Extra, "two");
    assert.equal(doc.TimeLayouts.layout.Layouts[2].StartTime, "08:00:00");
    assert.deepEqual(doc.ExtraPlugin, { keep: true });
});

test("课间插删不移动课程，删除上课时段报告受影响课表", () => {
    const doc = fixture();
    assert.equal(transform.affectedCourses(doc, "layout", 1).length, 0);
    assert.equal(transform.affectedCourses(doc, "layout", 2).length, 2);
    transform.removeSlot(doc, "layout", 1);
    assert.deepEqual(subjects(doc.ClassPlans.monday), ["math", "lang"]);
    transform.removeSlot(doc, "layout", 1);
    assert.deepEqual(subjects(doc.ClassPlans.monday), ["math"]);
    assert.deepEqual(subjects(doc.ClassPlans.tuesday), ["lang"]);
});

test("移动时段保持课程与原上课时段的对应关系", () => {
    const doc = fixture();
    transform.moveSlot(doc, "layout", 2, 0);
    assert.deepEqual(subjects(doc.ClassPlans.monday), ["lang", "math"]);
    assert.equal(doc.ClassPlans.monday.Classes[0].Extra, "two");
    assert.equal(doc.TimeLayouts.layout.Layouts[0].Other, "second");
    transform.moveSlot(doc, "layout", 2, 0); // 移动课间，不改变课程顺序
    assert.deepEqual(subjects(doc.ClassPlans.monday), ["lang", "math"]);
});

test("改变时间点类型移除或插入对应课程，保留未受影响内容", () => {
    const doc = fixture();
    transform.changeSlotType(doc, "layout", 0, 1);
    assert.deepEqual(subjects(doc.ClassPlans.monday), ["lang"]);
    transform.changeSlotType(doc, "layout", 0, 0);
    assert.deepEqual(subjects(doc.ClassPlans.monday), ["", "lang"]);
    assert.equal(doc.ClassPlans.monday.Classes[1].Extra, "two");
});

test("正式TimeSpan优先，兼容旧秒数及DateTime并保留日期时区", () => {
    assert.equal(transform.pointTime({ StartTime: "08:00:00", StartSecond: "" }, true), "08:00:00");
    assert.equal(transform.pointTime({ StartSecond: 28800 }, true), 28800);
    assert.equal(transform.editedTime(28800, "08:15"), 29700);
    assert.equal(transform.editedTime("2026-10-08T08:00:00+08:00", "09:15"), "2026-10-08T09:15:00+08:00");
    assert.equal(transform.editedTime("08:00:00", "09:15"), "09:15:00");
    assert.equal(transform.timeText(29700), "08:15:00");
    assert.equal(transform.timeText("2026-10-08T08:00:00Z"), "08:00:00");
    assert.ok(Number.isNaN(transform.timeSeconds("24:01:00")));
});

test("字段编辑和JSON往返保留未知字段及原始大小写", () => {
    const doc = fixture();
    transform.write(doc.Subjects.math, "TeacherName", "李老师");
    transform.write(doc.ClassPlans.monday.TimeRule, "WeekDay", 3);
    const roundtrip = JSON.parse(JSON.stringify(transform.clone(doc)));
    assert.deepEqual(roundtrip.Subjects.math.Custom, { retained: true });
    assert.equal(roundtrip.ClassPlans.monday.TimeRule.Custom, 7);
    assert.equal(roundtrip.TimeLayouts.layout.AttachedSettings.plugin, "keep");
    const lower = { name: "old", custom: 8 };
    transform.write(lower, "Name", "new");
    assert.deepEqual(lower, { name: "new", custom: 8 });
});

test("缺失引用、无效时间和课程数量不一致阻止保存", () => {
    const doc = fixture();
    delete doc.ClassPlans.other;
    assert.equal(transform.validateProfile(doc).length, 0);
    doc.TimeLayouts.layout.Layouts[0].EndTime = "07:00:00";
    doc.TimeLayouts.layout.Layouts[0].DefaultClassId = "missing";
    doc.ClassPlans.monday.Classes[0].SubjectId = "missing";
    doc.ClassPlans.tuesday.Classes.pop();
    const errors = transform.validateProfile(doc);
    assert.equal(errors.length, 4);
    assert.ok(Array.from(errors).some(error => error.includes("起止时间无效")));
    assert.ok(Array.from(errors).some(error => error.includes("默认科目")));
    assert.ok(Array.from(errors).some(error => error.includes("课程数")));
});

test("GUID引用的大小写差异不影响验证或关联课程映射", () => {
    const doc = fixture();
    delete doc.ClassPlans.other;
    doc.ClassPlans.monday.TimeLayoutId = "LAYOUT";
    doc.ClassPlans.monday.Classes[0].SubjectId = "MATH";
    assert.equal(transform.validateProfile(doc).length, 0);
    transform.insertSlot(doc, "layout", 0);
    assert.deepEqual(subjects(doc.ClassPlans.monday), ["", "MATH", "lang"]);
});

// 沙箱对象来自另一个 realm，比较前转成本 realm 的普通 JSON。
const plain = value => JSON.parse(JSON.stringify(value));
const withTempLayer = () => {
    const doc = fixture();
    const id = transform.createTempLayer(doc, "monday", "2026-10-12");
    return { doc, id };
};

test("新建临时层复制来源课表并按日期安排，常规列表不包含临时层", () => {
    const { doc, id } = withTempLayer();
    const layer = doc.ClassPlans[id];
    assert.equal(layer.IsOverlay, true);
    assert.equal(layer.OverlaySourceId, "monday");
    assert.equal(layer.OverlaySetupTime, "2026-10-12T00:00:00");
    assert.equal(layer.Name, "周一（临时层）");
    assert.deepEqual(subjects(layer), ["math", "lang"]);
    assert.equal(doc.OrderedSchedules["2026-10-12T00:00:00"].ClassPlanId, id);
    assert.deepEqual(plain(transform.tempLayers(doc).map(item => [item.id, item.date, item.sourceId])), [[id, "2026-10-12", "monday"]]);
    assert.ok(!transform.regularEntries(doc, "ClassPlans").some(([key]) => key === id));
    assert.ok(!transform.validateProfile(doc).some(error => /临时层|预定课表/.test(error)));
    assert.throws(() => transform.createTempLayer(doc, "tuesday", "2026-10-12"), /已有临时层/);
    assert.throws(() => transform.createTempLayer(doc, id, "2026-10-13"), /常规课表/);
});

test("单独调整当天时间生成临时层时间表，常规时间表保持不变", () => {
    const doc = fixture();
    const id = transform.createTempLayer(doc, "monday", "2026-10-12", true);
    const layoutId = doc.ClassPlans[id].TimeLayoutId;
    assert.notEqual(layoutId, "layout");
    assert.equal(doc.TimeLayouts[layoutId].IsOverlay, true);
    assert.equal(doc.TimeLayouts[layoutId].OverlaySourceId, "layout");
    assert.equal(doc.TimeLayouts[layoutId].Name, "时间表（临时层）");
    assert.ok(!transform.regularEntries(doc, "TimeLayouts").some(([key]) => key === layoutId));
    transform.deleteTempLayer(doc, id);
    assert.equal(doc.TimeLayouts[layoutId], undefined);
    assert.deepEqual(plain(doc.OrderedSchedules), {});
});

test("修改临时层日期不允许与其他安排重叠，换课标记相对来源课表", () => {
    const { doc, id } = withTempLayer();
    transform.createTempLayer(doc, "tuesday", "2026-10-14");
    assert.throws(() => transform.moveTempLayer(doc, id, "2026-10-14"), /已有临时层/);
    transform.moveTempLayer(doc, id, "2026-10-13");
    assert.deepEqual(plain(Object.keys(doc.OrderedSchedules).sort()), ["2026-10-13T00:00:00", "2026-10-14T00:00:00"]);
    assert.equal(doc.ClassPlans[id].OverlaySetupTime, "2026-10-13T00:00:00");
    doc.ClassPlans[id].Classes[1].SubjectId = "math";
    transform.markChanged(doc, id);
    assert.deepEqual(plain(doc.ClassPlans[id].Classes.map(course => course.IsChangedClass)), [false, true]);
});

test("清理过期临时层与宿主规则一致，并清除指向它们的指针", () => {
    const doc = fixture();
    const past = transform.createTempLayer(doc, "monday", "2026-10-08", true);
    const future = transform.createTempLayer(doc, "tuesday", "2026-10-12");
    doc.OverlayClassPlanId = past;
    assert.equal(transform.cleanExpiredTempLayers(doc, "2026-10-10"), 1);
    assert.equal(doc.ClassPlans[past], undefined);
    assert.ok(doc.ClassPlans[future]);
    assert.equal(doc.OverlayClassPlanId, "00000000-0000-0000-0000-000000000000");
    assert.equal(Object.values(doc.TimeLayouts).filter(layout => layout.IsOverlay).length, 0);
});

test("删除常规课表同时移除其预定课表并清空临时层来源", () => {
    const { doc, id } = withTempLayer();
    doc.OrderedSchedules["2026-10-20T00:00:00"] = { ClassPlanId: "monday" };
    transform.deletePlan(doc, "monday");
    assert.equal(doc.ClassPlans.monday, undefined);
    assert.equal(doc.OrderedSchedules["2026-10-20T00:00:00"], undefined);
    assert.equal(doc.ClassPlans[id].OverlaySourceId, null);
    assert.ok(!transform.validateProfile(doc).some(error => /临时层|预定课表/.test(error)));
});

test("预定课表引用缺失课表或同日重复时校验报错", () => {
    const doc = fixture();
    doc.OrderedSchedules = { "2026-10-12T00:00:00": { ClassPlanId: "missing" }, "2026-10-13T00:00:00": { ClassPlanId: "monday" }, "2026-10-13T00:00:00+08:00": { ClassPlanId: "tuesday" } };
    const errors = transform.validateProfile(doc);
    assert.ok(errors.some(error => error.includes("不存在的课表")));
    assert.ok(errors.some(error => error.includes("多个")));
});

test("全零 GUID 表示无默认科目或空课，不算缺失引用", () => {
    const doc = fixture();
    delete doc.ClassPlans.other;
    doc.TimeLayouts.layout.Layouts[0].DefaultClassId = "00000000-0000-0000-0000-000000000000";
    doc.ClassPlans.monday.Classes[0].SubjectId = "00000000-0000-0000-0000-000000000000";
    assert.deepEqual(plain(transform.validateProfile(doc)), []);
    assert.equal(transform.affectedCourses(doc, "layout", 0).length, 1);
});

test("新建临时层校验失败时不改动档案", () => {
    const doc = fixture();
    assert.throws(() => transform.createTempLayer(doc, "monday", "不是日期"), /有效的日期/);
    assert.equal(doc.OrderedSchedules, undefined);
});

test("清理过期使用传入的教室端日期", () => {
    const doc = fixture();
    const layer = transform.createTempLayer(doc, "monday", "2026-10-10");
    assert.equal(transform.cleanExpiredTempLayers(doc, "2026-10-10"), 0);
    assert.equal(transform.cleanExpiredTempLayers(doc, "2026-10-11"), 1);
    assert.equal(doc.ClassPlans[layer], undefined);
});
