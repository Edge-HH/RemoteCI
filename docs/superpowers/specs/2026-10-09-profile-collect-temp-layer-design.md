# 档案收集与临时层设计

- 日期：2026-10-09
- 状态：已实现并经 Standards/Spec 双轴审查（用户要求不再逐项确认，完成后汇总待确认点）

## 1. 目标

档案管理此前只能"上传 → 编辑 → 下发"，存在三个缺口：

1. **不能从班级设备收集档案**。应支持：从设备收集 → 在服务端编辑 → 再下发。
2. **不认识临时层**。ClassIsland 的临时层课表被当成普通课表混在列表里，不能按日期查看、编辑或新建；下发时也无法"作为临时层"推送。
3. **"创建并启用新档案"必然失败**（已在 `800ca94` 单独修复：宿主只有 `SaveSettings(string)`）。

用户确认的需求：

- 收集结果**载入为草稿**，检查、编辑后由用户保存，绝不在未确认时覆盖服务端档案。
- 收集入口：班级档案页收集本班、管理员"批量编辑"收集所选班级、档案库"从班级收集为全局模板"。
- 每班同一时间只有一台在线设备（新设备接入会顶掉旧设备），无需选择设备。
- 临时层：编辑器识别并编辑；收集时带回设备上的临时层；新增"作为临时层下发"。
- 下发临时层时，设备同一天已有临时层：**勾选确认后替换**，未勾选则该设备失败并说明日期。

## 2. ClassIsland 临时层模型（2.1，已核对宿主源码与程序集）

- 临时层课表 = `ClassPlans` 中 `IsOverlay=true` 的课表，`OverlaySourceId` 指向来源课表，`OverlaySetupTime` 为生效日期。
- 生效日期由 `OrderedSchedules[日期] = { ClassPlanId }` 决定；宿主 `CreateTempClassPlan` 每天只允许一个临时层，并同时设置 `OverlayClassPlanId` 与 `IsOverlayClassPlanEnabled`。
- 可选的临时层时间表 = `TimeLayouts` 中 `IsOverlay=true`、`OverlaySourceId` 指向原时间表的副本，名称加"（临时层）"。
- 宿主 `CleanExpiredTempClassPlan` 删除早于今天的 `OrderedSchedules`、没有被日期引用的临时层课表、没有被保留课表使用的临时层时间表。
- 宿主 `ClassPlan.RefreshClassesList` 会把课程数补齐或截断为时间表上课时段数。

## 3. 协议

| 项目 | 内容 |
| --- | --- |
| `CommandKind.ReadProfile = 26` | 插件把当前内存档案序列化为 JSON 放在 `CommandResult.data` 返回；需要 `ManageSchedule`，仅服务端档案管理入口可发起（通用命令 API、手机/手表通道、局域网直连都拒绝）。 |
| 能力 `profile.read` | 读取设备档案。旧插件没有此能力时逐班报告"需升级插件"。 |
| 能力 `profile.temp-layer` | 支持 `ProfileApplyMode.TempLayers`。旧插件只有 `profile.apply` 时同样报告需升级。 |
| `ProfileApplyMode.TempLayers = 4` | 把载荷中由 `OrderedSchedules` 指向的临时层写入设备。 |
| `ProfileApplyRequest.replaceExistingTempLayers` | 设备同日已有临时层或预定课表时是否替换。 |
| `ProfileDispatchRequest.tempLayerIds` | 要下发的临时层课表 ID；省略表示档案中全部临时层（插件跳过已过期的）。 |

## 4. 共享档案逻辑（`ProfileDocument`）

- **常规类别不再包含临时层**：`BuildSelection` 选择"全部"时跳过 `IsOverlay` 对象；显式选择临时层对象会报错，提示改用"作为临时层下发"。
- **更新/整体替换保留设备临时层**：设备原有的临时层课表和临时层时间表不属于"所选类别"，保留下来，并按宿主规则修复：课程数随时间表补齐或截断（`RefreshClassesList`），缺失科目改为空课，缺失课表群改为默认课表群，来源缺失只清空来源指针；只有所用时间表已不存在时才连同日期一并移除（审查后由"依赖失效即移除"改为修复，避免静默删掉老师的换课）。
- **`BuildTempLayerSelection`**：只保留所选临时层、它们的 `OrderedSchedules` 条目及依赖（时间表、科目、课表群、来源课表）。
- **`ApplyTempLayers(current, request, today)`**，对每个临时层：
  1. 日期早于设备今天 → 跳过并计数；全部跳过则失败。
  2. 设备该日已有安排：未勾选替换 → 失败并列出日期；勾选 → 删除该日条目，若指向临时层课表且不再被引用则一并删除（及不再使用的临时层时间表）。
  3. 科目、课表群：设备缺失才补充，不覆盖设备现有对象。
  4. 时间表：设备有同 ID 且时段一致 → 直接引用；设备缺失或时段不同 → 以新 ID 写入临时层时间表副本（`IsOverlay=true`），不改动设备常规时间表。
  5. 临时层课表以新 ID 写入，`OverlaySetupTime` 为该日，来源课表在设备上不存在时 `OverlaySourceId` 置空；按来源课表重算 `IsChangedClass`。
  6. `OrderedSchedules[日期]` 指向新课表；日期为今天时设置 `OverlayClassPlanId` 与 `IsOverlayClassPlanEnabled=true`。
- **`NormalizeCollected`**：收集结果按宿主规则补齐/截断课程数，清除悬空的临时课表指针与预定课表，使收集档案能直接通过校验。

## 5. 插件

- `ReadProfile`：UI 线程读取当前档案 JSON，超过 5 MB 返回失败。
- `ApplyProfile` 支持 `TempLayers`；事务、快照回滚沿用现有执行器，宿主写回字段增加 `IsOverlayClassPlanEnabled`。
- `ReadProfile` 与 `ApplyProfile` 一样列入局域网"仅服务端"命令。

## 6. 服务端

- `ProfileDispatchService.CollectAsync(actor, classIds, onlyClass)`：逐班校验管理权限，选该班在线插件，校验能力，并发发送 `ReadProfile`（20 秒超时），返回 `{classId, className, deviceName, success, message, profileJson, errors}`，成功项已规范化。离线、旧插件逐班报告；请求中含无权管理的班级时与下发一致，整批返回 403（界面只列出可管理的班级）。
- 页面处理器 `?handler=Collect`（班级页锁定当前班级），REST `POST /api/profiles/collect`。
- 下发：`mode=4` 时不要求 `sections`，按 `tempLayerIds` 构建载荷，要求 `profile.temp-layer` 能力。

## 7. WebUI

- 工具栏新增"从设备收集"：班级模式收集当前班；批量模式另有"收集所选班"；档案库模式打开班级选择框，收集结果成为新的模板草稿"〈班级〉档案（收集）"。
- 收集到的班级草稿替换当前草稿内容（已有未保存修改时先确认），保留服务端档案 ID 与修订号，保存时照常做修订号校验。
- 新增"临时层"标签：按日期列出临时层（标注今天/已过期、来源课表）；可改日期、名称、课程；可"新建临时层"（选来源课表与日期，可勾选"单独调整时间"生成临时层时间表）；可删除；可"清理已过期"。常规"时间表/课表"标签不再显示临时层对象。
- 下发对话框新增"作为临时层下发"：隐藏类别选择，列出未过期的临时层供勾选，提供"替换设备上同日已有的临时层或预定课表"确认项。

## 8. 测试

- 插件：`ReadProfile` 回执、`TempLayers` 的跳过过期、同日冲突（拒绝/替换）、时间表一致时引用/不同时生成临时层时间表、今天设置当前临时层、替换常规课表保留临时层；宿主适配器写回 `IsOverlayClassPlanEnabled`。
- 服务端：收集的权限、离线、旧插件、成功回执与规范化；临时层下发的能力校验与载荷；通用通道拒绝 `ReadProfile`；REST 收集。
- 前端：临时层列表、新建、改日期、删除、清理过期的纯函数测试。

## 9. 实现中追加的行为

- 校验拒绝 `OrderedSchedules` 同一天出现多个条目（如带与不带时区后缀），服务端与编辑器一致。
- 编辑器删除常规课表时，一并删除指向它的预定课表，并清空以它为来源的临时层的来源指针（确认框说明）。
- 临时层标签可切换当天时间表、直接编辑临时层时间表的时段，并标注与来源课表不同的"已换课"节次。
- 编辑器把全零 GUID 视为"无默认科目/空课"（宿主保存的档案大量使用），不再误报缺失引用。
- 编辑器用浏览器本地日期判断"今天/已过期"，设备按自身日期判断；两者跨时区时可能相差一天，以设备为准。

## 10. 不做

- 不把收集结果自动保存到服务端。
- 不提供"预定课表（日期指向常规课表）"的编辑界面，收集时原样保留。
- 不处理临时课表群（`TempClassPlanGroup*`），原样保留。
