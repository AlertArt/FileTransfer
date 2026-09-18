namespace FileTransferApp.Core.Services.Interfaces;

/// <summary>
/// 平台无关的应用数据持久化键值存储。
/// 平台实现负责映射到应用私有目录；测试实现可内存化。
/// 存储对象为 JSON 编码字符串（JSON 文件以 key 命名）。
/// </summary>
public interface ISettingsStore
{
    /// <summary>读取指定 key 的原始字符串（JSON）。不存在返回 null。</summary>
    string? ReadRaw(string key);

    /// <summary>写入指定 key 的原始字符串（JSON）。持久化失败时静默（不影响运行）。</summary>
    void WriteRaw(string key, string json);

    /// <summary>删除指定 key。</summary>
    void Remove(string key);
}