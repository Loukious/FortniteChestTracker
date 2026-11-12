using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Unreal.Encryption;

/// <summary>
/// Managed AES-ECB decryption for replay payloads on platforms without native AES-ECB,
/// including browsers. Implements the inverse cipher and key expansion from FIPS 197.
/// </summary>
public static class ReplayAesEcb
{
    private const int BlockSize = 16;
    private static readonly byte[] SBox = new byte[256];
    private static readonly byte[] InverseSBox = new byte[256];
    private static readonly byte[] Times9 = new byte[256];
    private static readonly byte[] Times11 = new byte[256];
    private static readonly byte[] Times13 = new byte[256];
    private static readonly byte[] Times14 = new byte[256];
    private static readonly uint[] Td0 = new uint[256];
    private static readonly uint[] Td1 = new uint[256];
    private static readonly uint[] Td2 = new uint[256];
    private static readonly uint[] Td3 = new uint[256];

    static ReplayAesEcb()
    {
        for (var value = 0; value < 256; value++)
        {
            // Multiplicative inverse in GF(2^8), followed by the AES affine transform.
            var inverse = value == 0 ? (byte)0 : Power((byte)value, 254);
            var substituted = (byte)(inverse ^ Rotate(inverse, 1) ^ Rotate(inverse, 2)
                ^ Rotate(inverse, 3) ^ Rotate(inverse, 4) ^ 0x63);
            SBox[value] = substituted;
            InverseSBox[substituted] = (byte)value;
            Times9[value] = Multiply((byte)value, 9);
            Times11[value] = Multiply((byte)value, 11);
            Times13[value] = Multiply((byte)value, 13);
            Times14[value] = Multiply((byte)value, 14);
        }
        // Combine inverse substitution and column mixing into word lookups.
        // Rotated tables supply the other three bytes of each column.
        for (var value = 0; value < 256; value++)
        {
            var substituted = InverseSBox[value];
            var table = (uint)Times14[substituted] << 24 | (uint)Times9[substituted] << 16
                | (uint)Times13[substituted] << 8 | Times11[substituted];
            Td0[value] = table;
            Td1[value] = table >> 8 | table << 24;
            Td2[value] = table >> 16 | table << 16;
            Td3[value] = table >> 24 | table << 8;
        }
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> key,
        PaddingMode padding = PaddingMode.PKCS7)
    {
        if (key.Length is not (16 or 24 or 32))
            throw new CryptographicException("AES keys must contain 16, 24, or 32 bytes.");
        if (ciphertext.Length % BlockSize != 0 || padding == PaddingMode.PKCS7 && ciphertext.IsEmpty)
            throw new CryptographicException("The encrypted replay payload must contain complete AES blocks.");
        if (padding is not (PaddingMode.None or PaddingMode.PKCS7))
            throw new ArgumentOutOfRangeException(nameof(padding), "Only no padding and PKCS7 are supported.");

        var rounds = key.Length / 4 + 6;
        Span<byte> roundKeys = stackalloc byte[BlockSize * (rounds + 1)];
        ExpandKey(key, roundKeys);
        Span<uint> decryptionKeys = stackalloc uint[4 * (rounds + 1)];
        // The equivalent inverse cipher mixes the middle round keys once,
        // rather than mixing key bytes again for every encrypted block.
        for (var round = 0; round <= rounds; round++)
        {
            var roundKey = roundKeys.Slice(round * BlockSize, BlockSize);
            if (round > 0 && round < rounds) InverseMixColumns(roundKey);
            for (var column = 0; column < 4; column++)
                decryptionKeys[round * 4 + column] = BinaryPrimitives.ReadUInt32BigEndian(roundKey.Slice(column * 4, 4));
        }
        var plaintext = new byte[ciphertext.Length];
        try
        {
            for (var offset = 0; offset < ciphertext.Length; offset += BlockSize)
            {
                var input = ciphertext.Slice(offset, BlockSize);
                var keyIndex = rounds * 4;
                var s0 = BinaryPrimitives.ReadUInt32BigEndian(input) ^ decryptionKeys[keyIndex];
                var s1 = BinaryPrimitives.ReadUInt32BigEndian(input[4..]) ^ decryptionKeys[keyIndex + 1];
                var s2 = BinaryPrimitives.ReadUInt32BigEndian(input[8..]) ^ decryptionKeys[keyIndex + 2];
                var s3 = BinaryPrimitives.ReadUInt32BigEndian(input[12..]) ^ decryptionKeys[keyIndex + 3];
                for (var round = rounds - 1; round > 0; round--)
                {
                    keyIndex -= 4;
                    var t0 = Td0[s0 >> 24] ^ Td1[(s3 >> 16) & 255] ^ Td2[(s2 >> 8) & 255] ^ Td3[s1 & 255] ^ decryptionKeys[keyIndex];
                    var t1 = Td0[s1 >> 24] ^ Td1[(s0 >> 16) & 255] ^ Td2[(s3 >> 8) & 255] ^ Td3[s2 & 255] ^ decryptionKeys[keyIndex + 1];
                    var t2 = Td0[s2 >> 24] ^ Td1[(s1 >> 16) & 255] ^ Td2[(s0 >> 8) & 255] ^ Td3[s3 & 255] ^ decryptionKeys[keyIndex + 2];
                    var t3 = Td0[s3 >> 24] ^ Td1[(s2 >> 16) & 255] ^ Td2[(s1 >> 8) & 255] ^ Td3[s0 & 255] ^ decryptionKeys[keyIndex + 3];
                    s0 = t0; s1 = t1; s2 = t2; s3 = t3;
                }
                var output = plaintext.AsSpan(offset, BlockSize);
                BinaryPrimitives.WriteUInt32BigEndian(output,
                    FinalColumn(s0, s3, s2, s1) ^ decryptionKeys[0]);
                BinaryPrimitives.WriteUInt32BigEndian(output[4..],
                    FinalColumn(s1, s0, s3, s2) ^ decryptionKeys[1]);
                BinaryPrimitives.WriteUInt32BigEndian(output[8..],
                    FinalColumn(s2, s1, s0, s3) ^ decryptionKeys[2]);
                BinaryPrimitives.WriteUInt32BigEndian(output[12..],
                    FinalColumn(s3, s2, s1, s0) ^ decryptionKeys[3]);
            }

            if (padding == PaddingMode.None) return plaintext;
            var paddingLength = plaintext[^1];
            if (paddingLength is < 1 or > BlockSize)
                throw new CryptographicException("Invalid PKCS7 padding in the encrypted replay payload.");
            var mismatch = 0;
            for (var i = plaintext.Length - paddingLength; i < plaintext.Length; i++)
                mismatch |= plaintext[i] ^ paddingLength;
            if (mismatch != 0)
                throw new CryptographicException("Invalid PKCS7 padding in the encrypted replay payload.");
            var unpadded = plaintext.AsSpan(0, plaintext.Length - paddingLength).ToArray();
            CryptographicOperations.ZeroMemory(plaintext);
            return unpadded;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(roundKeys);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(decryptionKeys));
        }
    }

    private static uint FinalColumn(uint a, uint b, uint c, uint d) =>
        (uint)InverseSBox[a >> 24] << 24 | (uint)InverseSBox[(b >> 16) & 255] << 16
        | (uint)InverseSBox[(c >> 8) & 255] << 8 | InverseSBox[d & 255];

    private static void ExpandKey(ReadOnlySpan<byte> key, Span<byte> expanded)
    {
        key.CopyTo(expanded);
        var keyWords = key.Length / 4;
        var roundConstant = (byte)1;
        Span<byte> word = stackalloc byte[4];
        for (var index = keyWords; index < expanded.Length / 4; index++)
        {
            expanded.Slice((index - 1) * 4, 4).CopyTo(word);
            if (index % keyWords == 0)
            {
                var first = word[0];
                word[0] = (byte)(SBox[word[1]] ^ roundConstant);
                word[1] = SBox[word[2]];
                word[2] = SBox[word[3]];
                word[3] = SBox[first];
                roundConstant = Multiply(roundConstant, 2);
            }
            else if (keyWords > 6 && index % keyWords == 4)
            {
                for (var i = 0; i < 4; i++) word[i] = SBox[word[i]];
            }
            for (var i = 0; i < 4; i++)
                expanded[index * 4 + i] = (byte)(expanded[(index - keyWords) * 4 + i] ^ word[i]);
        }
        CryptographicOperations.ZeroMemory(word);
    }

    private static void InverseMixColumns(Span<byte> state)
    {
        for (var column = 0; column < BlockSize; column += 4)
        {
            var a = state[column];
            var b = state[column + 1];
            var c = state[column + 2];
            var d = state[column + 3];
            state[column] = (byte)(Times14[a] ^ Times11[b] ^ Times13[c] ^ Times9[d]);
            state[column + 1] = (byte)(Times9[a] ^ Times14[b] ^ Times11[c] ^ Times13[d]);
            state[column + 2] = (byte)(Times13[a] ^ Times9[b] ^ Times14[c] ^ Times11[d]);
            state[column + 3] = (byte)(Times11[a] ^ Times13[b] ^ Times9[c] ^ Times14[d]);
        }
    }

    private static byte Rotate(byte value, int bits) => (byte)(value << bits | value >> (8 - bits));

    private static byte Power(byte value, int exponent)
    {
        byte result = 1;
        while (exponent > 0)
        {
            if ((exponent & 1) != 0) result = Multiply(result, value);
            value = Multiply(value, value);
            exponent >>= 1;
        }
        return result;
    }

    private static byte Multiply(byte a, byte b)
    {
        var result = 0;
        while (b != 0)
        {
            if ((b & 1) != 0) result ^= a;
            a = (byte)(a << 1 ^ ((a & 0x80) != 0 ? 0x1b : 0));
            b >>= 1;
        }
        return (byte)result;
    }
}
