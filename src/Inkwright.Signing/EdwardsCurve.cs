using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace Inkwright.Signing;

/// <summary>
/// Ed25519 and Ed448 as RFC 8032 defines them, on <see cref="BigInteger"/> arithmetic. The base
/// class library carries no Edwards-curve primitive, and ISO/TS 32002 admits these signatures into
/// PDF, so checking one here is the only way to check it at all without a third-party stack. The
/// arithmetic is unhurried and makes no attempt to run in constant time, which is fine for
/// verification — everything it touches is public — and for the test-signing this library does.
/// </summary>
internal static class EdwardsCurve
{
    // --- Ed25519: a = -1, p = 2^255 - 19 (RFC 8032 §5.1) ---

    private static readonly BigInteger P25519 = BigInteger.Pow(2, 255) - 19;

    private static readonly BigInteger D25519 = BigInteger.Parse(
        "37095705934669439343138083508754565189542113879843219016388785533085940283555",
        CultureInfo.InvariantCulture);

    private static readonly BigInteger L25519 = BigInteger.Pow(2, 252) +
        BigInteger.Parse("27742317777372353535851937790883648493", CultureInfo.InvariantCulture);

    private static readonly BigInteger Bx25519 = BigInteger.Parse(
        "15112221349535400772501151409588531511454012693041857206046113283949847762202",
        CultureInfo.InvariantCulture);

    private static readonly BigInteger By25519 = BigInteger.Parse(
        "46316835694926478169428394003475163141307993866256225615783033603165251855960",
        CultureInfo.InvariantCulture);

    // --- Ed448: a = 1, p = 2^448 - 2^224 - 1 (RFC 8032 §5.2) ---

    private static readonly BigInteger P448 = BigInteger.Pow(2, 448) - BigInteger.Pow(2, 224) - 1;

    private static readonly BigInteger D448 = P448 - 39081;

    private static readonly BigInteger L448 = BigInteger.Pow(2, 446) -
        BigInteger.Parse(
            "13818066809895115352007386748515426880336692474882178609894547503885",
            CultureInfo.InvariantCulture);

    private static readonly BigInteger Bx448 = BigInteger.Parse(
        "224580040295924300187604334099896036246789641632564134246125461686950415467406032909029192869357953282578032075146446173674602635247710",
        CultureInfo.InvariantCulture);

    private static readonly BigInteger By448 = BigInteger.Parse(
        "298819210078481492676017930443930673437544040154080242095928241372331506189835876003536878655418784733982303233503462500531545062832660",
        CultureInfo.InvariantCulture);

    /// <summary>The prefix RFC 8032 calls dom4, for Ed448 with an empty context and no pre-hash.</summary>
    private static readonly byte[] Dom448 = [.. "SigEd448"u8, 0, 0];

    // ---------------------------------------------------------------- Ed25519

    /// <summary>Checks an Ed25519 signature over a message (RFC 8032 §5.1.7).</summary>
    internal static bool VerifyEd25519(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64)
            return false;

        if (Decode25519(publicKey) is not { } a || Decode25519(signature[..32]) is not { } r)
            return false;

        BigInteger s = LittleEndian(signature[32..]);
        if (s >= L25519)
            return false;

        byte[] digest = SHA512.HashData([.. signature[..32], .. publicKey, .. message]);
        BigInteger k = LittleEndian(digest) % L25519;

        Point25519 left = Multiply25519(Base25519(), s);
        Point25519 right = Add25519(r, Multiply25519(a, k));
        return SamePoint25519(left, right);
    }

    /// <summary>Signs a message with an Ed25519 private key seed (RFC 8032 §5.1.6).</summary>
    internal static byte[] SignEd25519(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        byte[] digest = SHA512.HashData(seed);
        BigInteger scalar = Clamp25519(digest.AsSpan(0, 32));
        byte[] prefix = digest[32..];

        byte[] publicKey = Encode25519(Multiply25519(Base25519(), scalar));

        BigInteger r = LittleEndian(SHA512.HashData([.. prefix, .. message])) % L25519;
        byte[] rEncoded = Encode25519(Multiply25519(Base25519(), r));

        BigInteger k = LittleEndian(SHA512.HashData([.. rEncoded, .. publicKey, .. message])) % L25519;
        BigInteger s = (r + (k * scalar)) % L25519;

        return [.. rEncoded, .. ToLittleEndian(s, 32)];
    }

    /// <summary>Derives the public key of an Ed25519 seed (RFC 8032 §5.1.5).</summary>
    internal static byte[] Ed25519PublicKey(ReadOnlySpan<byte> seed)
    {
        BigInteger scalar = Clamp25519(SHA512.HashData(seed).AsSpan(0, 32));
        return Encode25519(Multiply25519(Base25519(), scalar));
    }

    private static BigInteger Clamp25519(ReadOnlySpan<byte> half)
    {
        byte[] copy = half.ToArray();
        copy[0] &= 248;
        copy[31] &= 127;
        copy[31] |= 64;
        return LittleEndian(copy);
    }

    /// <summary>Extended homogeneous coordinates: x = X/Z, y = Y/Z, T = XY/Z.</summary>
    private readonly record struct Point25519(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static Point25519 Base25519() => new(Bx25519, By25519, BigInteger.One, Bx25519 * By25519 % P25519);

    private static Point25519 Identity25519() => new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);

    private static Point25519 Add25519(Point25519 p, Point25519 q)
    {
        BigInteger a = Mod((p.Y - p.X) * (q.Y - q.X), P25519);
        BigInteger b = Mod((p.Y + p.X) * (q.Y + q.X), P25519);
        BigInteger c = Mod(p.T * 2 * D25519 % P25519 * q.T, P25519);
        BigInteger d = Mod(p.Z * 2 * q.Z, P25519);
        BigInteger e = b - a;
        BigInteger f = d - c;
        BigInteger g = d + c;
        BigInteger h = b + a;
        return new Point25519(Mod(e * f, P25519), Mod(g * h, P25519), Mod(f * g, P25519), Mod(e * h, P25519));
    }

    private static Point25519 Multiply25519(Point25519 point, BigInteger scalar)
    {
        Point25519 result = Identity25519();
        Point25519 addend = point;

        while (scalar > 0)
        {
            if (!scalar.IsEven)
                result = Add25519(result, addend);

            addend = Add25519(addend, addend);
            scalar >>= 1;
        }

        return result;
    }

    private static bool SamePoint25519(Point25519 p, Point25519 q) =>
        Mod((p.X * q.Z) - (q.X * p.Z), P25519).IsZero && Mod((p.Y * q.Z) - (q.Y * p.Z), P25519).IsZero;

    private static byte[] Encode25519(Point25519 point)
    {
        BigInteger inverse = BigInteger.ModPow(point.Z, P25519 - 2, P25519);
        BigInteger x = Mod(point.X * inverse, P25519);
        BigInteger y = Mod(point.Y * inverse, P25519);

        byte[] encoded = ToLittleEndian(y, 32);
        if (!x.IsEven)
            encoded[31] |= 0x80;

        return encoded;
    }

    private static Point25519? Decode25519(ReadOnlySpan<byte> encoded)
    {
        byte[] bytes = encoded.ToArray();
        int signBit = (bytes[31] >> 7) & 1;
        bytes[31] &= 0x7F;

        BigInteger y = LittleEndian(bytes);
        if (y >= P25519)
            return null;

        // x² = (y² - 1) / (d·y² + 1); the square root recipe of §5.1.3 for p ≡ 5 (mod 8).
        BigInteger y2 = y * y % P25519;
        BigInteger u = Mod(y2 - 1, P25519);
        BigInteger v = Mod((D25519 * y2) + 1, P25519);

        BigInteger candidate = u * BigInteger.ModPow(v, 3, P25519) % P25519 *
            BigInteger.ModPow(u * BigInteger.ModPow(v, 7, P25519) % P25519, (P25519 - 5) / 8, P25519) % P25519;

        BigInteger check = v * candidate % P25519 * candidate % P25519;
        BigInteger x;

        if (check == u)
            x = candidate;
        else if (check == Mod(-u, P25519))
            x = candidate * BigInteger.ModPow(2, (P25519 - 1) / 4, P25519) % P25519;
        else
            return null;

        if (x.IsZero && signBit == 1)
            return null;

        if ((x.IsEven ? 0 : 1) != signBit)
            x = Mod(-x, P25519);

        return new Point25519(x, y, BigInteger.One, x * y % P25519);
    }

    // ---------------------------------------------------------------- Ed448

    /// <summary>Whether this machine can work with Ed448, whose hash is SHAKE256.</summary>
    internal static bool Ed448Available => Shake256.IsSupported;

    /// <summary>Checks an Ed448 signature over a message (RFC 8032 §5.2.7).</summary>
    internal static bool VerifyEd448(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 57 || signature.Length != 114 || !Ed448Available)
            return false;

        if (Decode448(publicKey) is not { } a || Decode448(signature[..57]) is not { } r)
            return false;

        BigInteger s = LittleEndian(signature[57..]);
        if (s >= L448)
            return false;

        byte[] digest = Shake256.HashData([.. Dom448, .. signature[..57], .. publicKey, .. message], 114);
        BigInteger k = LittleEndian(digest) % L448;

        Point448 left = Multiply448(Base448(), s);
        Point448 right = Add448(r, Multiply448(a, k));
        return SamePoint448(left, right);
    }

    /// <summary>Signs a message with an Ed448 private key seed (RFC 8032 §5.2.6).</summary>
    internal static byte[] SignEd448(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        byte[] digest = Shake256.HashData(seed, 114);
        BigInteger scalar = Clamp448(digest.AsSpan(0, 57));
        byte[] prefix = digest[57..];

        byte[] publicKey = Encode448(Multiply448(Base448(), scalar));

        BigInteger r = LittleEndian(Shake256.HashData([.. Dom448, .. prefix, .. message], 114)) % L448;
        byte[] rEncoded = Encode448(Multiply448(Base448(), r));

        BigInteger k = LittleEndian(Shake256.HashData([.. Dom448, .. rEncoded, .. publicKey, .. message], 114)) % L448;
        BigInteger s = (r + (k * scalar)) % L448;

        return [.. rEncoded, .. ToLittleEndian(s, 57)];
    }

    /// <summary>Derives the public key of an Ed448 seed (RFC 8032 §5.2.5).</summary>
    internal static byte[] Ed448PublicKey(ReadOnlySpan<byte> seed)
    {
        BigInteger scalar = Clamp448(Shake256.HashData(seed, 114).AsSpan(0, 57));
        return Encode448(Multiply448(Base448(), scalar));
    }

    private static BigInteger Clamp448(ReadOnlySpan<byte> half)
    {
        byte[] copy = half.ToArray();
        copy[0] &= 0xFC;
        copy[55] |= 0x80;
        copy[56] = 0;
        return LittleEndian(copy);
    }

    /// <summary>Projective coordinates: x = X/Z, y = Y/Z.</summary>
    private readonly record struct Point448(BigInteger X, BigInteger Y, BigInteger Z);

    private static Point448 Base448() => new(Bx448, By448, BigInteger.One);

    private static Point448 Identity448() => new(BigInteger.Zero, BigInteger.One, BigInteger.One);

    private static Point448 Add448(Point448 p, Point448 q)
    {
        BigInteger a = p.Z * q.Z % P448;
        BigInteger b = a * a % P448;
        BigInteger c = p.X * q.X % P448;
        BigInteger d = p.Y * q.Y % P448;
        BigInteger e = D448 * c % P448 * d % P448;
        BigInteger f = Mod(b - e, P448);
        BigInteger g = Mod(b + e, P448);
        BigInteger h = Mod((p.X + p.Y) * (q.X + q.Y), P448);

        return new Point448(
            Mod(a * f % P448 * (h - c - d), P448),
            Mod(a * g % P448 * (d - c), P448),
            f * g % P448);
    }

    private static Point448 Multiply448(Point448 point, BigInteger scalar)
    {
        Point448 result = Identity448();
        Point448 addend = point;

        while (scalar > 0)
        {
            if (!scalar.IsEven)
                result = Add448(result, addend);

            addend = Add448(addend, addend);
            scalar >>= 1;
        }

        return result;
    }

    private static bool SamePoint448(Point448 p, Point448 q) =>
        Mod((p.X * q.Z) - (q.X * p.Z), P448).IsZero && Mod((p.Y * q.Z) - (q.Y * p.Z), P448).IsZero;

    private static byte[] Encode448(Point448 point)
    {
        BigInteger inverse = BigInteger.ModPow(point.Z, P448 - 2, P448);
        BigInteger x = Mod(point.X * inverse, P448);
        BigInteger y = Mod(point.Y * inverse, P448);

        byte[] encoded = ToLittleEndian(y, 57);
        if (!x.IsEven)
            encoded[56] |= 0x80;

        return encoded;
    }

    private static Point448? Decode448(ReadOnlySpan<byte> encoded)
    {
        byte[] bytes = encoded.ToArray();
        int signBit = (bytes[56] >> 7) & 1;
        bytes[56] &= 0x7F;

        BigInteger y = LittleEndian(bytes);
        if (y >= P448)
            return null;

        // x² = (y² - 1) / (d·y² - 1); the square root recipe of §5.2.3 for p ≡ 3 (mod 4).
        BigInteger y2 = y * y % P448;
        BigInteger u = Mod(y2 - 1, P448);
        BigInteger v = Mod((D448 * y2) - 1, P448);

        BigInteger candidate = BigInteger.ModPow(u, 3, P448) * v % P448 *
            BigInteger.ModPow(
                BigInteger.ModPow(u, 5, P448) * BigInteger.ModPow(v, 3, P448) % P448,
                (P448 - 3) / 4,
                P448) % P448;

        if (v * candidate % P448 * candidate % P448 != u)
            return null;

        if (candidate.IsZero && signBit == 1)
            return null;

        if ((candidate.IsEven ? 0 : 1) != signBit)
            candidate = Mod(-candidate, P448);

        return new Point448(candidate, y, BigInteger.One);
    }

    // ---------------------------------------------------------------- shared

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        BigInteger remainder = value % modulus;
        return remainder.Sign < 0 ? remainder + modulus : remainder;
    }

    private static BigInteger LittleEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    private static byte[] ToLittleEndian(BigInteger value, int length)
    {
        byte[] result = new byte[length];
        value.TryWriteBytes(result, out _, isUnsigned: true, isBigEndian: false);
        return result;
    }
}
