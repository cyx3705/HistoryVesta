# MyAPI 外置测试模块

本目录是独立于 MyAPI 生产内核的模块接入夹具：`MyAPI.TestCapability` 提供不依赖 MyAPI 的普通 .NET 能力，`MyAPI.TestModule` 只负责将这些能力显式注册为命令。

该目录用于契约测试、真实 DLL 加载以及 HTTP/MCP 冒烟，不属于 MyAPI 的生产源码或发布内容。宿主仍需显式启用模块扫描后才会加载此模块。
