package com.remoteci.mobile.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Apartment
import androidx.compose.material.icons.rounded.CalendarMonth
import androidx.compose.material.icons.rounded.DesktopWindows
import androidx.compose.material.icons.rounded.Dns
import androidx.compose.material.icons.rounded.Extension
import androidx.compose.material.icons.rounded.Group
import androidx.compose.material.icons.rounded.NotificationsActive
import androidx.compose.material.icons.rounded.School
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp

private fun groupIcon(id: String): ImageVector = when (id) {
    "schedule" -> Icons.Rounded.CalendarMonth
    "messages" -> Icons.Rounded.NotificationsActive
    "device" -> Icons.Rounded.DesktopWindows
    "classisland" -> Icons.Rounded.Extension
    "class" -> Icons.Rounded.School
    "account" -> Icons.Rounded.Group
    "academic" -> Icons.Rounded.Apartment
    else -> Icons.Rounded.Dns
}

/**
 * 分组权限清单：勾选状态即账号或角色最终拥有的权限。传入 [baseline]（所选角色的默认权限）时，
 * 与之不同的项标出“已新增 / 已收回”，并提供“恢复为角色默认”。[locked] 用于管理员：全部勾选且不可修改。
 */
@Composable
internal fun PermissionChecklist(
    selection: PermissionSelection,
    onChange: (PermissionSelection) -> Unit,
    description: String,
    modifier: Modifier = Modifier,
    title: String = "权限",
    baseline: PermissionSelection? = null,
    locked: Boolean = false,
) {
    fun checked(item: PermissionItem) = locked || selection.has(item)
    Column(modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleSmall)
                Text(description, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (baseline != null) {
                TextButton(onClick = { onChange(baseline) }, enabled = !locked && !selection.matches(baseline)) {
                    Text("恢复为角色默认")
                }
            }
        }
        PermissionCatalog.groups.forEach { group ->
            Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
                val count = group.items.count(::checked)
                val editable = group.items.filterNot { it.isBase }
                val allChecked = editable.all(::checked)
                Row(Modifier.padding(start = 4.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                    Box(
                        Modifier.size(32.dp).clip(CircleShape).background(MaterialTheme.colorScheme.primaryContainer),
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(groupIcon(group.id), contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(18.dp))
                    }
                    Spacer(Modifier.width(10.dp))
                    Column(Modifier.weight(1f)) {
                        Text(group.title, style = MaterialTheme.typography.titleSmall)
                        Text("${group.scope} · 已选 $count/${group.items.size}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    TextButton(onClick = { onChange(selection.withGroup(group, !allChecked)) }, enabled = !locked) {
                        Text(if (allChecked) "全不选" else "全选")
                    }
                }
                group.items.forEachIndexed { index, item ->
                    val isChecked = checked(item)
                    val enabled = !locked && !item.isBase
                    val adjustment = if (locked) null else selection.adjustmentLabel(item, baseline)
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .clip(connectedShape(index, group.items.size, outer = 20.dp))
                            .background(MaterialTheme.colorScheme.surfaceContainerLow)
                            .toggleable(value = isChecked, enabled = enabled, role = Role.Checkbox) { onChange(selection.with(item, it)) }
                            .padding(horizontal = 8.dp, vertical = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Checkbox(checked = isChecked, onCheckedChange = null, enabled = enabled, modifier = Modifier.padding(8.dp))
                        Column(Modifier.weight(1f).padding(vertical = 4.dp)) {
                            Text(item.label, style = MaterialTheme.typography.bodyLarge)
                            Text(item.hint, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        if (adjustment != null) {
                            val added = isChecked
                            Surface(
                                shape = RoundedCornerShape(8.dp),
                                color = if (added) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.errorContainer,
                                modifier = Modifier.padding(start = 8.dp, end = 4.dp),
                            ) {
                                Text(
                                    adjustment,
                                    style = MaterialTheme.typography.labelSmall,
                                    color = if (added) MaterialTheme.colorScheme.onSecondaryContainer else MaterialTheme.colorScheme.onErrorContainer,
                                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 3.dp),
                                )
                            }
                        }
                    }
                }
            }
        }
        Text(PermissionCatalog.ADMIN_ONLY_NOTE, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}
