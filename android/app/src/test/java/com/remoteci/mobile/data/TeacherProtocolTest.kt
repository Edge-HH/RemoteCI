package com.remoteci.mobile.data

import kotlinx.serialization.json.Json
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** 老师角色相关协议字段的解析兼容：新字段可读、旧载荷（缺字段）不报错。 */
class TeacherProtocolTest {

    @Test
    fun userProfile_ParsesRoleKindAndIdentifiesTeacher() {
        val teacher = Json.decodeFromString<UserProfile>(
            """{"id":"u1","username":"wang","displayName":"王老师","role":1,"roleKind":5,"roleName":"老师","permissions":1025}""",
        )
        assertTrue(teacher.isTeacher)
        assertEquals("老师", teacher.roleLabel)

        val legacy = Json.decodeFromString<UserProfile>("""{"id":"u2","username":"a","displayName":"甲","role":1}""")
        assertFalse(legacy.isTeacher)
        assertEquals("用户", legacy.roleLabel)
    }

    @Test
    fun canPullScheduleFor_ChecksClassAdministratorOfThatClassOnly() {
        val user = Json.decodeFromString<UserProfile>(
            """
            {"id":"u3","username":"li","displayName":"李","role":1,"classes":[
              {"id":"class-a","name":"A 班","roleName":"自定义名称","roleKind":4},
              {"id":"class-b","name":"B 班","roleName":"学生","roleKind":1},
              {"id":"class-c","name":"C 班","roleName":"班管理员"}
            ]}
            """.trimIndent(),
        )
        assertTrue(user.canPullScheduleFor("class-a"))
        assertFalse(user.canPullScheduleFor("class-b"))
        // 旧版服务端不下发 roleKind 时退回内置角色名。
        assertTrue(user.canPullScheduleFor("class-c"))
        assertFalse(user.canPullScheduleFor(null))

        val admin = Json.decodeFromString<UserProfile>("""{"id":"u4","username":"root","displayName":"管理员","role":2}""")
        assertTrue(admin.canPullScheduleFor("class-b"))
    }

    @Test
    fun courseEntry_TeacherIsOptionalForLegacyPayloads() {
        val withTeacher = Json.decodeFromString<CourseEntry>(
            """{"index":0,"label":"第一节","subjectId":"11111111-1111-1111-1111-111111111111","subject":"数学","teacher":"王老师","enabled":true}""",
        )
        assertEquals("王老师", withTeacher.teacher)

        val legacy = Json.decodeFromString<CourseEntry>(
            """{"index":1,"label":"第二节","subjectId":"11111111-1111-1111-1111-111111111111","subject":"体育","enabled":true}""",
        )
        assertNull(legacy.teacher)
    }

    @Test
    fun myScheduleResponse_ParsesAggregatedDays() {
        val response = Json.decodeFromString<MyScheduleResponse>(
            """
            {"fromDate":"2026-09-29","generatedAt":"2026-09-29T08:00:00+08:00","days":[
              {"date":"2026-09-29","items":[
                {"classId":"22222222-2222-2222-2222-222222222222","className":"高一(3)班","courses":[
                  {"index":0,"label":"第 1 节","subjectId":"33333333-3333-3333-3333-333333333333","subject":"数学","startTime":"08:00","endTime":"08:45","teacher":"王老师","enabled":true}
                ]}
              ]}
            ]}
            """.trimIndent(),
        )
        val item = response.days.single().items.single()
        assertEquals("高一(3)班", item.className)
        assertEquals("王老师", item.courses.single().teacher)
    }
}
