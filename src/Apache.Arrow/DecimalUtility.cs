// Licensed to the Apache Software Foundation (ASF) under one or more
// contributor license agreements. See the NOTICE file distributed with
// this work for additional information regarding copyright ownership.
// The ASF licenses this file to You under the Apache License, Version 2.0
// (the "License"); you may not use this file except in compliance with
// the License.  You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
#if NET7_0_OR_GREATER
using System.Buffers.Binary;
#endif
using System.Data.SqlTypes;
using System.Numerics;

namespace Apache.Arrow
{
    /// <summary>
    /// This is semi-optimised best attempt at converting to / from decimal and the buffers
    /// </summary>
    internal static class DecimalUtility
    {
        private static readonly BigInteger _maxDecimal = new BigInteger(decimal.MaxValue);
        private static readonly BigInteger _minDecimal = new BigInteger(decimal.MinValue);
        private static readonly ulong[] s_powersOfTen =
        {
            1, 10, 100, 1000, 10000, 100000, 1000000, 10000000, 100000000, 1000000000, 10000000000, 100000000000,
            1000000000000, 10000000000000, 100000000000000, 1000000000000000, 10000000000000000, 100000000000000000,
            1000000000000000000, 10000000000000000000
        };

        private static int PowersOfTenLength => s_powersOfTen.Length - 1;

        // Powers of ten as BigInteger, cached so that the conversions below never recompute one per value.
        // Covers every precision and scale a decimal256 can express (76 digits), plus a little headroom.
        private static readonly BigInteger[] s_bigIntegerPowersOfTen = ComputeBigIntegerPowersOfTen();

        private static BigInteger[] ComputeBigIntegerPowersOfTen()
        {
            var powers = new BigInteger[78]; // 10^0 through 10^77
            powers[0] = BigInteger.One;
            for (int i = 1; i < powers.Length; i++)
            {
                powers[i] = powers[i - 1] * 10;
            }
            return powers;
        }

        private static BigInteger BigIntegerPowerOfTen(int exponent)
        {
            BigInteger[] powers = s_bigIntegerPowersOfTen;

            // An exponent outside the table is left to BigInteger.Pow, which also keeps its argument
            // validation for the negative case.
            return (uint)exponent < (uint)powers.Length ? powers[exponent] : BigInteger.Pow(10, exponent);
        }

#if NET7_0_OR_GREATER
        // decimal mantissa is 96 bits unsigned
        private static readonly UInt128 s_maxDecimalMantissa = new UInt128(0x0000_0000_FFFF_FFFF, 0xFFFF_FFFF_FFFF_FFFF);

        private static readonly UInt128[] s_uint128PowersOfTen = ComputeUInt128Powers();

        // s_uint128MaxDividedByPowersOfTen[i] is the largest value that can be multiplied by 10^i without
        // overflowing a UInt128, so that scaling can be range checked without dividing.
        private static readonly UInt128[] s_uint128MaxDividedByPowersOfTen = ComputeUInt128MaxDividedByPowers();

        private static UInt128[] ComputeUInt128Powers()
        {
            var powers = new UInt128[39]; // 10^0 through 10^38
            powers[0] = 1;
            for (int i = 1; i < powers.Length; i++)
                powers[i] = powers[i - 1] * 10;
            return powers;
        }

        private static UInt128[] ComputeUInt128MaxDividedByPowers()
        {
            UInt128[] powers = s_uint128PowersOfTen;
            var limits = new UInt128[powers.Length];
            for (int i = 0; i < limits.Length; i++)
                limits[i] = UInt128.MaxValue / powers[i];
            return limits;
        }

        private static bool TryMultiplyByPowerOfTen(UInt128 value, int power, out UInt128 result)
        {
            if (power <= 0 || value == UInt128.Zero)
            {
                result = value;
                return true;
            }

            UInt128[] powers = s_uint128PowersOfTen;
            if (power >= powers.Length || value > s_uint128MaxDividedByPowersOfTen[power])
            {
                result = default;
                return false;
            }

            result = value * powers[power];
            return true;
        }
#endif

        internal static decimal GetDecimal(in ArrowBuffer valueBuffer, int index, int scale, int byteWidth)
        {
            if (!TryGetDecimal(valueBuffer, index, scale, byteWidth, out decimal result))
            {
                throw new OverflowException("Value is too large or too small to be represented as a decimal");
            }
            return result;
        }

        internal static bool TryGetDecimal(in ArrowBuffer valueBuffer, int index, int scale, int byteWidth, out decimal result)
        {
            int startIndex = index * byteWidth;
            ReadOnlySpan<byte> value = valueBuffer.Span.Slice(startIndex, byteWidth);

#if NET7_0_OR_GREATER
            if (byteWidth == 16)
            {
                Int128 int128Value = BinaryPrimitives.ReadInt128LittleEndian(value);
                return TryGetDecimalViaInt128(int128Value, scale, out result);
            }

            if (byteWidth == 32)
            {
                // Check if the value fits in 128 bits (upper 128 bits are sign extension)
                Int128 lower = BinaryPrimitives.ReadInt128LittleEndian(value);
                Int128 upper = BinaryPrimitives.ReadInt128LittleEndian(value.Slice(16));
                Int128 signExtension = lower < 0 ? Int128.NegativeOne : Int128.Zero;
                if (upper == signExtension)
                {
                    return TryGetDecimalViaInt128(lower, scale, out result);
                }
            }
#endif

            return TryGetDecimalViaBigInteger(value, scale, out result);
        }

#if NET7_0_OR_GREATER
        private static bool TryGetDecimalViaInt128(Int128 integerValue, int scale, out decimal result)
        {
            bool negative = integerValue < 0;
            UInt128 abs = negative ? (UInt128)(-integerValue) : (UInt128)integerValue;

            // Fast path: value fits directly in decimal (96-bit mantissa, scale <= 28)
            if (abs <= s_maxDecimalMantissa && scale <= 28)
            {
                result = UInt128ToDecimal(abs, negative, (byte)scale);
                return true;
            }

            if (scale == 0)
            {
                result = default;
                return false;
            }

            // Split into integer and fractional parts
            if (scale <= 38)
            {
                UInt128 scaleBy = s_uint128PowersOfTen[scale];
                (UInt128 intPart, UInt128 fracPart) = UInt128.DivRem(abs, scaleBy);

                if (intPart > s_maxDecimalMantissa)
                {
                    result = default;
                    return false;
                }

                decimal intDecimal = UInt128ToDecimal(intPart, negative, 0);

                // Reduce fractional part to fit decimal constraints
                int fracScale = scale;
                while (fracPart > s_maxDecimalMantissa || fracScale > 28)
                {
                    fracPart /= 10;
                    fracScale--;
                }

                decimal fracDecimal = UInt128ToDecimal(fracPart, false, (byte)fracScale);
                result = negative ? intDecimal - fracDecimal : intDecimal + fracDecimal;
                return true;
            }
            else
            {
                // scale > 38: abs < 2^127 < 10^39, so the integer part is 0 or very small.
                // Reduce mantissa and scale together until they fit decimal constraints.
                UInt128 mantissa = abs;
                int decScale = scale;
                while (mantissa > s_maxDecimalMantissa || decScale > 28)
                {
                    mantissa /= 10;
                    decScale--;
                }

                result = UInt128ToDecimal(mantissa, negative, (byte)decScale);
                return true;
            }
        }

        private static decimal UInt128ToDecimal(UInt128 value, bool negative, byte scale)
        {
            ulong lo64 = (ulong)(value & ulong.MaxValue);
            uint hi32 = (uint)(value >> 64);
            return new decimal((int)lo64, (int)(lo64 >> 32), (int)hi32, negative, scale);
        }
#endif

        private static bool TryGetDecimalViaBigInteger(ReadOnlySpan<byte> value, int scale, out decimal result)
        {
            BigInteger integerValue;

#if NETCOREAPP
            integerValue = new BigInteger(value);
#else
            integerValue = new BigInteger(value.ToArray());
#endif

            if (integerValue > _maxDecimal || integerValue < _minDecimal)
            {
                BigInteger scaleBy = BigIntegerPowerOfTen(scale);
                BigInteger integerPart = BigInteger.DivRem(integerValue, scaleBy, out BigInteger fractionalPart);

                if (integerPart > _maxDecimal || integerPart < _minDecimal)
                {
                    result = default;
                    return false;
                }

                result = (decimal)integerPart + FractionToDecimal(fractionalPart, scale);
                return true;
            }
            else
            {
                result = DivideByScale(integerValue, scale);
                return true;
            }
        }

#if NETCOREAPP
        internal unsafe static string GetString(in ArrowBuffer valueBuffer, int index, int precision, int scale, int byteWidth)
        {
            int startIndex = index * byteWidth;
            ReadOnlySpan<byte> value = valueBuffer.Span.Slice(startIndex, byteWidth);
            BigInteger integerValue = new BigInteger(value);
            if (scale == 0)
            {
                return integerValue.ToString();
            }

            bool negative = integerValue.Sign < 0;
            if (negative)
            {
                integerValue = -integerValue;
            }

            int start = scale + 3;
            Span<char> result = stackalloc char[start + precision];
            if (!integerValue.TryFormat(result.Slice(start), out int charsWritten) || charsWritten > precision)
            {
                throw new OverflowException($"Value: {integerValue} cannot be formatted");
            }

            if (scale >= charsWritten)
            {
                int length = charsWritten;
                result[++length] = '0';
                result[++length] = '.';
                while (scale > length - 2)
                {
                    result[++length] = '0';
                }
                start = charsWritten + 1;
                charsWritten = length;
            }
            else
            {
                result.Slice(start, charsWritten - scale).CopyTo(result.Slice(--start));
                charsWritten++;
                result[charsWritten + 1] = '.';
            }

            if (negative)
            {
                result[--start] = '-';
                charsWritten++;
            }

            return new string(result.Slice(start, charsWritten));
        }
#else
        internal unsafe static string GetString(in ArrowBuffer valueBuffer, int index, int precision, int scale, int byteWidth)
        {
            int startIndex = index * byteWidth;
            ReadOnlySpan<byte> value = valueBuffer.Span.Slice(startIndex, byteWidth);
            BigInteger integerValue = new BigInteger(value.ToArray());
            if (scale == 0)
            {
                return integerValue.ToString();
            }

            bool negative = integerValue.Sign < 0;
            if (negative)
            {
                integerValue = -integerValue;
            }

            string toString = integerValue.ToString();
            int charsWritten = toString.Length;
            if (charsWritten > precision)
            {
                throw new OverflowException($"Value: {integerValue} cannot be formatted");
            }

            char[] result = new char[precision + 2];
            int pos = 0;
            if (negative)
            {
                result[pos++] = '-';
            }
            if (scale >= charsWritten)
            {
                result[pos++] = '0';
                result[pos++] = '.';
                int length = 0;
                while (scale > charsWritten + length)
                {
                    result[pos++] = '0';
                    length++;
                }
                toString.CopyTo(0, result, pos, charsWritten);
                pos += charsWritten;
            }
            else
            {
                int wholePartLength = charsWritten - scale;
                toString.CopyTo(0, result, pos, wholePartLength);
                pos += wholePartLength;
                result[pos++] = '.';
                toString.CopyTo(wholePartLength, result, pos, scale);
                pos += scale;
            }
            return new string(result, 0, pos);
        }
#endif

        internal static SqlDecimal GetSqlDecimal128(in ArrowBuffer valueBuffer, int index, int precision, int scale)
        {
            const int byteWidth = 16;
            const int intWidth = byteWidth / 4;
            const int longWidth = byteWidth / 8;

            byte mostSignificantByte = valueBuffer.Span[(index + 1) * byteWidth - 1];
            bool isPositive = (mostSignificantByte & 0x80) == 0;

            if (isPositive)
            {
                ReadOnlySpan<int> value = valueBuffer.Span.CastTo<int>().Slice(index * intWidth, intWidth);
                return new SqlDecimal((byte)precision, (byte)scale, true, value[0], value[1], value[2], value[3]);
            }
            else
            {
                ReadOnlySpan<long> value = valueBuffer.Span.CastTo<long>().Slice(index * longWidth, longWidth);
                long data1 = -value[0];
                long data2 = (data1 == 0) ? -value[1] : ~value[1];

                return new SqlDecimal((byte)precision, (byte)scale, false, (int)(data1 & 0xffffffff), (int)(data1 >> 32), (int)(data2 & 0xffffffff), (int)(data2 >> 32));
            }
        }

        private static decimal FractionToDecimal(BigInteger fractionalPart, int scale)
        {
            // The fractional BigInteger may have more digits than decimal can represent (~28-29).
            // Reduce it by dividing out powers of 10, losing the least-significant digits,
            // then divide the remainder by the reduced scale.
            int digitsRemoved = 0;
            while (fractionalPart > _maxDecimal || fractionalPart < _minDecimal)
            {
                fractionalPart /= 10;
                digitsRemoved++;
            }

            return DivideByScale(fractionalPart, scale - digitsRemoved);
        }

        private static decimal DivideByScale(BigInteger integerValue, int scale)
        {
            decimal result = (decimal)integerValue; // this cast is safe here
            int drop = scale;
            while (drop > PowersOfTenLength)
            {
                result /= s_powersOfTen[PowersOfTenLength];
                drop -= PowersOfTenLength;
            }

            result /= s_powersOfTen[drop];
            return result;
        }

#if NET7_0_OR_GREATER
        /// <summary>
        /// Writes a decimal to the value buffer using 128-bit arithmetic, returning false when the value
        /// does not fit <paramref name="byteWidth"/> or when padding it out to <paramref name="scale"/>
        /// needs more than 128 bits. Both are left to BigInteger, which reports them as it always has.
        /// </summary>
        private static bool TryWriteDecimal(decimal value, ReadOnlySpan<int> decimalBits, int decScale, int precision, int scale, int byteWidth, Span<byte> bytes)
        {
            UInt128 mantissa = new UInt128((uint)decimalBits[2], ((ulong)(uint)decimalBits[1] << 32) | (uint)decimalBits[0]);

            // validate precision, which as below is measured against the decimal's own digits rather than
            // against the value once it has been padded out to the array's scale
            UInt128[] powers = s_uint128PowersOfTen;
            if ((uint)precision < (uint)powers.Length && mantissa >= powers[precision])
                throw new OverflowException($"Decimal precision cannot be greater than that in the Arrow vector: {value} has precision > {precision}");

            // pad with trailing zeros
            if (!TryMultiplyByPowerOfTen(mantissa, scale - decScale, out UInt128 unscaled) ||
                unscaled > (UInt128)Int128.MaxValue)
            {
                return false;
            }

            Int128 signed = decimalBits[3] < 0 ? -(Int128)unscaled : (Int128)unscaled;

            switch (byteWidth)
            {
                case 4:
                    if (signed < int.MinValue || signed > int.MaxValue)
                        return false;
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)signed);
                    return true;

                case 8:
                    if (signed < long.MinValue || signed > long.MaxValue)
                        return false;
                    BinaryPrimitives.WriteInt64LittleEndian(bytes, (long)signed);
                    return true;

                case 16:
                    BinaryPrimitives.WriteInt128LittleEndian(bytes, signed);
                    return true;

                case 32:
                    BinaryPrimitives.WriteInt128LittleEndian(bytes, signed);
                    bytes.Slice(16).Fill(Int128.IsNegative(signed) ? (byte)0xFF : (byte)0);
                    return true;

                default:
                    return false;
            }
        }
#endif

        internal static void GetBytes(decimal value, int precision, int scale, int byteWidth, Span<byte> bytes)
        {
            // create BigInteger from decimal
            BigInteger bigInt;

#if NET5_0_OR_GREATER
            Span<int> decimalBits = stackalloc int[4];
            decimal.GetBits(value, decimalBits);
#else
            int[] decimalBits = decimal.GetBits(value);
#endif

            int decScale = (decimalBits[3] >> 16) & 0x7F;

            // validate scale
            if (decScale > scale)
                throw new OverflowException($"Decimal scale cannot be greater than that in the Arrow vector: {decScale} != {scale}");

#if NET7_0_OR_GREATER
            // A decimal's mantissa is 96 bits, so the whole conversion fits in 128-bit arithmetic unless
            // padding the scale overflows it, which only BigInteger can represent.
            if (bytes.Length == byteWidth &&
                TryWriteDecimal(value, decimalBits, decScale, precision, scale, byteWidth, bytes))
            {
                return;
            }
#endif

#if NETCOREAPP
            Span<byte> bigIntBytes = stackalloc byte[13];

            Span<byte> intBytes = stackalloc byte[4];
            for (int i = 0; i < 3; i++)
            {
                int bit = decimalBits[i];
                if (!BitConverter.TryWriteBytes(intBytes, bit))
                    throw new OverflowException($"Could not extract bytes from int {bit}");

                for (int j = 0; j < 4; j++)
                {
                    bigIntBytes[4 * i + j] = intBytes[j];
                }
            }
            bigInt = new BigInteger(bigIntBytes);
#else
            byte[] bigIntBytes = new byte[13];
            for (int i = 0; i < 3; i++)
            {
                int bit = decimalBits[i];
                byte[] intBytes = BitConverter.GetBytes(bit);
                for (int j = 0; j < intBytes.Length; j++)
                {
                    bigIntBytes[4 * i + j] = intBytes[j];
                }
            }
            bigInt = new BigInteger(bigIntBytes);
#endif

            if (value < 0)
            {
                bigInt = -bigInt;
            }

            // validate precision
            if (bigInt >= BigIntegerPowerOfTen(precision))
                throw new OverflowException($"Decimal precision cannot be greater than that in the Arrow vector: {value} has precision > {precision}");

            if (decScale < scale) // pad with trailing zeros
            {
                bigInt *= BigIntegerPowerOfTen(scale - decScale);
            }

            // extract bytes from BigInteger
            if (bytes.Length != byteWidth)
            {
                throw new OverflowException($"ValueBuffer size not equal to {byteWidth} byte width: {bytes.Length}");
            }

            int bytesWritten;
#if NETCOREAPP
            if (!bigInt.TryWriteBytes(bytes, out bytesWritten, false, !BitConverter.IsLittleEndian))
                throw new OverflowException("Could not extract bytes from integer value " + bigInt);
#else
            byte[] tempBytes = bigInt.ToByteArray();
            tempBytes.CopyTo(bytes);
            bytesWritten = tempBytes.Length;
#endif

            if (bytes.Length > byteWidth)
            {
                throw new OverflowException($"Decimal size greater than {byteWidth} bytes: {bytes.Length}");
            }

            if (bigInt.Sign == -1)
            {
                for (int i = bytesWritten; i < byteWidth; i++)
                {
                    bytes[i] = 255;
                }
            }
        }

        internal static void GetBytes(string value, int precision, int scale, int byteWidth, Span<byte> bytes)
        {
            if (value == null || value.Length == 0)
            {
                throw new ArgumentException("numeric value may not be null or blank", nameof(value));
            }

            int start = 0;
            if (value[0] == '-' || value[0] == '+')
            {
                start++;
            }
            while (value[start] == '0' && start < value.Length - 1)
            {
                start++;
            }

            int pos = value.IndexOf('.');
            int neededPrecision = value.Length - start;
            int neededScale;
            if (pos == -1)
            {
                neededScale = 0;
            }
            else
            {
                neededPrecision--;
                neededScale = value.Length - pos - 1;
            }

            if (neededScale > scale)
            {
                throw new OverflowException($"Decimal scale cannot be greater than that in the Arrow vector: {value} has scale > {scale}");
            }
            if (neededPrecision > precision)
            {
                throw new OverflowException($"Decimal precision cannot be greater than that in the Arrow vector: {value} has precision > {precision}");
            }

#if NETCOREAPP
            ReadOnlySpan<char> src = value.AsSpan();
            Span<char> buffer = stackalloc char[precision + start + 1];

            int end;
            if (pos == -1)
            {
                src.CopyTo(buffer);
                end = src.Length;
            }
            else
            {
                src.Slice(0, pos).CopyTo(buffer);
                src.Slice(pos + 1).CopyTo(buffer.Slice(pos));
                end = src.Length - 1;
            }

            while (neededScale < scale)
            {
                buffer[end++] = '0';
                neededScale++;
            }

            if (!BigInteger.TryParse(buffer.Slice(0, end), out BigInteger bigInt))
            {
                throw new ArgumentException($"Unable to parse {value} as decimal");
            }

            if (!bigInt.TryWriteBytes(bytes, out int bytesWritten, false, !BitConverter.IsLittleEndian))
            {
                throw new OverflowException("Could not extract bytes from integer value " + bigInt);
            }
#else
            char[] buffer = new char[precision + start + 1];

            int end;
            if (pos == -1)
            {
                value.CopyTo(0, buffer, 0, value.Length);
                end = value.Length;
            }
            else
            {
                value.CopyTo(0, buffer, 0, pos);
                value.CopyTo(pos + 1, buffer, pos, neededScale);
                end = value.Length - 1;
            }

            while (neededScale < scale)
            {
                buffer[end++] = '0';
                neededScale++;
            }

            if (!BigInteger.TryParse(new string(buffer, 0, end), out BigInteger bigInt))
            {
                throw new ArgumentException($"Unable to parse {value} as decimal");
            }

            byte[] tempBytes = bigInt.ToByteArray();
            try
            {
                tempBytes.CopyTo(bytes);
            }
            catch (ArgumentException)
            {
                throw new OverflowException("Could not extract bytes from integer value " + bigInt);
            }
            int bytesWritten = tempBytes.Length;
#endif

            if (bytes.Length > byteWidth)
            {
                throw new OverflowException($"Decimal size greater than {byteWidth} bytes: {bytes.Length}");
            }

            byte fill = bigInt.Sign == -1 ? (byte)255 : (byte)0;
            for (int i = bytesWritten; i < byteWidth; i++)
            {
                bytes[i] = fill;
            }
        }

        internal static void GetBytes(SqlDecimal value, int precision, int scale, Span<byte> bytes)
        {
            if (value.Precision != precision || value.Scale != scale)
            {
                value = SqlDecimal.ConvertToPrecScale(value, precision, scale);
            }

#if NET7_0_OR_GREATER
            value.WriteTdsValue(bytes.CastTo<uint>());
#else
            value.Data.AsSpan().CopyTo(bytes.CastTo<int>());
#endif

            if (!value.IsPositive)
            {
                Span<long> longSpan = bytes.CastTo<long>();
                longSpan[0] = -longSpan[0];
                longSpan[1] = (longSpan[0] == 0) ? -longSpan[1] : ~longSpan[1];
            }
        }
    }
}
