package com.remoteci.mobile.data

import kotlinx.serialization.Serializable

/** 每周固定星期几与时段的自定义课程；weekCountDiv/weekCountDivTotal 规则同档案课表（0 为每周，1/2 单周，2/2 双周）。 */
@Serializable
data class PersonalCourseRequest(
    val subject: String,
    val location: String,
    /** 0=周日，1=周一 … 6=周六。 */
    val weekDay: Int,
    val startTime: String,
    val endTime: String,
    val weekCountDiv: Int,
    val weekCountDivTotal: Int,
    val timeZoneOffsetMinutes: Int,
    val reminderMinutes: Int? = null,
    val notifyAtStart: Boolean = true,
)

@Serializable
data class PersonalCourseView(
    val id: String,
    val subject: String,
    val location: String = "",
    /** 旧服务端不返回以下字段，编辑时从 startsAt 推算。 */
    val weekDay: Int? = null,
    val startTime: String? = null,
    val endTime: String? = null,
    val weekCountDiv: Int = 0,
    val weekCountDivTotal: Int = 2,
    val timeZoneOffsetMinutes: Int? = null,
    /** 单次课程所在日期；每周重复的课程为 null。 */
    val date: String? = null,
    val summary: String = "",
    /** 下一次（或正在进行的）课的绝对时间。 */
    val startsAt: String,
    val endsAt: String,
    val reminderMinutes: Int? = null,
    val notifyAtStart: Boolean = true,
)

/** 服务端统一设置的学期；startDate 为空表示管理员未设置，不能选择单双周。 */
@Serializable
data class SemesterSettingsView(
    val startDate: String? = null,
    val syncToClassrooms: Boolean = true,
    val currentWeek: Int? = null,
)
