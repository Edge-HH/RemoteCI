package com.remoteci.mobile.data

/**
 * 扩展参数表单的纯逻辑：初始值、候选项显示名与提交前校验。
 * 校验规则与服务端/插件执行端一致，避免把必然被拒绝的命令发出去。
 */
fun ExtensionParameter.optionLabel(index: Int): String {
    val option = options.getOrNull(index) ?: return ""
    val labels = optionLabels
    return if (labels != null && labels.size == options.size && labels[index].isNotBlank()) labels[index] else option
}

/** 开关参数只能是 "true"/"false"：没有默认值时按关闭处理，其余类型使用声明的默认值。 */
fun initialExtensionArgs(parameters: List<ExtensionParameter>): Map<String, String?> =
    parameters.associate { parameter ->
        parameter.key to when (parameter.type) {
            Protocol.EXT_PARAM_SWITCH ->
                if (parameter.defaultValue.equals("true", ignoreCase = true)) "true" else "false"
            else -> parameter.defaultValue
        }
    }

/** 返回第一条面向用户的错误；全部通过时返回 null。 */
fun validateExtensionArgs(parameters: List<ExtensionParameter>, args: Map<String, String?>): String? {
    for (parameter in parameters) {
        val name = parameter.label.ifBlank { parameter.key }
        val value = args[parameter.key]?.trim()
        if (value.isNullOrEmpty()) {
            if (parameter.required) return "请填写“$name”"
            continue
        }
        if (value.length > 4096) return "“$name”过长"
        when (parameter.type) {
            Protocol.EXT_PARAM_NUMBER -> {
                val number = value.toDoubleOrNull()?.takeIf { it.isFinite() } ?: return "“$name”必须是数字"
                if ((parameter.min != null && number < parameter.min) || (parameter.max != null && number > parameter.max))
                    return "“$name”超出允许范围"
            }
            Protocol.EXT_PARAM_SWITCH ->
                if (!value.equals("true", ignoreCase = true) && !value.equals("false", ignoreCase = true))
                    return "“$name”只能是开启或关闭"
            Protocol.EXT_PARAM_SELECT ->
                if (parameter.options.isNotEmpty() && value !in parameter.options) return "“$name”不是有效选项"
        }
    }
    return null
}
