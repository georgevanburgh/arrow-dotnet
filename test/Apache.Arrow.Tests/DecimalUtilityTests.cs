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
using System.Data.SqlTypes;
using System.Globalization;
using Apache.Arrow.Types;
using Xunit;

namespace Apache.Arrow.Tests
{
    public class DecimalUtilityTests
    {
        public class Overflow
        {
            [Theory]
            [InlineData(100.123, 10, 4, false)]
            [InlineData(100.123, 6, 4, false)]
            [InlineData(100.123, 3, 3, true)]
            [InlineData(100.123, 10, 2, true)]
            [InlineData(100.123, 5, 2, true)]
            [InlineData(100.123, 5, 3, true)]
            [InlineData(100.123, 6, 3, false)]
            public void HasExpectedResultOrThrows(decimal d, int precision, int scale, bool shouldThrow)
            {
                var builder = new Decimal128Array.Builder(new Decimal128Type(precision, scale));

                if (shouldThrow)
                {
                    Assert.Throws<OverflowException>(() => builder.Append(d));
                }
                else
                {
                    builder.Append(d);
                    var result = builder.Build(new TestMemoryAllocator());
                    Assert.Equal(d, result.GetValue(0));
                }
            }

            [Theory]
            [InlineData(4.56, 38, 9, false)]
            [InlineData(7.89, 76, 38, false)]
            public void Decimal256HasExpectedResultOrThrows(decimal d, int precision, int scale, bool shouldThrow)
            {
                var builder = new Decimal256Array.Builder(new Decimal256Type(precision, scale));
                builder.Append(d);
                Decimal256Array result = builder.Build(new TestMemoryAllocator()); ;

                if (shouldThrow)
                {
                    Assert.Throws<OverflowException>(() => result.GetValue(0));
                }
                else
                {
                    Assert.Equal(d, result.GetValue(0));
                }
            }
        }

        public class SqlDecimals
        {
            [Fact]
            public void NegativeSqlDecimal()
            {
                const int precision = 38;
                const int scale = 0;
                const int bitWidth = 16;

                var negative = new SqlDecimal(precision, scale, false, 0, 0, 1, 0);
                var bytes = new byte[16];
                DecimalUtility.GetBytes(negative.Value, precision, scale, bitWidth, bytes);
                var sqlNegative = DecimalUtility.GetSqlDecimal128(new ArrowBuffer(bytes), 0, precision, scale);
                Assert.Equal(negative, sqlNegative);

                DecimalUtility.GetBytes(sqlNegative, precision, scale, bytes);
                var decimalNegative = DecimalUtility.GetDecimal(new ArrowBuffer(bytes), 0, scale, bitWidth);
                Assert.Equal(negative.Value, decimalNegative);
            }

            [Fact]
            public void LargeScale()
            {
                string digits = "1.2345678901234567890123456789012345678";

                var positive = SqlDecimal.Parse(digits);
                Assert.Equal(38, positive.Precision);
                Assert.Equal(37, positive.Scale);

                var bytes = new byte[16];
                DecimalUtility.GetBytes(positive, positive.Precision, positive.Scale, bytes);
                var sqlPositive = DecimalUtility.GetSqlDecimal128(new ArrowBuffer(bytes), 0, positive.Precision, positive.Scale);

                Assert.Equal(positive, sqlPositive);
                Assert.Equal(digits, sqlPositive.ToString());

                digits = "-" + digits;
                var negative = SqlDecimal.Parse(digits);
                Assert.Equal(38, positive.Precision);
                Assert.Equal(37, positive.Scale);

                DecimalUtility.GetBytes(negative, negative.Precision, negative.Scale, bytes);
                var sqlNegative = DecimalUtility.GetSqlDecimal128(new ArrowBuffer(bytes), 0, negative.Precision, negative.Scale);

                Assert.Equal(negative, sqlNegative);
                Assert.Equal(digits, sqlNegative.ToString());
            }
        }

        public class Strings
        {
            [Theory]
            [InlineData(100.12, 10, 2, "100.12")]
            [InlineData(100.12, 8, 3, "100.120")]
            [InlineData(100.12, 7, 4, "100.1200")]
            [InlineData(.12, 6, 3, "0.120")]
            [InlineData(.0012, 5, 4, "0.0012")]
            [InlineData(-100.12, 10, 2, "-100.12")]
            [InlineData(-100.12, 8, 3, "-100.120")]
            [InlineData(-100.12, 7, 4, "-100.1200")]
            [InlineData(-.12, 6, 3, "-0.120")]
            [InlineData(-.0012, 5, 4, "-0.0012")]
            [InlineData(7.89, 76, 38, "7.89000000000000000000000000000000000000")]
            public void FromDecimal(decimal d, int precision, int scale, string result)
            {
                if (precision <= 38)
                {
                    TestFromDecimal(d, precision, scale, 16, result);
                }
                TestFromDecimal(d, precision, scale, 32, result);
            }

            private void TestFromDecimal(decimal d, int precision, int scale, int byteWidth, string result)
            {
                var bytes = new byte[byteWidth];
                DecimalUtility.GetBytes(d, precision, scale, byteWidth, bytes);
                Assert.Equal(result, DecimalUtility.GetString(new ArrowBuffer(bytes), 0, precision, scale, byteWidth));
            }

            [Theory]
            [InlineData("100.12", 10, 2, "100.12")]
            [InlineData("100.12", 8, 3, "100.120")]
            [InlineData("100.12", 7, 4, "100.1200")]
            [InlineData(".12", 6, 3, "0.120")]
            [InlineData(".0012", 5, 4, "0.0012")]
            [InlineData("-100.12", 10, 2, "-100.12")]
            [InlineData("-100.12", 8, 3, "-100.120")]
            [InlineData("-100.12", 7, 4, "-100.1200")]
            [InlineData("-.12", 6, 3, "-0.120")]
            [InlineData("-.0012", 5, 4, "-0.0012")]
            [InlineData("+.0012", 5, 4, "0.0012")]
            [InlineData("99999999999999999999999999999999999999", 38, 0, "99999999999999999999999999999999999999")]
            [InlineData("-99999999999999999999999999999999999999", 38, 0, "-99999999999999999999999999999999999999")]
            public void FromString(string s, int precision, int scale, string result)
            {
                TestFromString(s, precision, scale, 16, result);
                TestFromString(s, precision, scale, 32, result);
            }

            [Fact]
            public void ThroughDecimal256()
            {
                var seventysix = new string('9', 76);
                TestFromString(seventysix, 76, 0, 32, seventysix);
                TestFromString("0000" + seventysix, 76, 0, 32, seventysix);

                seventysix = "-" + seventysix;
                TestFromString(seventysix, 76, 0, 32, seventysix);

                var seventyseven = new string('9', 77);
                Assert.Throws<OverflowException>(() => TestFromString(seventyseven, 76, 0, 32, seventyseven));
            }

            private void TestFromString(string s, int precision, int scale, int byteWidth, string result)
            {
                var bytes = new byte[byteWidth];
                DecimalUtility.GetBytes(s, precision, scale, byteWidth, bytes);
                Assert.Equal(result, DecimalUtility.GetString(new ArrowBuffer(bytes), 0, precision, scale, byteWidth));
            }

            [Theory]
            [InlineData("", 10, 2, 16, typeof(ArgumentException))]
            [InlineData("", 10, 2, 32, typeof(ArgumentException))]
            [InlineData(null, 10, 2, 32, typeof(ArgumentException))]
            [InlineData("1.23", 10, 1, 16, typeof(OverflowException))]
            [InlineData("12345678901234567890", 24, 1, 8, typeof(OverflowException))]
            [InlineData("abc", 24, 1, 8, typeof(ArgumentException))]
            public void ParseErrors(string s, int precision, int scale, int byteWidth, Type exceptionType)
            {
                byte[] bytes = new byte[byteWidth];
                Assert.Throws(exceptionType, () => DecimalUtility.GetBytes(s, precision, scale, byteWidth, bytes));
            }
        }

        /// <summary>
        /// Covers writing a <see cref="decimal"/> to a decimal array's value buffer, in particular the
        /// boundaries where the value stops fitting 32, 64 or 128 bits.
        /// </summary>
        public class Writing
        {
            private const int ByteWidth = 16;

            public static readonly TheoryData<int, int, string> Values = new TheoryData<int, int, string>
            {
                { 38, 0, "0" },
                { 38, 0, "1" },
                { 38, 0, "-1" },
                { 38, 0, "9223372036854775807" },   // long.MaxValue
                { 38, 0, "9223372036854775808" },
                { 38, 0, "-9223372036854775808" },  // long.MinValue
                { 38, 0, "-9223372036854775809" },
                { 38, 0, "18446744073709551616" },  // 2^64
                { 38, 0, "79228162514264337593543950335" },   // decimal.MaxValue
                { 38, 0, "-79228162514264337593543950335" },  // decimal.MinValue
                { 38, 28, "7.9228162514264337593543950335" },
                { 38, 28, "-7.9228162514264337593543950335" },
                { 38, 10, "123456789.0123456789" },
                { 20, 4, "1234567890123456.7890" },
                { 20, 4, "-1234567890123456.7890" },
                { 38, 20, "1.5" },                  // padded out by 19 digits
                { 38, 37, "1.5" },                  // padded out to the widest scale a decimal128 holds
            };

            [Theory]
            [MemberData(nameof(Values))]
            public void RoundTripsThroughDecimal(int precision, int scale, string text)
            {
                decimal value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

                byte[] bytes = new byte[ByteWidth];
                DecimalUtility.GetBytes(value, precision, scale, ByteWidth, bytes);

                Assert.Equal(value, DecimalUtility.GetDecimal(new ArrowBuffer(bytes), 0, scale, ByteWidth));
            }

            /// <summary>
            /// A decimal256 holds the same two's complement value as a decimal128, sign extended, so the
            /// two conversions have to agree over the bytes they share.
            /// </summary>
            [Theory]
            [MemberData(nameof(Values))]
            public void MatchesTheWiderConversion(int precision, int scale, string text)
            {
                decimal value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

                byte[] narrow = new byte[ByteWidth];
                DecimalUtility.GetBytes(value, precision, scale, ByteWidth, narrow);

                byte[] wide = new byte[32];
                DecimalUtility.GetBytes(value, precision, scale, 32, wide);

                Assert.Equal(narrow, wide.AsSpan(0, ByteWidth).ToArray());
                Assert.Equal(value < 0 ? (byte)0xFF : (byte)0, wide[31]);
            }

            public static readonly TheoryData<int, int, int, string> NarrowValues = new TheoryData<int, int, int, string>
            {
                { 4, 9, 0, "0" },
                { 4, 9, 0, "999999999" },
                { 4, 9, 0, "-999999999" },
                { 4, 9, 4, "12345.6789" },
                { 4, 9, 4, "-12345.6789" },
                { 4, 9, 9, "0.123456789" },
                { 4, 9, 8, "1.5" },                 // padded out by seven digits
                { 8, 18, 0, "999999999999999999" },
                { 8, 18, 0, "-999999999999999999" },
                { 8, 18, 6, "123456789012.345678" },
                { 8, 18, 17, "1.5" },
                { 8, 19, 0, "9223372036854775807" },   // long.MaxValue
                { 8, 19, 0, "-9223372036854775808" },  // long.MinValue
                { 32, 76, 0, "0" },
                { 32, 76, 0, "79228162514264337593543950335" },
                { 32, 76, 0, "-79228162514264337593543950335" },
                { 32, 76, 28, "7.9228162514264337593543950335" },
                { 32, 76, 38, "1.5" },              // padded past what a decimal128 could hold
                { 32, 76, 38, "-1.5" },
            };

            [Theory]
            [MemberData(nameof(NarrowValues))]
            public void RoundTripsAtEveryWidth(int byteWidth, int precision, int scale, string text)
            {
                decimal value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

                byte[] bytes = new byte[byteWidth];
                DecimalUtility.GetBytes(value, precision, scale, byteWidth, bytes);

                Assert.Equal(value, DecimalUtility.GetDecimal(new ArrowBuffer(bytes), 0, scale, byteWidth));
            }

            [Theory]
            [InlineData(4, "2147483648")]            // int.MaxValue + 1
            [InlineData(4, "-2147483649")]           // int.MinValue - 1
            [InlineData(8, "9223372036854775808")]   // long.MaxValue + 1
            [InlineData(8, "-9223372036854775809")]  // long.MinValue - 1
            public void ValuesTooWideForTheTypeThrow(int byteWidth, string text)
            {
                // The precision is deliberately generous, so that the byte width is what rejects the value.
                // A decimal's mantissa is only 96 bits, so decimal128 can only be overflowed by padding,
                // which PaddingBeyondOneHundredAndTwentyEightBitsThrows covers.
                decimal value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

                byte[] bytes = new byte[byteWidth];
                Assert.Throws<OverflowException>(() => DecimalUtility.GetBytes(value, 39, 0, byteWidth, bytes));
            }

            /// <summary>
            /// A decimal256 whose padded value needs more than 128 bits is written through BigInteger, which
            /// is the path that reads the cached powers of ten rather than the 128-bit tables.
            /// </summary>
            [Theory]
            [InlineData("1234.5678901234")]
            [InlineData("-1234.5678901234")]
            [InlineData("0.0000000001")]
            [InlineData("79228162514264337593543950335")]
            [InlineData("-79228162514264337593543950335")]
            public void RoundTripsBeyondOneHundredAndTwentyEightBits(string text)
            {
                const int byteWidth = 32;
                const int precision = 76;
                const int scale = 38;

                decimal value = decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

                byte[] bytes = new byte[byteWidth];
                DecimalUtility.GetBytes(value, precision, scale, byteWidth, bytes);

                Assert.Equal(value, DecimalUtility.GetDecimal(new ArrowBuffer(bytes), 0, scale, byteWidth));
            }

            [Theory]
            [InlineData(4)]
            [InlineData(8)]
            [InlineData(16)]
            [InlineData(32)]
            public void PrecisionIsCheckedForNegativeValuesAtEveryWidth(int byteWidth)
            {
                byte[] bytes = new byte[byteWidth];
                Assert.Throws<OverflowException>(() => DecimalUtility.GetBytes(100.123m, 5, 3, byteWidth, bytes));
                Assert.Throws<OverflowException>(() => DecimalUtility.GetBytes(-100.123m, 5, 3, byteWidth, bytes));
            }

            [Fact]
            public void PaddingBeyondOneHundredAndTwentyEightBitsThrows()
            {
                // decimal.MaxValue padded out by another 20 digits needs 49 digits, far more than the
                // 39 a decimal128 can hold.
                byte[] bytes = new byte[ByteWidth];
                Assert.Throws<OverflowException>(
                    () => DecimalUtility.GetBytes(decimal.MaxValue, 38, 20, ByteWidth, bytes));
            }

            [Fact]
            public void PrecisionIsCheckedForNegativeValues()
            {
                byte[] bytes = new byte[ByteWidth];
                Assert.Throws<OverflowException>(() => DecimalUtility.GetBytes(100.123m, 5, 3, ByteWidth, bytes));
                Assert.Throws<OverflowException>(() => DecimalUtility.GetBytes(-100.123m, 5, 3, ByteWidth, bytes));
            }

            [Fact]
            public void PrecisionIsMeasuredBeforePadding()
            {
                // 100.123 has six significant digits, so it fits a precision of six even though writing it
                // at a scale of four produces the seven digit value 1001230.
                byte[] bytes = new byte[ByteWidth];
                DecimalUtility.GetBytes(100.123m, 6, 4, ByteWidth, bytes);
                Assert.Equal(100.123m, DecimalUtility.GetDecimal(new ArrowBuffer(bytes), 0, 4, ByteWidth));

                DecimalUtility.GetBytes(-100.123m, 6, 4, ByteWidth, bytes);
                Assert.Equal(-100.123m, DecimalUtility.GetDecimal(new ArrowBuffer(bytes), 0, 4, ByteWidth));
            }

            [Fact]
            public void ScaleSmallerThanTheValuesThrows()
            {
                byte[] bytes = new byte[ByteWidth];
                Assert.Throws<OverflowException>(() => DecimalUtility.GetBytes(100.123m, 10, 2, ByteWidth, bytes));
            }
        }
    }
}
