package com.remoteci.mobile.ui

import com.remoteci.mobile.data.Swap
import com.remoteci.mobile.data.SwapCatalog
import com.remoteci.mobile.data.SwapRequestView
import com.remoteci.mobile.data.UserNotification
import com.remoteci.mobile.data.protocolJson
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SwapLogicTest {
    private val catalog = protocolJson.decodeFromString(
        SwapCatalog.serializer(),
        """
        {"teacherName":"王老师","canForce":true,"mySubjects":["数学"],"classes":[
          {"classId":"c1","className":"一班","subjects":["数学","英语"],"days":[
            {"date":"2026-10-06","courses":[
              {"index":0,"label":"第1节","subject":"数学","teacher":"王老师","mine":true},
              {"index":1,"label":"第2节","subject":"英语","teacher":"李老师","mine":false},
              {"index":2,"label":"第3节","subject":"物理","teacher":"赵老师","mine":false}]}]}]}
        """.trimIndent(),
    )

    @Test
    fun `swap tab is only visible with request permission`() {
        assertTrue(HomeTab.SwapRequests in visibleHomeTabs(true, canManage = true))
        assertFalse(HomeTab.SwapRequests in visibleHomeTabs(false, canManage = true))
        assertEquals(HomeTab.entries.size - 1, visibleHomeTabs(false, canManage = true).size)
    }

    @Test
    fun `management tab is hidden without any management item`() {
        assertTrue(HomeTab.People in visibleHomeTabs(true, canManage = true))
        assertFalse(HomeTab.People in visibleHomeTabs(true, canManage = false))
    }

    @Test
    fun `default source is the first own lesson`() {
        assertEquals(SwapPick("c1", "2026-10-06", 0), catalog.firstOwnPick())
    }

    @Test
    fun `exchange requires at least one own lesson and a reason`() {
        val own = SwapPick("c1", "2026-10-06", 0)
        val other = SwapPick("c1", "2026-10-06", 1)
        val third = SwapPick("c1", "2026-10-06", 2)
        assertNull(validateSwap(catalog, true, own, other, null, "外出教研"))
        assertEquals("两节课中至少要有一节是你自己的课", validateSwap(catalog, true, other, third, null, "外出"))
        assertEquals("请填写换课理由", validateSwap(catalog, true, own, other, null, "  "))
        assertEquals("两节课不能是同一节", validateSwap(catalog, true, own, own, null, "理由"))
    }

    @Test
    fun `replace requires a taught subject`() {
        val other = SwapPick("c1", "2026-10-06", 1)
        assertNull(validateSwap(catalog, false, null, other, "数学", "代课"))
        assertEquals("请选择你任教的学科", validateSwap(catalog, false, null, other, "英语", "代课"))
        assertEquals("目标课已经是你的这门课", validateSwap(catalog, false, null, SwapPick("c1", "2026-10-06", 0), "数学", "代课"))
    }

    @Test
    fun `swap request and user notification payloads parse`() {
        val item = protocolJson.decodeFromString(
            SwapRequestView.serializer(),
            """{"id":"a1","shortId":"a1b2c3d4","mode":1,"status":6,"forced":true,"requesterName":"王老师",
               "source":{"classId":"c1","date":"2026-10-06","index":0,"className":"一班","label":"第1节","subject":"数学"},
               "target":{"classId":"c1","date":"2026-10-06","index":1,"className":"一班","label":"第2节","subject":"英语","teacher":"李老师"},
               "reason":"紧急","canRevoke":true,"futureField":1}""",
        )
        assertEquals(Swap.STATUS_FORCED, item.status)
        assertTrue(item.canRevoke)
        assertEquals("已强制换课", Swap.statusText(item.status))
        assertTrue(swapSummary(item).contains("⇄"))
        val note = protocolJson.decodeFromString(
            UserNotification.serializer(),
            """{"id":"n1","kind":"swap_requested","title":"王老师 申请与你换课","body":"一班","swapRequestId":"a1","createdAt":"2026-10-05T08:00:00+00:00"}""",
        )
        assertEquals(Swap.KIND_REQUESTED, note.kind)
        assertNull(note.readAt)
    }
}
