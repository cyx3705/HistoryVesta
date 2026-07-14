# Athena Android 上位机开发资料索引

整理时间：2026-07-13

## 资料来源

| 文件/目录 | 内容 | 来源 |
| --- | --- | --- |
| `slamware_sdk_android.2.8.8-rtm.20220610.tar.gz` | Slamware Android SDK 原始压缩包 | https://download-en.slamtec.com/api/download/slamware-sdk-android/2.8.8-rtm?lang=netural |
| `slamware_sdk_android_2.8.8_extracted/` | Android SDK 解压目录，包含 jar 和 native so | 同上 |
| `Slamware_Android_SDK_docs_4.6.0_index.html` | Slamware Android SDK 文档首页离线副本 | https://developer.slamtec.com/docs/slamware/android-sdk/4.6.0_rtm/ |
| `Slamware_Android_SDK_docs_4.6.0_html/` | Android SDK 文档各模块 HTML 离线副本 | 同上 |
| `Slamware_Android_SDK_docs_4.6.0_html_readable/` | 尝试修复中文编码后的 HTML 副本 | 同上 |
| `Hermes_Athena2_Common_SDK_docs_index.html` | Hermes/Athena2 通用版 SDK Swagger 首页 | https://docs.slamtec.com/ |
| `Hermes_Athena2_Common_SDK_swagger-conf.json` | 通用版 RESTful API OpenAPI/Swagger 配置 | https://docs.slamtec.com/opt/swagger-conf.json |
| `Hermes_Athena2_RESTful_API_operations.csv` | 从 Swagger 生成的 RESTful API 操作清单 | 由 `swagger-conf.json` 生成 |

## Android SDK 包内容

解压后的 SDK 主要包含：

- `slamware_sdk_android.jar`
- `jniLibs/libs/arm64-v8a/librpsdk.so`
- `jniLibs/libs/armeabi-v7a/librpsdk.so`
- `jniLibs/libs/x86/librpsdk.so`

文档首页中的 Hello World 示例连接方式：

```java
AbstractSlamwarePlatform platform = DeviceManager.connect("192.168.11.1", 1445);
Pose pose = platform.getPose();
```

## 可选开发路线

1. Android App 直接集成 Slamware Android SDK  
   适合在底盘 Android 系统上做原生上位机应用。重点参考 `slamware_sdk_android.jar`、`librpsdk.so`、`Slamware_Android_SDK_docs_4.6.0_html/robot.html` 和 `slamware.html`。

2. 通过 Athena2/Hermes 通用 RESTful API 开发  
   适合用 HTTP 接口控制底盘、查询状态、定位建图、任务动作、应用安装管理等。重点参考 `Hermes_Athena2_Common_SDK_swagger-conf.json` 和 `Hermes_Athena2_RESTful_API_operations.csv`。

## RESTful API 重点分组

从 Swagger 解析到 167 个接口，主要分组包括：

- `system`：系统、电源、网络、设备信息、健康状态等
- `slam`：定位、建图、位姿、定位质量、充电桩等
- `motion`：创建/终止运动行为、查询当前行为、路径、速度、运动策略等
- `artifact`：地图语义元素
- `sensors`：传感器控制
- `application`：Android 应用程序管理，仅限 ARM 平台，包括获取已安装自定义 APP、安装 APP、卸载 APP
- `multi-floor`：多楼层地图、乘梯等
- `industry` / `delivery`：行业任务、配送服务相关接口

## 注意事项

- Android SDK 在线文档页面存在历史中文编码问题，离线副本中部分中文可能仍显示异常；代码、类名、包名和接口路径可正常参考。
- 当前项目提到的型号是“雅典娜”。若实物为 Athena 2.0，RESTful API 路线更值得优先评估，因为官网明确提供 Hermes/Athena2 通用版 SDK 文档。
- `192.168.11.1:1445` 是文档示例连接地址，实际项目需要按底盘网络配置确认。
