package com.remoteci.mobile.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.foundation.layout.Row
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.text.input.KeyboardType
import com.remoteci.mobile.data.ConnectionManager

@Composable
internal fun TwoFactorPrompt() {
    val requested by ConnectionManager.twoFactorRequested.collectAsState()
    val state by ConnectionManager.state.collectAsState()
    if (!requested) return
    var code by remember { mutableStateOf("") }
    var recovery by remember { mutableStateOf(false) }
    val connecting = state is ConnectionManager.State.Connecting
    AlertDialog(
        onDismissRequest = ConnectionManager::cancelTwoFactor,
        title = { Text("二步验证") },
        text = {
            Column {
                Text((state as? ConnectionManager.State.Error)?.message ?: "正在验证…")
                OutlinedTextField(
                    value = code, onValueChange = { code = it.take(32) },
                    label = { Text(if (recovery) "一次性恢复码" else "6 位验证码") },
                    singleLine = true, enabled = !connecting,
                    keyboardOptions = KeyboardOptions(keyboardType = if (recovery) KeyboardType.Ascii else KeyboardType.Number),
                )
                Row {
                    Checkbox(checked = recovery, onCheckedChange = { recovery = it; code = "" }, enabled = !connecting)
                    Text("使用恢复码")
                }
            }
        },
        confirmButton = {
            TextButton(onClick = { ConnectionManager.submitTwoFactor(code, recovery); code = "" },
                enabled = !connecting && if (recovery) code.isNotBlank() else code.length == 6 && code.all(Char::isDigit)) { Text("验证并登录") }
        },
        dismissButton = { TextButton(onClick = ConnectionManager::cancelTwoFactor) { Text("取消") } },
    )
}
