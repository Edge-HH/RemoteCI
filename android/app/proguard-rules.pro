# 协议模型使用 kotlinx.serialization，混淆时保留序列化注解与生成的 Serializer。
-keepattributes *Annotation*, InnerClasses
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keepclasseswithmembers class com.remoteci.mobile.data.** {
    kotlinx.serialization.KSerializer serializer(...);
}
-keep,includedescriptorclasses class com.remoteci.mobile.data.**$$serializer { *; }
