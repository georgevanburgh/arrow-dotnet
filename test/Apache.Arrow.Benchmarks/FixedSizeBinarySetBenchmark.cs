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
using Apache.Arrow.Types;
using BenchmarkDotNet.Attributes;

namespace Apache.Arrow.Benchmarks
{
    /// <summary>
    /// Measures FixedSizeBinaryArray.BuilderBase.Set. The SetBytes benchmarks pass values that are already
    /// bytes, so that the copy into the value buffer is all that is timed; the SetDecimal benchmarks go
    /// through the same copy with the decimal conversion in front of it.
    /// </summary>
    [MemoryDiagnoser]
    public class FixedSizeBinarySetBenchmark
    {
        [Params(10_000)]
        public int Count { get; set; }

        private byte[] _bytes4;
        private byte[] _bytes8;
        private byte[] _bytes16;
        private byte[] _bytes32;
        private decimal[] _values;

        private Decimal32Array.Builder _builder4;
        private Decimal64Array.Builder _builder8;
        private Decimal128Array.Builder _builder16;
        private Decimal256Array.Builder _builder32;

        [GlobalSetup]
        public void GlobalSetup()
        {
            var random = new Random(42);

            _bytes4 = new byte[4];
            _bytes8 = new byte[8];
            _bytes16 = new byte[16];
            _bytes32 = new byte[32];
            random.NextBytes(_bytes4);
            random.NextBytes(_bytes8);
            random.NextBytes(_bytes16);
            random.NextBytes(_bytes32);

            _values = new decimal[Count];
            for (int i = 0; i < Count; i++)
            {
                _values[i] = (decimal)Math.Round(random.NextDouble() * 10000, 4);
            }

            _builder4 = new Decimal32Array.Builder(new Decimal32Type(9, 4)).Resize(Count);
            _builder8 = new Decimal64Array.Builder(new Decimal64Type(18, 4)).Resize(Count);
            _builder16 = new Decimal128Array.Builder(new Decimal128Type(38, 4)).Resize(Count);
            _builder32 = new Decimal256Array.Builder(new Decimal256Type(76, 4)).Resize(Count);
        }

        [Benchmark]
        public Decimal32Array.Builder SetBytes_4()
        {
            Decimal32Array.Builder builder = _builder4;
            for (int i = 0; i < Count; i++)
            {
                builder.Set(i, _bytes4);
            }
            return builder;
        }

        [Benchmark]
        public Decimal64Array.Builder SetBytes_8()
        {
            Decimal64Array.Builder builder = _builder8;
            for (int i = 0; i < Count; i++)
            {
                builder.Set(i, _bytes8);
            }
            return builder;
        }

        [Benchmark]
        public Decimal128Array.Builder SetBytes_16()
        {
            Decimal128Array.Builder builder = _builder16;
            for (int i = 0; i < Count; i++)
            {
                builder.Set(i, _bytes16);
            }
            return builder;
        }

        [Benchmark]
        public Decimal256Array.Builder SetBytes_32()
        {
            Decimal256Array.Builder builder = _builder32;
            for (int i = 0; i < Count; i++)
            {
                builder.Set(i, _bytes32);
            }
            return builder;
        }

        [Benchmark]
        public Decimal32Array.Builder SetDecimal_4()
        {
            Decimal32Array.Builder builder = _builder4;
            for (int i = 0; i < Count; i++)
            {
                builder.Set(i, _values[i]);
            }
            return builder;
        }

        [Benchmark]
        public Decimal64Array.Builder SetDecimal_8()
        {
            Decimal64Array.Builder builder = _builder8;
            for (int i = 0; i < Count; i++)
            {
                builder.Set(i, _values[i]);
            }
            return builder;
        }

        [Benchmark]
        public Decimal128Array.Builder SetDecimal_16()
        {
            Decimal128Array.Builder builder = _builder16;
            for (int i = 0; i < Count; i++)
            {
                builder.Set(i, _values[i]);
            }
            return builder;
        }

        [Benchmark]
        public Decimal256Array.Builder SetDecimal_32()
        {
            Decimal256Array.Builder builder = _builder32;
            for (int i = 0; i < Count; i++)
            {
                builder.Set(i, _values[i]);
            }
            return builder;
        }
    }
}
