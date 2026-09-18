using System.Security.Cryptography;
using FileTransferApp.Core.Security;
using Xunit;

namespace FileTransferApp.Core.Tests;

/// <summary>
/// 加密原语测试：ECDH 密钥派生确定性（双方各用己私钥+对方公钥得到相同共享密钥）、
/// AES-GCM 加密往返、篡改检测、HKDF 派生密钥与独立 nonce 随机性。
/// </summary>
public class DeviceCryptoTests
{
    [Fact]
    public void GenerateKeyPair_ProducesValidPkcs8AndSpki()
    {
        var (pub, priv) = DeviceCrypto.GenerateKeyPair();
        Assert.False(string.IsNullOrEmpty(pub));
        Assert.False(string.IsNullOrEmpty(priv));

        // 验证可被 BCL 正常导入
        using var ecdh = ECDiffieHellman.Create();
        ecdh.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pub), out _);
        ecdh.ImportPkcs8PrivateKey(Convert.FromBase64String(priv), out _);
    }

    [Fact]
    public void Ecdh_BothSides_DeriveSameSharedKey()
    {
        var (pubA, privA) = DeviceCrypto.GenerateKeyPair();
        var (pubB, privB) = DeviceCrypto.GenerateKeyPair();

        // A 计算：A私钥 + B公钥；B 计算：B私钥 + A公钥。结果必须一致（ECDH 核心性质）。
        var sharedA = DeviceCrypto.DeriveSharedKey(privA, pubB);
        var sharedB = DeviceCrypto.DeriveSharedKey(privB, pubA);
        Assert.Equal(Convert.ToHexString(sharedA), Convert.ToHexString(sharedB));
    }

    [Fact]
    public void Hkdf_DerivesKey_DeterministicAndLength32()
    {
        var raw = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var k1 = DeviceCrypto.DeriveAesKey(raw, "A", "B");
        var k2 = DeviceCrypto.DeriveAesKey(raw, "A", "B");
        var kDifferentPeer = DeviceCrypto.DeriveAesKey(raw, "A", "C");

        Assert.Equal(32, k1.Length);
        Assert.Equal(Convert.ToHexString(k1), Convert.ToHexString(k2));
        Assert.NotEqual(Convert.ToHexString(k1), Convert.ToHexString(kDifferentPeer));
    }

    [Fact]
    public void Hkdf_DeviceIdOrder_IsCommutative()
    {
        var raw = new byte[32];
        var kAB = DeviceCrypto.DeriveAesKey(raw, "A", "B");
        var kBA = DeviceCrypto.DeriveAesKey(raw, "B", "A");
        // 双方各自计算时设备 ID 顺序可能相反，但密钥必须一致
        Assert.Equal(Convert.ToHexString(kAB), Convert.ToHexString(kBA));
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrips()
    {
        var (_, privA) = DeviceCrypto.GenerateKeyPair();
        var (pubB, _) = DeviceCrypto.GenerateKeyPair();
        var shared = DeviceCrypto.DeriveSharedKey(privA, pubB);
        var key = DeviceCrypto.DeriveAesKey(shared, "A", "B");

        var plaintext = System.Text.Encoding.UTF8.GetBytes("hello 世界, this is the payload 123");
        var frame = DeviceCrypto.Encrypt(key, plaintext);

        // 帧长度 = nonce(12) + ct(等长) + tag(16)
        Assert.Equal(12 + plaintext.Length + 16, frame.Length);

        var decrypted = DeviceCrypto.Decrypt(key, frame);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var key = new byte[32];
        var plaintext = System.Text.Encoding.UTF8.GetBytes("confidential data");
        var frame = DeviceCrypto.Encrypt(key, plaintext);

        // 篡改密文一个字节 → GCM 鉴权必须失败
        frame[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => DeviceCrypto.Decrypt(key, frame));

        // 篡改 nonce → 同样失败
        var frame2 = DeviceCrypto.Encrypt(key, plaintext);
        frame2[0] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => DeviceCrypto.Decrypt(key, frame2));
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var keyA = new byte[32];
        keyA[0] = 1;
        var keyB = new byte[32];
        keyB[31] = 1;

        var frame = DeviceCrypto.Encrypt(keyA, new byte[] { 1, 2, 3 });
        Assert.ThrowsAny<CryptographicException>(() => DeviceCrypto.Decrypt(keyB, frame));
    }

    [Fact]
    public void Encrypt_UsesUniqueNonce_PerCall()
    {
        var key = new byte[32];
        var ct1 = DeviceCrypto.Encrypt(key, new byte[] { 9, 9, 9 });
        var ct2 = DeviceCrypto.Encrypt(key, new byte[] { 9, 9, 9 });
        // 密文块不同（nonce 不同 → GCM keystream 不同）
        Assert.NotEqual(Convert.ToHexString(ct1), Convert.ToHexString(ct2));
    }

    [Fact]
    public void PublicKeyFingerprint_IsStableAndShort()
    {
        var (pub, _) = DeviceCrypto.GenerateKeyPair();
        var f1 = DeviceCrypto.PublicKeyFingerprint(pub);
        var f2 = DeviceCrypto.PublicKeyFingerprint(pub);
        Assert.Equal(f1, f2);
        Assert.Equal(16, f1.Length);
        Assert.Equal(string.Empty, DeviceCrypto.PublicKeyFingerprint("not-base64!!"));
    }
}