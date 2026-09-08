using System;
using System.Collections.Generic;
using System.Linq;

namespace KillerScan.Services.SpeedTest
{
    public sealed class SpeedTestOptions
    {
        public Uri Endpoint { get; set; } = null!;
        public TimeSpan PhaseDuration { get; set; } = TimeSpan.FromSeconds(8);
        public TimeSpan WarmupDuration { get; set; } = TimeSpan.FromSeconds(2);
        public int MaximumStreams { get; set; } = 4;
        public long ByteBudgetPerPhase { get; set; } = 512L * 1024 * 1024;
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public int DownloadPayloadBytes { get; set; } = 8 * 1024 * 1024;
        public int UploadPayloadBytes { get; set; } = 4 * 1024 * 1024;
        public int IdleLatencySampleCount { get; set; } = 5;
    }

    public enum SpeedTestPhase { IdleLatency, DownloadWarmup, Download, UploadWarmup, Upload, Completed }
    public enum SpeedTestFailureKind { InvalidConfiguration, Timeout, EndpointUnavailable, TlsFailure, HttpError, RateLimited, InvalidResponse, TransferFailed }

    public sealed class SpeedTestException : Exception
    {
        public SpeedTestFailureKind Kind { get; }
        public int? StatusCode { get; }
        public SpeedTestException(SpeedTestFailureKind kind, string message, Exception? innerException = null, int? statusCode = null)
            : base(message, innerException) { Kind = kind; StatusCode = statusCode; }
    }

    public sealed class SpeedTestProgress
    {
        public SpeedTestPhase Phase { get; internal set; }
        public double? Mbps { get; internal set; }
        public long BytesTransferred { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }
        public double? LatencyMs { get; internal set; }
        public int ActiveStreams { get; internal set; }
    }

    public sealed class SpeedTestPhaseResult
    {
        public double? Mbps { get; internal set; }
        public long BytesTransferred { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }
        public long WarmupBytes { get; internal set; }
        public long BytesScheduled { get; internal set; }
        public int StreamCount { get; internal set; }
        public bool ByteBudgetReached { get; internal set; }
        public bool CompletedDuration { get; internal set; }
        public IReadOnlyList<double> LatencySamples { get; internal set; } = Array.Empty<double>();
        public double? LoadedLatencyMs => SpeedTestMetrics.Median(LatencySamples);
        public double? LoadedJitterMs => SpeedTestMetrics.Jitter(LatencySamples);
        public int FailedLatencySamples { get; internal set; }
    }

    public sealed class SpeedTestResult
    {
        public Uri Endpoint { get; internal set; } = null!;
        public DateTimeOffset StartedAt { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }
        public SpeedTestPhaseResult Download { get; internal set; } = null!;
        public SpeedTestPhaseResult Upload { get; internal set; } = null!;
        public IReadOnlyList<double> IdleLatencySamples { get; internal set; } = Array.Empty<double>();
        public double? IdleLatencyMs => SpeedTestMetrics.Median(IdleLatencySamples);
        public double? JitterMs => SpeedTestMetrics.Jitter(IdleLatencySamples);
    }

    /// <summary>HTTP round-trip latency includes endpoint processing. Jitter is the mean
    /// absolute difference between consecutive successful round trips, in milliseconds.</summary>
    public static class SpeedTestMetrics
    {
        public static double? MegabitsPerSecond(long bytes, TimeSpan elapsed) =>
            bytes > 0 && elapsed.TotalSeconds > 0 ? bytes * 8d / elapsed.TotalSeconds / 1000000d : null;

        public static double? Median(IEnumerable<double> samples)
        {
            var values = samples.Where(IsSample).OrderBy(value => value).ToArray();
            if (values.Length == 0) return null;
            int middle = values.Length / 2;
            return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
        }

        public static double? Jitter(IEnumerable<double> samples)
        {
            var values = samples.Where(IsSample).ToArray();
            if (values.Length < 2) return null;
            double difference = 0;
            for (int i = 1; i < values.Length; i++) difference += Math.Abs(values[i] - values[i - 1]);
            return difference / (values.Length - 1);
        }

        private static bool IsSample(double value) => value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
