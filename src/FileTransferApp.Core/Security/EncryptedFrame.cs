namespace FileTransferApp.Core.Security;

/// <summary>
/// AES-GCM-256 加密消息帧：nonce(12) || ciphertext || tag(16)。
/// 明文与密文等长（GCM 不改变长度，tag 追加在尾部），因此 Content-Length 语义与 v1 一致。
/// </summary>
public static class EncryptedFrame
{
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int Overhead = NonceLength + TagLength;

    public static byte[] Build(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextWithTag)
    {
        var frame = new byte[nonce.Length + ciphertextWithTag.Length];
        nonce.CopyTo(frame);
        ciphertextWithTag.CopyTo(frame.AsSpan(nonce.Length));
        return frame;
    }

    public static bool TryParse(ReadOnlySpan<byte> frame, out byte[] nonce, out byte[] ciphertextWithTag)
    {
        nonce = Array.Empty<byte>();
        ciphertextWithTag = Array.Empty<byte>();
        if (frame.Length < Overhead) return false;
        nonce = frame[..NonceLength].ToArray();
        ciphertextWithTag = frame[NonceLength..].ToArray();
        return true;
    }
}