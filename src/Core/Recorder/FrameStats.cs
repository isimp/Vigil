using System;
using System.Globalization;

namespace Vigil.Core
{
    /// <summary>
    /// Frame-time distribution for a whole session in constant memory: a histogram with 0.5 ms
    /// buckets up to 5 s, plus the exact maximum. Percentiles are accurate to a bucket.
    /// </summary>
    public sealed class FrameStats
    {
        private const float Bucket = 0.5f;
        private const int Buckets = 10000;
        private readonly long[] _hist = new long[Buckets + 1];
        private long _count;
        private double _sum;
        private float _max;

        public long Count => _count;
        public float Max => _max;
        public double MeanMs => _count > 0 ? _sum / _count : 0;

        public void Add(float ms)
        {
            if (ms < 0 || float.IsNaN(ms)) return;
            int b = (int)(ms / Bucket);
            _hist[b > Buckets ? Buckets : b]++;
            _count++; _sum += ms;
            if (ms > _max) _max = ms;
        }

        public float Percentile(double p)
        {
            if (_count == 0) return 0;
            long rank = (long)Math.Ceiling(p / 100.0 * _count);
            if (rank < 1) rank = 1;
            long seen = 0;
            for (int i = 0; i <= Buckets; i++)
            {
                seen += _hist[i];
                if (seen >= rank) return i == Buckets ? _max : Math.Min(_max, i * Bucket);
            }
            return _max;
        }

        public long CountAbove(float ms)
        {
            int first = (int)(ms / Bucket) + 1;
            long n = 0;
            for (int i = Math.Max(0, first); i <= Buckets; i++) n += _hist[i];
            return n;
        }

        public double ShareAbove(float ms) => _count > 0 ? (double)CountAbove(ms) / _count : 0;

        public string Describe()
        {
            var ci = CultureInfo.InvariantCulture;
            double mean = MeanMs;
            return string.Format(ci,
                "{0} frames, mean {1:0.0} ms ({2:0} fps), median {3:0.#} ms, 95% under {4:0.#} ms, 99% under {5:0.#} ms, worst {6:0} ms, over 50 ms: {7:0.0}%, over 100 ms: {8}",
                _count, mean, mean > 0 ? 1000 / mean : 0, Percentile(50), Percentile(95), Percentile(99), _max,
                ShareAbove(50) * 100, CountAbove(100));
        }
    }
}
