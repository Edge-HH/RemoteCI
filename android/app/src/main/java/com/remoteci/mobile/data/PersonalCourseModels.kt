package com.remoteci.mobile.data

import kotlinx.serialization.Serializable

@Serializable
data class PersonalCourseRequest(
    val subject: String,
    val location: String = "",
    val startsAt: String,
    val endsAt: String,
    val reminderMinutes: Int? = null,
    val notifyAtStart: Boolean = true,
)

@Serializable
data class PersonalCourseView(
    val id: String,
    val subject: String,
    val location: String = "",
    val startsAt: String,
    val endsAt: String,
    val reminderMinutes: Int? = null,
    val notifyAtStart: Boolean = true,
)
