package com.remoteci.mobile.ui

import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.SelectableDates
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset

/** DatePicker 以 UTC 零点的毫秒数表示日期。 */
private fun LocalDate.toPickerMillis(): Long = atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli()

private fun Long.toPickerDate(): LocalDate = Instant.ofEpochMilli(this).atZone(ZoneOffset.UTC).toLocalDate()

/** 日期选择弹窗：只能选 [min, max]（含首尾）之间的日期；换课用它在今天起 30 天内任选一天。 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun BoundedDatePickerDialog(
    initial: LocalDate?,
    min: LocalDate,
    max: LocalDate,
    onDismiss: () -> Unit,
    onPick: (LocalDate) -> Unit,
) {
    val selectable = remember(min, max) {
        object : SelectableDates {
            override fun isSelectableDate(utcTimeMillis: Long): Boolean {
                val date = utcTimeMillis.toPickerDate()
                return !date.isBefore(min) && !date.isAfter(max)
            }

            override fun isSelectableYear(year: Int): Boolean = year in min.year..max.year
        }
    }
    val state = rememberDatePickerState(
        initialSelectedDateMillis = initial?.takeIf { !it.isBefore(min) && !it.isAfter(max) }?.toPickerMillis(),
        initialDisplayedMonthMillis = (initial ?: min).toPickerMillis(),
        yearRange = min.year..max.year,
        selectableDates = selectable,
    )
    DatePickerDialog(
        onDismissRequest = onDismiss,
        confirmButton = {
            TextButton(
                onClick = {
                    state.selectedDateMillis?.let { onPick(it.toPickerDate()) }
                    onDismiss()
                },
                enabled = state.selectedDateMillis != null,
            ) { Text("确定") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("取消") } },
    ) {
        DatePicker(state = state, showModeToggle = false)
    }
}
