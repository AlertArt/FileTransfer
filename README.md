# FileTransferApp — 跨平台局域网文件快传

基于 **Avalonia UI 11 + .NET 10** 的跨平台文件传输应用，支持 **Windows ↔ Android** 局域网高速文件互传。

> 设计目标：零配置、即开即用、现代化 UI、大文件稳定传输。

---

## ✨ 功能特性

| 能力 | 说明 |
|------|------|
| 📡 自动设备发现 | UDP 多播 + 广播双路心跳，自动发现同一局域网内的设备 |
| 🔗 手动直连 | UDP 被 AP 隔离 / 随机 MAC 时，可手动输入目标 IP 直连 |
| 📤 文件发送 | 支持任意类型 / 任意大小文件，2 MB 切片断点续传 |
| 📥 文件接收 | 接收端自动审批，SHA-256 完整性校验 |
| 🖼️ 缩略图预览 | 图片自动生成缩略图，传输卡片可视化 |
| 📱 响应式 UI | 现代 App 风格设计令牌，手机竖屏 / 横屏 / 桌面自适应 |
| 🔐 签名方案 | V1 (JAR) + V2 (APK Sig v2) 双签名，兼容 Android 6 ~ 15 |
| 🛡️ 前台服务保活 | Android 8+ 前台服务 + 通知，后台持续监听 |

---

## 🛠️ 技术栈

- **UI 框架**: Avalonia UI 11.x (Fluent Theme + 自定义 Design Tokens)
- **运行时**: .NET 10
- **MVVM**: CommunityToolkit.Mvvm (源生成器)
- **DI**: Microsoft.Extensions.DependencyInjection
- **设备发现**: UDP 多播 (`239.255.255.250:53317`) + 广播
- **文件传输**: HTTP/1.1 (自定义轻量服务器，端口 `53318`) + `System.IO.Pipelines` 零拷贝
- **完整性**: SHA-256 全文件校验 + 切片级 Hash
- **Android 保活**: Foreground Service (`TypeDataSync`) + `WifiManager.MulticastLock`

---

## 📁 项目结构

```
FileTransfer/
├── src/
│   ├── FileTransferApp.Core/          # 跨平台核心 (协议 / 引擎 / 模型)
│   │   ├── Models/                     # DeviceNode / TransferTaskInfo / FileMetadata 等
│   │   ├── Protocols/                  # ProtocolConstants (端口、路由、头)
│   │   ├── Messaging/                  # MVVM 消息总线消息定义
│   │   └── Services/
│   │       ├── Interfaces/             # IDiscoveryService / ITransferEngine / IStorageService ...
│   │       └── Impl/
│   │           ├── UdpDiscoveryService.cs       # UDP 设备发现
│   │           ├── TransferHttpServer.cs        # 轻量 HTTP 传输服务器
│   │           ├── PipelinesTransferEngine.cs   # Pipelines 零拷贝传输引擎
│   │           └── TransferStateMachine.cs      # 状态机 (Created→Preparing→...→Completed)
│   │
│   ├── FileTransferApp/               # 共享 UI (Avalonia View + ViewModel)
│   │   ├── ViewModels/                 # MainViewModel / DeviceListViewModel / TransferItemViewModel
│   │   ├── Views/                      # MainView.axaml (响应式布局)
│   │   ├── Services/                   # AvaloniaFilePickerService / DialogTransferApprovalService
│   │   └── App.axaml.cs                # 启动入口 + 后台服务初始化
│   │
│   ├── FileTransferApp.Desktop/       # Windows 桌面头项目
│   │   ├── Services/
│   │   │   ├── DesktopStorageService.cs        # System.IO 直接读写
│   │   │   └── WindowsFirewallRegistrar.cs     # 防火墙端口放行注册
│   │   └── Program.cs
│   │
│   └── FileTransferApp.Android/       # Android 头项目
│       ├── Services/
│       │   ├── AndroidStorageService.cs        # 存储权限降级策略
│       │   ├── TransferForegroundService.cs    # 前台保活服务
│       │   └── AndroidKeepAliveService.cs
│       ├── Application.cs                      # DI 初始化 + 前台服务启动
│       ├── MainActivity.cs                     # 权限申请 (POST_NOTIFICATIONS 等)
│       └── Properties/AndroidManifest.xml
│
├── assets/appicon/                     # 应用图标源文件
├── build/                              # 发布产物 (已 gitignore)
│   ├── win-x64/                        # Windows 自包含 exe
│   └── android/                        # Android fat APK (armeabi-v7a + arm64-v8a)
└── Avalonia_CrossPlatform_FileTransfer_Design_Doc.md   # 详细设计文档
```

---

## 🔌 通信协议

### 端口

| 用途 | 端口 | 协议 |
|------|------|------|
| 设备发现 | `53317` | UDP (多播 `239.255.255.250` + 广播 `255.255.255.255`) |
| 文件传输 | `53318` | HTTP/1.1 (TCP) |

### HTTP 接口

| 方法 | 路径 | 说明 |
|------|------|------|
| POST | `/api/v1/transfer/prepare` | 握手：发送文件元数据，对方返回是否接受 + 已接收切片（断点续传） |
| POST | `/api/v1/transfer/chunk` | 推送单个切片 (2 MB)，Header 带 `X-File-Id` / `X-Chunk-Index` / `X-Chunk-Hash` |
| POST | `/api/v1/transfer/control` | 控制：暂停 / 恢复 / 取消 |

### 状态机

```
Created → Preparing → WaitingApproval → Transferring → Completed
                                      ↘ Paused → Transferring
                                      ↘ Disconnected
                                      ↘ Cancelled
                                      ↘ Failed
```

> 状态流转严格遵循 `TransferStateMachine`，禁止非法跳转（如 `Created → WaitingApproval`）。

---

## 🚀 快速开始

### 环境要求

- .NET 10 SDK
- Android SDK (API 36 build-tools) + JDK 21 (仅 Android 构建)
- Windows 10+ / Android 6.0+ (API 23+)

### 构建 Windows

```bash
dotnet publish src/FileTransferApp.Desktop/FileTransferApp.Desktop.csproj \
  -c Release -r win-x64 --self-contained true \
  -o build/win-x64
```

产物：`build/win-x64/FileTransferApp.Desktop.exe`

### 构建 Android

> 由于 .NET for Android 多 ABI 打包的 `AndroidSupportedAbis` 属性在部分 SDK 版本下不生效，本项目采用 **单 ABI 发布 + 手动合并** 方案生成 Fat APK。

```bash
# 1. 编译 32 位 (armeabi-v7a)
dotnet publish src/FileTransferApp.Android/FileTransferApp.Android.csproj \
  -c Release -r android-arm -p:AndroidPackageFormat=apk \
  -o build/android/tmp_armv7

# 2. 编译 64 位 (arm64-v8a)
dotnet publish src/FileTransferApp.Android/FileTransferApp.Android.csproj \
  -c Release -r android-arm64 -p:AndroidPackageFormat=apk \
  -o build/android/tmp_arm64

# 3. 合并 lib/ 目录 + V1/V2 签名 (使用 apksigner)
#    详见 build/android/MergeFat.ps1
```

最终产物：`build/android/com.CompanyName.FileTransferApp-Signed.apk`

---

## 📱 使用指南

### 两台设备互传（Windows → Android）

1. **Android 端**：安装 APK 并打开，查看顶部副标题显示的 `LAN IP: x.x.x.x`
2. **Windows 端**：左侧「附近设备」面板直连区：
   - IP 填入 Android 的 LAN IP
   - 端口保持 `53318`（传输端口，不是发现端口 53317）
   - 点击「直连」→ 自动生成并选中目标设备
3. 点击顶栏 **📁 选择文件并发送**，选择文件后自动开始传输
4. Android 端自动接收，文件落在 `Download/` 目录（无权限时降级到应用私有目录）

### 自动发现（同网段且 UDP 可达时）

- 设备每 3 秒向 `239.255.255.250:53317` 广播心跳
- 收到心跳后自动加入设备列表，10 秒未收到标记离线
- **注意**：家用路由器 AP 隔离 / Android 随机 MAC 可能阻断 UDP，此时使用手动直连

---

## ⚙️ Android 权限说明

| 权限 | 用途 |
|------|------|
| `INTERNET` | 网络通信 |
| `ACCESS_NETWORK_STATE` | 网络状态检测 |
| `ACCESS_WIFI_STATE` | WiFi 信息 + MulticastLock |
| `CHANGE_WIFI_MULTICAST_STATE` | 接收 UDP 多播包 |
| `POST_NOTIFICATIONS` (Android 13+) | 前台服务通知 |
| `FOREGROUND_SERVICE` + `FOREGROUND_SERVICE_DATA_SYNC` | 后台保活 |
| `MANAGE_EXTERNAL_STORAGE` | 写入公共 Download 目录（未授权时降级到应用私有目录） |
| `usesCleartextTraffic="true"` | 允许 HTTP 明文传输（局域网无 TLS） |

---

## 🔍 故障排查

### 握手失败 HTTP 400

**原因**：发送端 JSON 为 camelCase，接收端反序列化大小写敏感 / `Expect: 100-continue` 未处理 / 大 body 使用 chunked 编码。

**已修复**：接收端 `JsonSerializerOptions.PropertyNameCaseInsensitive = true`；服务器响应 `100 Continue`；发送端改用 `ByteArrayContent` 确保 `Content-Length`。

### Android 无法接收

1. 确认两台设备在**同一局域网**且可互 ping
2. 检查 Android 端顶部 `LAN IP` 是否为真实局域网地址（非 `169.254.x.x`）
3. Windows 防火墙是否放行 `53318` 端口（首次启动会自动注册）
4. 若 UDP 发现不可用，使用**手动直连**输入 Android LAN IP

### 安装解析包失败

确认 APK 包含目标设备 ABI（本项目 fat APK 同时包含 `armeabi-v7a` + `arm64-v8a`），并使用 V1+V2 签名。

### 查看 Android 端诊断日志

```bash
adb logcat -s FTA.HTTP FTA.BOOT
```

---

## 🧠 关键设计决策

1. **HTTP 而非自定义 TCP 协议**：便于调试、穿越防火墙、利用成熟生态
2. **System.IO.Pipelines 零拷贝**：接收端 `Pipe` 连接网络流与文件流，减少内存拷贝
3. **状态机严格约束**：`TransferStateMachine` 防止非法状态跳转导致的 UI 卡死
4. **存储权限降级**：Android 11+ 无 `MANAGE_EXTERNAL_STORAGE` 时自动降级到应用私有外存，保证文件可写入
5. **前台服务保活**：Android 8+ 必须以前台服务形式持续监听，否则系统 1 分钟内冻结进程
6. **多 ABI Fat APK**：分别编译 `android-arm` 与 `android-arm64` 后合并 `lib/` 目录，兼容 32 位与 64 位设备

---

## 📄 许可证

MIT License
