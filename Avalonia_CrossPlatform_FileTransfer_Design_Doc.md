# 基于 Avalonia 的跨平台文件传输软件架构与设计规范

> **支持平台**：Windows / iOS / Android | **核心特性**：断点续传 | 设备发现 | 高性能 Pipelines | MVVM 规范  
> **技术栈**：Avalonia UI 11.x | .NET 10 | CommunityToolkit.Mvvm | System.IO.Pipelines  
> **适用对象**：软件架构师 / 高级 C# 工程师 / AI Agent 自动化开发系统  
> **版本**：v1.0.0 (2026-08) | **状态**：正式发布规范  

---

## 一、业务需求与功能规格 (PRD)

本软件旨在构建一个安全、高速、无服务器依赖的局域网跨平台文件传输工具，支持 Windows 桌面端以及 iOS/Android 移动端。核心需求涵盖设备发现、断点续传、分块传输控制与文件预览。

### 1.1 核心功能矩阵说明

| 模块名称 | 功能项 | 详细规格要求 |
| :--- | :--- | :--- |
| **设备发现** | UDP 广播与心跳维持 | 在局域网内通过 UDP 多播（端口 53317）广播设备节点信息（设备 ID、名称、类型、端口）。节点收到广播后更新本地设备列表，若 10s 内未收到心跳则自动标记为离线。 |
| | 手动直连与 IP 扫描 | 适应跨子网或 UDP 广播受限环境，支持用户直接输入目标 IP:Port 发起握手与设备绑定。 |
| **文件传输 Engine** | 单/多文件批量传输 | 支持批量拖拽与文件流并发调度。传输前先发起 HTTP Metadata 握手，交换文件名称、总大小、SHA256 Hash、切片数与缩略图。 |
| **传输控制** | 断连容错与断点续传 | 当网络闪断或 Wi-Fi 切换时，自动保存传输状态（Chunk Index / Offset）。重新建立连接后，接收端回传已接收的 Chunk Bitmap，实现无损断点续传。 |
| | 暂停与恢复 (Pause/Resume) | 发送方与接收方均可随时暂停任务。暂停时优雅关闭 Socket 数据流；恢复时向对方发起 Resume 信令并拉取/推送剩余切片。 |
| | 取消与清理 (Cancel/Cleanup) | 取消传输后即刻终止连接，接收端安全关闭文件句柄并自动删除临时文件 (`.tmp`)，防止垃圾残留。 |
| **UI 交互与状态** | 进度显示与平滑速率算力 | 基于 1 秒滑动窗口算法计算实时网速 (MB/s)，结合传输百分比实时渲染 ETA（预计剩余时间）与进度条。 |
| | 缩略图提取与传输 | 发送端使用 SkiaSharp/原生 API 提取图片与视频首帧，压缩为 128x128 WEBP/JPEG，在握手包中直接打包 Base64 传递，实现接收前预览。 |

### 1.2 传输任务有限状态机 (FSM) 架构

为确保多线程并发与网络中断时状态的一致性，每个传输任务严格遵循以下状态机流转：

`[Created]` -> `[Preparing (握手中)]` -> `[WaitingApproval (等待对方同意)]` -> `[Transferring (传输中)]` <-> `[Paused (已暂停)]`  
|  
v  
`[Disconnected (断开异常)]` -> `[Completed (完成)]` / `[Failed (失败)]` / `[Cancelled (已取消)]`

---

## 二、方案选型与架构分析 (Avalonia vs Flutter)

在跨平台技术选型中，Avalonia UI 方案与 Flutter + Rust 方案是当前最主流的高性能解法。

| 维度 | Avalonia 全栈 C# 方案 | Flutter + Rust / Dart 方案 |
| :--- | :--- | :--- |
| **语言与技术栈** | 全栈 C# / .NET 8/9/10，统一语言体系，无需 FFI / Platform Channels 桥接。 | Dart (UI) + Rust (传输)，双语言栈，需编写复杂的 FFI 绑定与跨语言内存管理。 |
| **文件/网络 I/O 性能** | 极高，原生 `System.IO.Pipelines` + `Span<T>` 零拷贝内存池。 | 极高，Rust Tokio 异步 Reactor 网络库，极致吞吐。 |
| **桌面端体验 (Windows)** | 顶级，原生 XAML 基因，原生拖拽、多窗口适配与 Shell 交互完美支持。 | 优秀，桌面端生态成熟度递增，但窗口控制与部分深度 Shell 需依赖插件。 |
| **移动端生态 (iOS/Android)** | 良好，Avalonia 11+ 全面支持，但原生权限/后台服务需编写原生绑定。 | 顶级，移动端插件极度丰富，后台保活与系统权限封装开箱即用。 |

**选型结论**：对于具备 C#/WPF 背景的团队，Avalonia 方案能极大降低开发难度，利用 C# 优秀的 Pipelines 异步高性能 Socket 操作，既保障了 Windows 端的极致体验，又实现了移动端的跨平台复用。

---

## 三、系统架构与工程目录结构 (Architecture & Design)

### 3.1 分层架构模型

系统采用严格的依赖单向传递原则：  
`UI View Layer` -> `ViewModel Layer` -> `Core Business & Protocols` -> `Platform Native Implementations`

### 3.2 规范化目录结构

```
FileTransferApp/
├── src/
│   ├── FileTransferApp.Core/                    # 纯 C# 核心业务与网络协议层 (无 UI 依赖)
│   │   ├── Models/                              # TaskInfo, DeviceNode, ChunkMetadata, TransferredChunk
│   │   ├── Protocols/                           # UDP 发现协议, HTTP/TCP 分块传输协议
│   │   ├── Services/                            # 核心服务 (Network, Discovery, PipelinesEngine, Storage)
│   │   │   ├── Interfaces/                      # ITransferEngine, IDiscoveryService, IStorageService
│   │   │   └── Impl/                            # PipelinesTransferEngine, UdpDiscoveryService
│   │   └── Messaging/                           # WeakReferenceMessenger 弱引用消息定义
│   ├── FileTransferApp.UI/                      # Avalonia 视图与 ViewModels (跨平台复用)
│   │   ├── ViewModels/                          # MainViewModel, DeviceListViewModel, TransferItemViewModel
│   │   ├── Views/                               # MainWindow.axaml, DeviceListView.axaml, TransferListView.axaml
│   │   ├── Converters/                          # BytesToSpeedConverter, StatusToColorConverter
│   │   └── App.axaml / App.axaml.cs             # DI 容器初始化 (Microsoft.Extensions.DependencyInjection)
│   └── FileTransferApp.Platforms/               # 平台特定适配层
│       ├── FileTransferApp.Desktop/             # Windows 入口，自动配置防火墙规则
│       ├── FileTransferApp.Android/             # Android 入口，前台服务 (Foreground Service) & SAF 接入
│       └── FileTransferApp.iOS/                 # iOS 入口，局域网权限 (NSLocalNetworkUsage) & 后台任务
└── tests/
    └── FileTransferApp.Core.Tests/              # 切片校验，pipelines 低内存，状态机单元测试
```

---

## 四、通信协议与数据传输设计 (Protocol Specification)

### 4.1 UDP 设备发现协议 (Port 53317)

所有节点开启监听 UDP 53317 端口。节点启动后每隔 3 秒向 `239.255.255.250` 发送心跳 JSON：

```json
{
  "deviceId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "deviceName": "Developer-Windows-PC",
  "deviceType": "Windows",
  "port": 53318,
  "protocolVersion": 1
}
```

### 4.2 HTTP/TCP 分块传输 Restful 接口设计 (Port 53318)

| 接口路径 | 方法 | 请求 Body / Header 规范 | 响应规范与含义 |
| :--- | :--- | :--- | :--- |
| `/api/v1/transfer/prepare` | `POST` | **JSON**: 包含 `fileId`, `fileName`, `fileSize`, `chunkSize` (预设 2MB), `sha256`, `thumbnailBase64` | `{ "accepted": true, "receivedChunks": [0,1,2] }`<br>接收端同意并回传已存在的切片列表以进行续传。 |
| `/api/v1/transfer/chunk` | `POST` | **Headers**: `X-File-Id`, `X-Chunk-Index`, `X-Chunk-Hash`<br>**Body**: 原始二进制字节流 (Binary) | **HTTP 200 OK**: `{ "chunkIndex": 3, "status": "Success" }` |
| `/api/v1/transfer/control` | `POST` | **JSON**: `{ "fileId": "xxx", "action": "PAUSE" }`<br>**Action**: `PAUSE` \| `RESUME` \| `CANCEL` | **HTTP 200 OK**: `{ "status": "Acknowledged" }` |

---

## 五、核心代码契约与接口定义 (Code Contracts)

### 5.1 平台抽象服务接口 (`Core/Services/Interfaces/`)

```csharp
namespace FileTransferApp.Core.Services.Interfaces;

// 缩略图生成抽象接口
public interface IThumbnailService
{
    Task<byte[]?> GenerateThumbnailAsync(string filePath, int maxWidth = 128, int maxHeight = 128);
}

// 移动端前台保活服务接口
public interface IPlatformKeepAliveService
{
    void StartKeepAlive(string title, string content);
    void StopKeepAlive();
}

// 文件存储与选择器抽象
public interface IStorageService
{
    Task<Stream> OpenReadStreamAsync(string fileIdentifier);
    Task<Stream> OpenWriteStreamAsync(string fileName, long totalSize);
}
```

### 5.2 事件总线弱引用消息 (`Core/Messaging/Messages.cs`)

```csharp
using CommunityToolkit.Mvvm.Messaging.Messages;

// 设备发现/离线事件
public record DeviceDiscoveredMessage(DeviceNode Device);
public record DeviceLostMessage(string DeviceId);

// 传输状态更新事件
public record TransferStatusChangedMessage(string FileId, TransferState NewState);

// 实时网速与进度更新事件
public record TransferProgressMessage(
    string FileId,
    long BytesTransferred,
    long TotalBytes,
    double SpeedBytesPerSecond
);
```

### 5.3 ViewModel 核心逻辑实现 (`UI/ViewModels/TransferItemViewModel.cs`)

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media.Imaging;

public partial class TransferItemViewModel : ObservableObject
{
    private readonly ITransferEngine _transferEngine;

    [ObservableProperty] private string _fileId = string.Empty;
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _bytesTransferred;
    [ObservableProperty] private double _progressPercentage;
    [ObservableProperty] private string _speedText = "0 KB/s";
    [ObservableProperty] private TransferState _state;
    [ObservableProperty] private Bitmap? _thumbnailImage;

    public TransferItemViewModel(ITransferEngine transferEngine)
    {
        _transferEngine = transferEngine;
    }

    [RelayCommand]
    private async Task PauseAsync() => await _transferEngine.PauseTransferAsync(FileId);

    [RelayCommand]
    private async Task ResumeAsync() => await _transferEngine.ResumeTransferAsync(FileId);

    [RelayCommand]
    private async Task CancelAsync() => await _transferEngine.CancelTransferAsync(FileId);
}
```

### 5.4 高性能 `System.IO.Pipelines` 读写内核模型

```csharp
using System.IO.Pipelines;

public async Task StreamChunkToDiskAsync(Stream networkStream, Stream fileStream, CancellationToken ct)
{
    var pipe = new Pipe();
    Task writing = FillPipeAsync(networkStream, pipe.Writer, ct);
    Task reading = ReadPipeAsync(fileStream, pipe.Reader, ct);
    await Task.WhenAll(writing, reading);
}

private async Task FillPipeAsync(Stream source, PipeWriter writer, CancellationToken ct)
{
    const int minimumBufferSize = 64 * 1024; // 64KB Buffer
    while (!ct.IsCancellationRequested)
    {
        Memory<byte> memory = writer.GetMemory(minimumBufferSize);
        int bytesRead = await source.ReadAsync(memory, ct);
        if (bytesRead == 0) break;

        writer.Advance(bytesRead);
        FlushResult result = await writer.FlushAsync(ct);
        if (result.IsCompleted) break;
    }
    await writer.CompleteAsync();
}
```

---

## 六、三端 (Windows / iOS / Android) 原生落地避坑指南

### 1. Windows 平台防拦截与配置
- **防火墙入站规则**：应用首次运行时必须注册入站规则。可在打包（如 Inno Setup / MSIX）中执行 Shell 命令：
  ```bash
  netsh advfirewall firewall add rule name="FileTransferApp" dir=in action=allow protocol=TCP localport=53318
  ```
- **文件拖放支持**：Avalonia Window 上需配置 `DragDrop.AllowDrop="True"`，并监听 `DragDrop.DropEvent` 路由事件。

### 2. Android 平台存储与保活
- **前台服务 (Foreground Service)**：在 Android 8.0+ 必须启动绑定 Notifications 的 Foreground Service，并在 `AndroidManifest.xml` 申请权限：
  ```xml
  <uses-permission android:name="android.permission.FOREGROUND_SERVICE" />
  ```
- **Storage Access Framework (SAF)**：针对 Android 10+ 高版本，文件写入必须调用 DocumentsContract 或 SAF 接口，避免原生 `System.IO.File` 访问被拒绝。

### 3. iOS 平台网络权限与后台限制
- **局域网权限配置**：iOS 14+ 必须在 `Info.plist` 注入声明，否则网络请求将被系统静默阻断：
  ```xml
  <key>NSLocalNetworkUsageDescription</key>
  <string>需要访问局域网以发现附近设备并传输文件</string>
  ```
- **后台传输机制**：应用退至后台 30s 内会被冻结。超大传输必须调用原生 `BGTaskScheduler` 请求后台处理时间，或提示用户置顶前台。

---

## 七、测试验证与质量保障计划 (Testing & QA)

### 7.1 单元测试策略 (Unit Testing)
- **分块 Hash 校验测试**：验证大文件经过 Pipelines 切片再合并后，MD5/SHA256 与原始文件 100% 一致。
- **断点 Resume Bitmap 测试**：模拟接收端缺失 `[1, 3, 5]` 块，验证发送端重新发包时仅推送指定缺失索引。

### 7.2 三端兼容性测试 Checklist

| 测试环境 | 测试用例描述 | 预期通过标准 |
| :--- | :--- | :--- |
| **Win <-> Android** | 传输 5GB 4K 视频文件，并在 50% 时主动关闭 Android Wi-Fi，5 秒后重连。 | 界面状态转换为 `Disconnected`，重连后点击 `Resume` 自动继续，合并文件 Hash 完全一致。 |
| **Win <-> iOS** | Win 发送 100 张高清照片至 iOS 端。 | iOS 弹出局域网授权提示，授权后瞬间显示图片缩略图，同步接收无卡顿。 |
| **低内存极限测试** | 在 2GB 内存 Android 设备上连续传输 10GB 镜像文件。 | 内存峰值稳定低于 90MB，未触发 OOM (OutOfMemory) 异常。 |

---

## 八、AI Agent 自动化开发实施指南 (Step-by-Step Skill Prompts)

为便于 AI Agent 逐步生成完整工程，请严格按以下步骤依次执行 Prompt：

### Phase 1: 核心工程搭建与 DI 注入
1. 创建解决方案与三层项目：`FileTransferApp.Core`, `FileTransferApp.UI`, `FileTransferApp.Desktop/Android/iOS`。
2. 在 Core 中创建 `DeviceNode`, `FileMetadata`, `TransferTaskInfo` 实体与 `TransferState` 枚举。
3. 在 `UI/App.axaml.cs` 中配置 `Microsoft.Extensions.DependencyInjection` 容器，注册全套单例与瞬态服务。

### Phase 2: 发现协议与高性能传输内核开发
1. 实现 `UdpDiscoveryService`：基于 `UdpClient` 建立 53317 端口组播监听与 3s 心跳逻辑。
2. 实现 `PipelinesTransferEngine`：利用 `System.IO.Pipelines` 编写零拷贝切片读取与写入器。
3. 实现 HTTP RESTful 服务：响应 Prepare 握手、Chunk 接收与 Control 控制指令。

### Phase 3: 跨平台原生抽象与缩略图引擎
1. 在 Core 中集成 SkiaSharp 实现跨平台图片/视频帧缩略图提取器。
2. 在 Android 中实现 `PlatformKeepAliveService` (绑定 Foreground Service 通知)。
3. 在 iOS/Android 项目中补充对应的权限清单文件与 SAF 存储绑定。

### Phase 4: MVVM 界面与 Reactive 事件绑定
1. 使用 `CommunityToolkit.Mvvm` 构建 `MainViewModel`, `DeviceListViewModel`, `TransferItemViewModel`。
2. 使用 `WeakReferenceMessenger` 建立 Core 网络服务到 UI ViewModel 的解耦消息广播。
3. 编写 Avalonia XAML 界面：实现设备列表、文件拖拽区、进度条以及缩略图 Template 渲染。

### Phase 5: 单元测试与质量验证
1. 编写 `Core.Tests` 项目，添加 `ChunkEngine` 拼接完整性与 Hash 校验测试。
2. 模拟网络丢包/超时，验证 FSM 状态转换与断点续传 Bitmap 匹配算法。
