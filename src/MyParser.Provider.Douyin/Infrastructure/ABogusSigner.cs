using System.Text;

namespace MyParser.Provider.Douyin.Infrastructure;

public static class ABogusSigner
{
    private const string Salt = "dhzx";
    private const string SdkVersion = "1.0.1.19-fix.01";
    private const string ResultAlphabet = "Dkdpgh2ZmsQB80/MfvV36XI1R45-WUAlEixNLwoqYTOPuzKFjJnry79HbGcaStCe";
    private const long ReferenceEpochMilliseconds = 1721836800000;
    private const int Aid = 6383;

    private static readonly byte[] BrowserFingerprint = Encoding.ASCII.GetBytes(DouyinConstants.BrowserFingerprint);

    private static int _counter = 2;

    public static string Sign(string queryString, string userAgent, string body = "")
    {
        ArgumentNullException.ThrowIfNull(queryString);
        ArgumentNullException.ThrowIfNull(userAgent);
        ArgumentNullException.ThrowIfNull(body);

        var signTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ink = signTime - 1;
        var queryHash = DoubleSm3(queryString + Salt);
        var bodyHash = DoubleSm3(body + Salt);
        var encryptedUa = DyRc4([0, 129, 14], ToJsBytes(userAgent.Trim()));
        var encodedUa = Encode(encryptedUa, UaAlphabet);
        var uaHash = Sm3.Hash(Encoding.UTF8.GetBytes(encodedUa));
        var version = MakeVersionArray(SdkVersion);
        var flags = GetEnvironmentFlags(userAgent);
        var counterBucket = GetCounterBucket(Interlocked.Increment(ref _counter));

        var firstRandom = (int)(Random.Shared.NextDouble() * 65535);
        var randomBytes = Blend(version[0], version[1], firstRandom & 0xff, (firstRandom >> 8) & 0xff)
            .Concat(BuildPermissionBytes(version[2], version[3], flags))
            .ToArray();

        var period = (int)((signTime - ReferenceEpochMilliseconds) / (14L * 24 * 60 * 60 * 1000));
        const byte timeDelta = 3;
        var timeBytes = Encoding.ASCII.GetBytes($"{(signTime + 3) & 0xff},");
        var queryDigestByte = EscapeDigest(queryHash, 3, 11, 12, (flags & 2) != 0);
        var bodyDigestByte = EscapeDigest(bodyHash, 4, 8, 9, (flags & 4) != 0);
        var uaDigestByte = EscapeDigest(uaHash, 5, 12, 13, (flags & 8) != 0);

        var payloadChecksum = Xor(
            randomBytes[0], randomBytes[1], randomBytes[2], randomBytes[3],
            randomBytes[4], randomBytes[5], randomBytes[6], randomBytes[7],
            41,
            period,
            counterBucket,
            timeDelta,
            GetByte(signTime, 0), GetByte(signTime, 1), GetByte(signTime, 2),
            GetByte(signTime, 3), GetByte(signTime, 4), GetByte(signTime, 5),
            129, 0,
            flags & 0xff, (flags >> 8) & 0xff, 0, 0, 0, 0,
            14, 0, 0, 0,
            queryHash[9], queryHash[18], queryDigestByte,
            bodyHash[10], bodyHash[19], bodyDigestByte,
            uaHash[11], uaHash[21], uaDigestByte,
            GetByte(ink, 0), GetByte(ink, 1), GetByte(ink, 2),
            GetByte(ink, 3), GetByte(ink, 4), GetByte(ink, 5),
            3,
            GetByte(Aid, 0), GetByte(Aid, 1), GetByte(Aid, 2), GetByte(Aid, 3),
            GetByte(Aid, 0), GetByte(Aid, 1), GetByte(Aid, 2), GetByte(Aid, 3),
            BrowserFingerprint.Length & 0xff, (BrowserFingerprint.Length >> 8) & 0xff,
            timeBytes.Length & 0xff, (timeBytes.Length >> 8) & 0xff);

        var orderedPayload = new List<byte>(50 + BrowserFingerprint.Length + 8)
        {
            GetByte(signTime, 5),
            14,
            uaHash[11],
            GetByte(ink, 1),
            GetByte(Aid, 2),
            GetByte(signTime, 0),
            GetByte(Aid, 3),
            0,
            129,
            queryHash[18],
            (byte)(flags & 0xff),
            3,
            queryDigestByte,
            GetByte(Aid, 1),
            timeDelta,
            queryHash[9],
            GetByte(ink, 4),
            0,
            GetByte(signTime, 1),
            GetByte(Aid, 0),
            (byte)period,
            bodyDigestByte,
            GetByte(signTime, 2),
            GetByte(Aid, 2),
            uaDigestByte,
            0,
            GetByte(ink, 2),
            GetByte(ink, 3),
            counterBucket,
            GetByte(Aid, 1),
            0,
            GetByte(Aid, 3),
            uaHash[21],
            bodyHash[10],
            0,
            (byte)((flags >> 8) & 0xff),
            GetByte(signTime, 4),
            GetByte(Aid, 0),
            bodyHash[19],
            0,
            GetByte(ink, 5),
            0,
            0,
            41,
            GetByte(ink, 0),
            GetByte(signTime, 3),
            (byte)(BrowserFingerprint.Length & 0xff),
            (byte)((BrowserFingerprint.Length >> 8) & 0xff),
            (byte)(timeBytes.Length & 0xff),
            (byte)((timeBytes.Length >> 8) & 0xff),
        };

        orderedPayload.AddRange(BrowserFingerprint);
        orderedPayload.AddRange(timeBytes);
        orderedPayload.Add(payloadChecksum);

        var headerRandom = (int)(Random.Shared.NextDouble() * 65535) & 0xff;
        var randomHeader = Blend(3, 82, headerRandom, GetBrowserRandomOffset(userAgent));
        var transformedPayload = Transform(orderedPayload);
        var encryptedPayload = DyRc4([211], randomBytes.Concat(transformedPayload).ToArray());
        return Encode(randomHeader.Concat(encryptedPayload).ToArray(), ResultAlphabet);
    }

    private static byte[] BuildPermissionBytes(int first, int second, int flags)
    {
        _ = Random.Shared.NextDouble();
        var low = (flags & 64) != 0
            ? (int)(Random.Shared.NextDouble() * 109) + 110
            : (int)(Random.Shared.NextDouble() * 240);
        if ((flags & 64) != 0)
        {
            low += low % 2;
        }
        else if (low > 109)
        {
            low += low % 2;
            low++;
        }

        var high = ((int)(Random.Shared.NextDouble() * 255) & 77) | 2 | 16 | 32 | 128;
        return Blend(first, second, low, high);
    }

    private static byte[] Blend(int first, int second, int randomLow, int randomHigh) =>
        [
            (byte)((randomLow & 170) | (first & 85)),
            (byte)((randomLow & 85) | (first & 170)),
            (byte)((randomHigh & 170) | (second & 85)),
            (byte)((randomHigh & 85) | (second & 170)),
        ];

    private static int GetEnvironmentFlags(string userAgent)
    {
        var flags = 1 | (1 << 1);
        if (GetBrowserName(userAgent) == "Firefox")
        {
            flags |= 1 << 5;
        }

        return flags;
    }

    private static byte GetCounterBucket(int counter)
    {
        if (counter > 10745) return 3;
        if (counter > 1283) return 4;
        if (counter > 139) return 5;
        return 6;
    }

    private static int GetBrowserRandomOffset(string userAgent)
    {
        var offset = GetBrowserName(userAgent) switch
        {
            "Chrome" => 0,
            "Firefox" => 40,
            "Safari" => 81,
            "Edge" => 125,
            "Huawei" => 170,
            _ => 210,
        };
        return (int)(Random.Shared.NextDouble() * 40) + offset;
    }

    private static string GetBrowserName(string userAgent)
    {
        if (userAgent.Contains("huawei", StringComparison.OrdinalIgnoreCase)) return "Huawei";
        if (userAgent.Contains("chrome/", StringComparison.OrdinalIgnoreCase)
            && !userAgent.Contains("chromium", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (userAgent.Contains("edg/", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("edge/", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (userAgent.Contains("firefox/", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("fxios/", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (userAgent.Contains("safari/", StringComparison.OrdinalIgnoreCase)) return "Safari";
        return "Other";
    }

    private static byte EscapeDigest(byte[] digest, int index, byte reserved, byte fallback, bool force)
    {
        var value = index < digest.Length ? digest[index] : fallback;
        while (value == reserved)
        {
            index++;
            value = index < digest.Length ? digest[index] : fallback;
        }

        return force ? reserved : value;
    }

    private static int[] MakeVersionArray(string version)
    {
        return version.Split('.')
            .Select(part => int.TryParse(part, out var value) ? value : 0)
            .ToArray();
    }

    private static byte[] Transform(IReadOnlyList<byte> input)
    {
        var result = new List<byte>((input.Count / 3) * 4 + input.Count % 3);
        for (var i = 0; i < input.Count; i += 3)
        {
            if (i + 2 >= input.Count)
            {
                result.Add(input[i]);
                if (i + 1 < input.Count && input[i + 1] != 0)
                {
                    result.Add(input[i + 1]);
                }

                continue;
            }

            var random = (int)(Random.Shared.NextDouble() * 1000) & 0xff;
            result.Add((byte)((random & 145) | (input[i] & 110)));
            result.Add((byte)((random & 66) | (input[i + 1] & 189)));
            result.Add((byte)((random & 44) | (input[i + 2] & 211)));
            result.Add((byte)((input[i] & 145) | (input[i + 1] & 66) | (input[i + 2] & 44)));
        }

        return result.ToArray();
    }

    private static byte[] DyRc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> state = stackalloc byte[256];
        for (var i = 0; i < state.Length; i++)
        {
            state[i] = (byte)(255 - i);
        }

        var j = 0;
        for (var i = 0; i < state.Length; i++)
        {
            j = (j * state[i] + j + key[i % key.Length]) & 0xff;
            (state[i], state[j]) = (state[j], state[i]);
        }

        var output = new byte[plaintext.Length];
        var x = 0;
        j = 0;
        for (var i = 0; i < plaintext.Length; i++)
        {
            x = (x + 1) & 0xff;
            j = (j + state[x]) & 0xff;
            (state[x], state[j]) = (state[j], state[x]);
            output[i] = (byte)(plaintext[i] ^ state[(state[x] + state[j]) & 0xff]);
        }

        return output;
    }

    private static byte[] DoubleSm3(string value)
    {
        return Sm3.Hash(Sm3.Hash(Encoding.UTF8.GetBytes(value)));
    }

    private static byte[] ToJsBytes(string value)
    {
        var result = new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            result[i] = (byte)value[i];
        }

        return result;
    }

    private static byte GetByte(long value, int index) => (byte)((value >> (index * 8)) & 0xff);

    private static byte Xor(params int[] values)
    {
        var result = 0;
        foreach (var value in values)
        {
            result ^= value;
        }

        return (byte)result;
    }

    private static string Encode(ReadOnlySpan<byte> bytes, string alphabet)
    {
        var output = new StringBuilder(((bytes.Length + 2) / 3) * 4);
        for (var i = 0; i < bytes.Length; i += 3)
        {
            var remaining = bytes.Length - i;
            var combined = bytes[i] << 16;
            if (remaining > 1)
            {
                combined |= bytes[i + 1] << 8;
            }

            if (remaining > 2)
            {
                combined |= bytes[i + 2];
            }

            output.Append(alphabet[(combined >> 18) & 0x3f]);
            output.Append(alphabet[(combined >> 12) & 0x3f]);
            output.Append(remaining > 1 ? alphabet[(combined >> 6) & 0x3f] : '=');
            output.Append(remaining > 2 ? alphabet[combined & 0x3f] : '=');
        }

        return output.ToString();
    }

    private const string UaAlphabet = "ckdp1h4ZKsUB80/Mfvw36XIgR25+WQAlEi7NLboqYTOPuzmFjJnryx9HVGDaStCe";

    private static class Sm3
    {
        private static readonly uint[] Iv =
        [
            0x7380166f, 0x4914b2b9, 0x172442d7, 0xda8a0600,
            0xa96f30bc, 0x163138aa, 0xe38dee4d, 0xb0fb0e4e,
        ];

        public static byte[] Hash(byte[] input)
        {
            var padded = Pad(input);
            var state = Iv.ToArray();
            for (var offset = 0; offset < padded.Length; offset += 64)
            {
                Compress(state, padded.AsSpan(offset, 64));
            }

            var output = new byte[32];
            for (var i = 0; i < state.Length; i++)
            {
                WriteUInt32BigEndian(output.AsSpan(i * 4, 4), state[i]);
            }

            return output;
        }

        private static byte[] Pad(byte[] input)
        {
            var bitLength = (ulong)input.Length * 8;
            var paddingLength = 1 + ((56 - (input.Length + 1) % 64 + 64) % 64) + 8;
            var padded = new byte[input.Length + paddingLength];
            Buffer.BlockCopy(input, 0, padded, 0, input.Length);
            padded[input.Length] = 0x80;
            for (var i = 0; i < 8; i++)
            {
                padded[^(i + 1)] = (byte)(bitLength >> (8 * i));
            }

            return padded;
        }

        private static void Compress(uint[] state, ReadOnlySpan<byte> block)
        {
            Span<uint> words = stackalloc uint[68];
            Span<uint> expanded = stackalloc uint[64];
            for (var i = 0; i < 16; i++)
            {
                words[i] = ReadUInt32BigEndian(block.Slice(i * 4, 4));
            }

            for (var i = 16; i < words.Length; i++)
            {
                words[i] = P1(words[i - 16] ^ words[i - 9] ^ RotateLeft(words[i - 3], 15))
                           ^ RotateLeft(words[i - 13], 7)
                           ^ words[i - 6];
            }

            for (var i = 0; i < expanded.Length; i++)
            {
                expanded[i] = words[i] ^ words[i + 4];
            }

            var a = state[0];
            var b = state[1];
            var c = state[2];
            var d = state[3];
            var e = state[4];
            var f = state[5];
            var g = state[6];
            var h = state[7];

            for (var i = 0; i < 64; i++)
            {
                var constant = i < 16 ? 0x79cc4519u : 0x7a879d8au;
                var ss1 = RotateLeft(unchecked(RotateLeft(a, 12) + e + RotateLeft(constant, i)), 7);
                var ss2 = ss1 ^ RotateLeft(a, 12);
                var tt1 = unchecked(FF(a, b, c, i) + d + ss2 + expanded[i]);
                var tt2 = unchecked(GG(e, f, g, i) + h + ss1 + words[i]);
                d = c;
                c = RotateLeft(b, 9);
                b = a;
                a = tt1;
                h = g;
                g = RotateLeft(f, 19);
                f = e;
                e = P0(tt2);
            }

            state[0] ^= a;
            state[1] ^= b;
            state[2] ^= c;
            state[3] ^= d;
            state[4] ^= e;
            state[5] ^= f;
            state[6] ^= g;
            state[7] ^= h;
        }

        private static uint FF(uint x, uint y, uint z, int index) =>
            index < 16 ? x ^ y ^ z : (x & y) | (x & z) | (y & z);

        private static uint GG(uint x, uint y, uint z, int index) =>
            index < 16 ? x ^ y ^ z : (x & y) | (~x & z);

        private static uint P0(uint value) => value ^ RotateLeft(value, 9) ^ RotateLeft(value, 17);
        private static uint P1(uint value) => value ^ RotateLeft(value, 15) ^ RotateLeft(value, 23);

        private static uint RotateLeft(uint value, int bits)
        {
            bits &= 31;
            return (value << bits) | (value >> (32 - bits));
        }

        private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes) =>
            ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

        private static void WriteUInt32BigEndian(Span<byte> bytes, uint value)
        {
            bytes[0] = (byte)(value >> 24);
            bytes[1] = (byte)(value >> 16);
            bytes[2] = (byte)(value >> 8);
            bytes[3] = (byte)value;
        }
    }
}
