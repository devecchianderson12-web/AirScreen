# WebRTC uses JNI callbacks into these classes.
-keep class org.webrtc.** { *; }
-dontwarn org.webrtc.**
-keepclassmembers class com.dualscreen.app.net.** { *; }
