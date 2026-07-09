using System;
using System.Text;

namespace PFound.RemoteResourceCache.Core
{
    /// <summary>
    /// Pure-C# XXH3 64-bit hash — a fast, non-cryptographic digest used here to turn an arbitrary cache key
    /// (a URL/string) into a short, collision-resistant, filesystem-safe filename. Engine-free (BCL only) so it
    /// lives in the Core and is mono/csc-testable; there is no engine-free build of Unity's Burst xxHash3, so
    /// this is a from-spec scalar port of the public XXH3 algorithm (Yann Collet, BSD-2-Clause / public domain).
    /// Integrity/security is NOT a goal here — this is filename derivation, so a fast 64-bit non-crypto hash is
    /// the right trade over SHA-256.
    /// </summary>
    public static class XxHash3
    {
        private const ulong Prime64_1 = 0x9E3779B185EBCA87UL;
        private const ulong Prime64_2 = 0xC2B2AE3D27D4EB4FUL;
        private const ulong Prime64_3 = 0x165667B19E3779F9UL;
        private const ulong Prime64_4 = 0x85EBCA77C2B2AE63UL;
        private const ulong Prime64_5 = 0x27D4EB2F165667C5UL;
        private const uint Prime32_1 = 0x9E3779B1u;
        private const uint Prime32_2 = 0x85EBCA77u;
        private const uint Prime32_3 = 0xC2B2AE3Du;
        private const ulong PrimeMx1 = 0x165667919E3779F9UL;
        private const ulong PrimeMx2 = 0x9FB21C651E98DF25UL;

        private const int StripeLen = 64;
        private const int SecretConsumeRate = 8;
        private const int AccNb = 8; // StripeLen / 8
        private const int SecretSizeMin = 136;
        private const int SecretDefaultSize = 192;
        private const int MidsizeStartOffset = 3;
        private const int MidsizeLastOffset = 17;
        private const int SecretMergeAccsStart = 11;
        private const int SecretLastAccStart = 7;

        // The 192-byte default secret (XXH3_kSecret).
        private static readonly byte[] Secret =
        {
            0xb8, 0xfe, 0x6c, 0x39, 0x23, 0xa4, 0x4b, 0xbe, 0x7c, 0x01, 0x81, 0x2c, 0xf7, 0x21, 0xad, 0x1c,
            0xde, 0xd4, 0x6d, 0xe9, 0x83, 0x90, 0x97, 0xdb, 0x72, 0x40, 0xa4, 0xa4, 0xb7, 0xb3, 0x67, 0x1f,
            0xcb, 0x79, 0xe6, 0x4e, 0xcc, 0xc0, 0xe5, 0x78, 0x82, 0x5a, 0xd0, 0x7d, 0xcc, 0xff, 0x72, 0x21,
            0xb8, 0x08, 0x46, 0x74, 0xf7, 0x43, 0x24, 0x8e, 0xe0, 0x35, 0x90, 0xe6, 0x81, 0x3a, 0x26, 0x4c,
            0x3c, 0x28, 0x52, 0xbb, 0x91, 0xc3, 0x00, 0xcb, 0x88, 0xd0, 0x65, 0x8b, 0x1b, 0x53, 0x2e, 0xa3,
            0x71, 0x64, 0x48, 0x97, 0xa2, 0x0d, 0xf9, 0x4e, 0x38, 0x19, 0xef, 0x46, 0xa9, 0xde, 0xac, 0xd8,
            0xa8, 0xfa, 0x76, 0x3f, 0xe3, 0x9c, 0x34, 0x3f, 0xf9, 0xdc, 0xbb, 0xc7, 0xc7, 0x0b, 0x4f, 0x1d,
            0x8a, 0x51, 0xe0, 0x4b, 0xcd, 0xb4, 0x59, 0x31, 0xc8, 0x9f, 0x7e, 0xc9, 0xd9, 0x78, 0x73, 0x64,
            0xea, 0xc5, 0xac, 0x83, 0x34, 0xd3, 0xeb, 0xc3, 0xc5, 0x81, 0xa0, 0xff, 0xfa, 0x13, 0x63, 0xeb,
            0x17, 0x0d, 0xdd, 0x51, 0xb7, 0xf0, 0xda, 0x49, 0xd3, 0x16, 0x55, 0x26, 0x29, 0xd4, 0x68, 0x9e,
            0x2b, 0x16, 0xbe, 0x58, 0x7d, 0x47, 0xa1, 0xfc, 0x8f, 0xf8, 0xb8, 0xd1, 0x7a, 0xd0, 0x31, 0xce,
            0x45, 0xcb, 0x3a, 0x8f, 0x95, 0x16, 0x04, 0x28, 0xaf, 0xd7, 0xfb, 0xca, 0xbb, 0x4b, 0x40, 0x7e,
        };

        /// <summary>Hashes a UTF-8 encoding of <paramref name="key"/> and returns the 16-char lowercase-hex digest.</summary>
        public static string HashToHex(string key) => Hash64(Encoding.UTF8.GetBytes(key)).ToString("x16");

        /// <summary>XXH3 64-bit of the whole buffer (seed 0, default secret).</summary>
        public static ulong Hash64(byte[] input)
        {
            unchecked
            {
                int len = input.Length;
                if (len <= 16) return Len0To16(input, len);
                if (len <= 128) return Len17To128(input, len);
                if (len <= 240) return Len129To240(input, len);
                return HashLong(input, len);
            }
        }

        // ---- length-class paths ------------------------------------------------------------------

        private static ulong Len0To16(byte[] p, int len)
        {
            if (len > 8) return Len9To16(p, len);
            if (len >= 4) return Len4To8(p, len);
            if (len > 0) return Len1To3(p, len);
            return XXH64Avalanche(ReadLE64(Secret, 56) ^ ReadLE64(Secret, 64));
        }

        private static ulong Len1To3(byte[] p, int len)
        {
            byte c1 = p[0];
            byte c2 = p[len >> 1];
            byte c3 = p[len - 1];
            uint combined = ((uint)c1 << 16) | ((uint)c2 << 24) | c3 | ((uint)len << 8);
            ulong bitflip = ReadLE32(Secret, 0) ^ ReadLE32(Secret, 4);
            return XXH64Avalanche(combined ^ bitflip);
        }

        private static ulong Len4To8(byte[] p, int len)
        {
            uint input1 = ReadLE32(p, 0);
            uint input2 = ReadLE32(p, len - 4);
            ulong bitflip = ReadLE64(Secret, 8) ^ ReadLE64(Secret, 16);
            ulong input64 = input2 + ((ulong)input1 << 32);
            ulong keyed = input64 ^ bitflip;
            return Rrmxmx(keyed, (ulong)len);
        }

        private static ulong Len9To16(byte[] p, int len)
        {
            ulong bitflip1 = ReadLE64(Secret, 24) ^ ReadLE64(Secret, 32);
            ulong bitflip2 = ReadLE64(Secret, 40) ^ ReadLE64(Secret, 48);
            ulong inputLo = ReadLE64(p, 0) ^ bitflip1;
            ulong inputHi = ReadLE64(p, len - 8) ^ bitflip2;
            ulong acc = (ulong)len + Swap64(inputLo) + inputHi + Mul128Fold64(inputLo, inputHi);
            return Avalanche(acc);
        }

        private static ulong Len17To128(byte[] p, int len)
        {
            ulong acc = (ulong)len * Prime64_1;
            if (len > 32)
            {
                if (len > 64)
                {
                    if (len > 96)
                    {
                        acc += Mix16B(p, 48, 96);
                        acc += Mix16B(p, len - 64, 112);
                    }
                    acc += Mix16B(p, 32, 64);
                    acc += Mix16B(p, len - 48, 80);
                }
                acc += Mix16B(p, 16, 32);
                acc += Mix16B(p, len - 32, 48);
            }
            acc += Mix16B(p, 0, 0);
            acc += Mix16B(p, len - 16, 16);
            return Avalanche(acc);
        }

        private static ulong Len129To240(byte[] p, int len)
        {
            ulong acc = (ulong)len * Prime64_1;
            int nbRounds = len / 16;
            int i;
            for (i = 0; i < 8; i++) acc += Mix16B(p, 16 * i, 16 * i);
            acc = Avalanche(acc);
            for (i = 8; i < nbRounds; i++) acc += Mix16B(p, 16 * i, 16 * (i - 8) + MidsizeStartOffset);
            acc += Mix16B(p, len - 16, SecretSizeMin - MidsizeLastOffset);
            return Avalanche(acc);
        }

        private static ulong HashLong(byte[] input, int len)
        {
            ulong[] acc =
            {
                Prime32_3, Prime64_1, Prime64_2, Prime64_3,
                Prime64_4, Prime32_2, Prime64_5, Prime32_1,
            };

            int nbStripesPerBlock = (SecretDefaultSize - StripeLen) / SecretConsumeRate; // 16
            int blockLen = StripeLen * nbStripesPerBlock;                                // 1024
            int nbBlocks = (len - 1) / blockLen;

            for (int n = 0; n < nbBlocks; n++)
            {
                Accumulate(acc, input, n * blockLen, nbStripesPerBlock);
                ScrambleAcc(acc, SecretDefaultSize - StripeLen);
            }

            int nbStripes = ((len - 1) - (blockLen * nbBlocks)) / StripeLen;
            Accumulate(acc, input, nbBlocks * blockLen, nbStripes);

            // last stripe
            Accumulate512(acc, input, len - StripeLen, SecretDefaultSize - StripeLen - SecretLastAccStart);

            return MergeAccs(acc, SecretMergeAccsStart, (ulong)len * Prime64_1);
        }

        // ---- long-hash primitives ----------------------------------------------------------------

        private static void Accumulate(ulong[] acc, byte[] input, int inputOffset, int nbStripes)
        {
            for (int n = 0; n < nbStripes; n++)
                Accumulate512(acc, input, inputOffset + n * StripeLen, n * SecretConsumeRate);
        }

        private static void Accumulate512(ulong[] acc, byte[] input, int inputOffset, int secretOffset)
        {
            for (int i = 0; i < AccNb; i++)
            {
                ulong dataVal = ReadLE64(input, inputOffset + 8 * i);
                ulong dataKey = dataVal ^ ReadLE64(Secret, secretOffset + 8 * i);
                acc[i ^ 1] += dataVal;
                acc[i] += (dataKey & 0xFFFFFFFFUL) * (dataKey >> 32);
            }
        }

        private static void ScrambleAcc(ulong[] acc, int secretOffset)
        {
            for (int i = 0; i < AccNb; i++)
            {
                ulong key64 = ReadLE64(Secret, secretOffset + 8 * i);
                ulong acc64 = acc[i];
                acc64 = XorShift64(acc64, 47);
                acc64 ^= key64;
                acc64 *= Prime32_1;
                acc[i] = acc64;
            }
        }

        private static ulong MergeAccs(ulong[] acc, int secretOffset, ulong start)
        {
            ulong result64 = start;
            for (int i = 0; i < 4; i++)
                result64 += Mix2Accs(acc, 2 * i, secretOffset + 16 * i);
            return Avalanche(result64);
        }

        private static ulong Mix2Accs(ulong[] acc, int accOffset, int secretOffset) =>
            Mul128Fold64(acc[accOffset] ^ ReadLE64(Secret, secretOffset),
                         acc[accOffset + 1] ^ ReadLE64(Secret, secretOffset + 8));

        private static ulong Mix16B(byte[] p, int inputOffset, int secretOffset)
        {
            ulong inputLo = ReadLE64(p, inputOffset);
            ulong inputHi = ReadLE64(p, inputOffset + 8);
            return Mul128Fold64(
                inputLo ^ (ReadLE64(Secret, secretOffset)),
                inputHi ^ (ReadLE64(Secret, secretOffset + 8)));
        }

        // ---- finalizers / mixers -----------------------------------------------------------------

        private static ulong Avalanche(ulong h64)
        {
            h64 = XorShift64(h64, 37);
            h64 *= PrimeMx1;
            h64 = XorShift64(h64, 32);
            return h64;
        }

        private static ulong XXH64Avalanche(ulong h64)
        {
            h64 ^= h64 >> 33;
            h64 *= Prime64_2;
            h64 ^= h64 >> 29;
            h64 *= Prime64_3;
            h64 ^= h64 >> 32;
            return h64;
        }

        private static ulong Rrmxmx(ulong h64, ulong len)
        {
            h64 ^= Rotl64(h64, 49) ^ Rotl64(h64, 24);
            h64 *= PrimeMx2;
            h64 ^= (h64 >> 35) + len;
            h64 *= PrimeMx2;
            return XorShift64(h64, 28);
        }

        private static ulong Mul128Fold64(ulong lhs, ulong rhs)
        {
            ulong loLo = (lhs & 0xFFFFFFFFUL) * (rhs & 0xFFFFFFFFUL);
            ulong hiLo = (lhs >> 32) * (rhs & 0xFFFFFFFFUL);
            ulong loHi = (lhs & 0xFFFFFFFFUL) * (rhs >> 32);
            ulong hiHi = (lhs >> 32) * (rhs >> 32);
            ulong cross = (loLo >> 32) + (hiLo & 0xFFFFFFFFUL) + loHi;
            ulong upper = (hiLo >> 32) + (cross >> 32) + hiHi;
            ulong lower = (cross << 32) | (loLo & 0xFFFFFFFFUL);
            return lower ^ upper;
        }

        private static ulong XorShift64(ulong v, int shift) => v ^ (v >> shift);
        private static ulong Rotl64(ulong v, int r) => (v << r) | (v >> (64 - r));
        private static ulong Swap64(ulong v) =>
            ((v & 0x00000000000000FFUL) << 56) | ((v & 0x000000000000FF00UL) << 40) |
            ((v & 0x0000000000FF0000UL) << 24) | ((v & 0x00000000FF000000UL) << 8) |
            ((v & 0x000000FF00000000UL) >> 8) | ((v & 0x0000FF0000000000UL) >> 24) |
            ((v & 0x00FF000000000000UL) >> 40) | ((v & 0xFF00000000000000UL) >> 56);

        private static uint ReadLE32(byte[] b, int i) =>
            (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));

        private static ulong ReadLE64(byte[] b, int i) =>
            b[i] | ((ulong)b[i + 1] << 8) | ((ulong)b[i + 2] << 16) | ((ulong)b[i + 3] << 24) |
            ((ulong)b[i + 4] << 32) | ((ulong)b[i + 5] << 40) | ((ulong)b[i + 6] << 48) | ((ulong)b[i + 7] << 56);
    }
}
