# kotlinx.serialization: @Serializable sınıfların ürettiği serileştiriciler korunmalı.
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**
-keepclassmembers class kotlinx.serialization.json.** { *** Companion; }
-keepclasseswithmembers class kotlinx.serialization.json.** { kotlinx.serialization.KSerializer serializer(...); }
-keep,includedescriptorclasses class io.github.koronerap.pulse.**$$serializer { *; }
-keepclassmembers class io.github.koronerap.pulse.** { *** Companion; }
-keepclasseswithmembers class io.github.koronerap.pulse.** { kotlinx.serialization.KSerializer serializer(...); }
