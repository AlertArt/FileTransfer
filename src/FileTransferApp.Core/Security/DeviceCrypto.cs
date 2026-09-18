using System.Security.Cryptography;
using FileTransferApp.Core.Protocols;

namespace FileTransferApp.Core.Security;

/// <summary>
/// 设备加密原语：ECDH(P-256) 密钥协商 + HKDF-SHA256 派生 + AES-GCM-256 认证加密。
/// 纯 BCL 实现（System.Security.Cryptography），无需额外包。
///
/// 密钥协商机制：
///   1. 双方各自在安装期生成长期 ECDH P-256 密钥对（见 DeviceIdentity）。
///   2. 配对时交换公钥（SubjectPublicKeyInfo base64，明文公钥无密级）。
///   3. sharedRaw = ECDH(selfPrivate, peerPublic)——双方计算得到相同原始共享秘密。
///   4. key = HKDF-SHA256(ikm: sharedRaw, salt: 设备类型聚合, info: "FileTransferApp/pairing/v2")
///      得到 32 字节 AES-256 密钥。HKDF 防止 ECDH 原始输出直接用于对称加密。
///   5. 该密钥持久化为 PairRecord.SharedKeyBase64，双方一致，后续 /prepare /chunk /control 全部 AES-GCM 加密。
/// </summary>
public static class DeviceCrypto
{
    /// <summary>生成安装期长期 ECDH P-256 密钥对（私钥仅本机保存，公钥用于配对交换）。</summary>
    public static (string PublicKeyBase64, string PrivateKeyBase64) GenerateKeyPair()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(ecdh.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(ecdh.ExportPkcs8PrivateKey()));
    }

    /// <summary>PUBLIC key hash fingerprint (hex)。用于日志/UI 校验公钥是否一致，具备防错配能力。</summary>
    public static string PublicKeyFingerprint(string publicKeyBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(publicKeyBase64);
            return Convert.ToHexString(SHA256.HashData(bytes))[..16];
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 计算与对端的共享原始秘密：ECDH(selfPrivate, peerPublic)。
    /// 返回预先导出格式（Pkcs8 导出），随后交由 HKDF 派生。产出跨端一致的 32 字节密钥。
    /// </summary>
    public static byte[] DeriveSharedKey(byte[] selfPrivatePkcs8, byte[] peerPublicSpki)
    {
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(selfPrivatePkcs8, out _);
        var peerKey = ECDiffieHellman.Create();
        try
        {
            peerKey.ImportSubjectPublicKeyInfo(peerPublicSpki, out _);
            return ecdh.DeriveKeyMaterial(peerKey.PublicKey);
        }
        finally
        {
            peerKey.Dispose();
        }
    }

    public static byte[] DeriveSharedKey(string selfPrivateKeyBase64, string peerPublicKeyBase64)
        => DeriveSharedKey(
            Convert.FromBase64String(selfPrivateKeyBase64),
            Convert.FromBase64String(peerPublicKeyBase64));

    /// <summary>
    /// HKDF-SHA256 派生最终 AES-256 密钥。
    /// salt 使用双方设备 ID 词典序拼接，固化"哪两端"的语义，防止跨设备密钥混淆；
    /// info 固定协议标识。输出固定 32 字节。
    /// </summary>
    public static byte[] DeriveAesKey(byte[] sharedRaw, string selfDeviceId, string peerDeviceId)
    {
        var salt = string.CompareOrdinal(selfDeviceId, peerDeviceId) <= 0
            ? $"{selfDeviceId}|{peerDeviceId}"
            : $"{peerDeviceId}|{selfDeviceId}";
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: sharedRaw,
            outputLength: ProtocolConstants.KeySizeBytes,
            salt: System.Text.Encoding.UTF8.GetBytes(salt),
            info: System.Text.Encoding.UTF8.GetBytes("FileTransferApp/pairing/v2"));
    }

    /// <summary>将共享密钥编码为配对记录可持久化的 base64 字符串。</summary>
    public static string EncodeSharedKey(byte[] key) => Convert.ToBase64String(key);
    public static byte[] DecodeSharedKey(string keyBase64) => Convert.FromBase64String(keyBase64);

    /// <summary>
    /// AES-GCM-256 加密：返回 nonce(12)||ciphertext||tag(16) 帧。
    /// GCM 是 AEAD，同时提供机密性与完整性；每帧使用独立随机 nonce，禁止复用。
    /// </summary>
    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(EncryptedFrame.NonceLength);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[EncryptedFrame.TagLength];
        using (var aes = new AesGcm(key, EncryptedFrame.TagLength))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        var frame = new byte[EncryptedFrame.Overhead + plaintext.Length];
        nonce.CopyTo(frame, 0);
        ciphertext.CopyTo(frame, EncryptedFrame.NonceLength);
        tag.CopyTo(frame, EncryptedFrame.NonceLength + ciphertext.Length);
        return frame;
    }

    /// <summary>解密整帧（含鉴权）。校验失败抛 CryptographicException。</summary>
    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> frame)
    {
        if (frame.Length < EncryptedFrame.Overhead)
            throw new CryptographicException("加密帧长度不足");
        var nonce = frame[..EncryptedFrame.NonceLength];
        var ciphertextEnd = frame.Length - EncryptedFrame.TagLength;
        var ciphertext = frame[EncryptedFrame.NonceLength..ciphertextEnd];
        var tag = frame[ciphertextEnd..];

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, EncryptedFrame.TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}