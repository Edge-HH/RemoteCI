package com.remoteci.watch.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.Checkbox
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.remoteci.watch.data.ConnectionManager

@Composable
internal fun TwoFactorPrompt() {
    val requested by ConnectionManager.twoFactorRequested.collectAsState()
    val state by ConnectionManager.state.collectAsState()
    if (!requested) return
    var code by remember { mutableStateOf("") }
    var recovery by remember { mutableStateOf(false) }
    val connecting = state is ConnectionManager.State.Connecting
    val palette = LocalWatchPalette.current
    Dialog(onDismissRequest = ConnectionManager::cancelTwoFactor,
        properties = DialogProperties(usePlatformDefaultWidth = false)) {
        MaterialTheme(colorScheme = darkColorScheme(primary = palette.buttonContainer, onPrimary = palette.onButtonContainer)) {
        Column(Modifier.fillMaxSize().background(Color.Black).verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 28.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("二步验证", color = Color.White)
            Text((state as? ConnectionManager.State.Error)?.message ?: "正在验证…", color = Color.White)
            OutlinedTextField(value = code, onValueChange = { code = it.take(32) },
                label = { Text(if (recovery) "恢复码" else "6 位验证码") },
                modifier = Modifier.fillMaxWidth(), singleLine = true, enabled = !connecting,
                keyboardOptions = KeyboardOptions(keyboardType = if (recovery) KeyboardType.Ascii else KeyboardType.Number))
            Checkbox(checked = recovery, onCheckedChange = { recovery = it; code = "" }, enabled = !connecting)
            Text("使用一次性恢复码", color = Color.White)
            Button(onClick = { ConnectionManager.submitTwoFactor(code, recovery); code = "" },
                enabled = !connecting && if (recovery) code.isNotBlank() else code.length == 6 && code.all(Char::isDigit)) { Text("验证登录") }
            TextButton(onClick = ConnectionManager::cancelTwoFactor) { Text("取消") }
        }
        }
    }
}
