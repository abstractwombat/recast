using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Recast.WindowsRecorder.Services
{
    public class FrameDropDetector
    {
        private readonly ILogger _log;
        private readonly List<FrameMetrics> _metricsHistory = new List<FrameMetrics>();
        private readonly object _lock = new object();
        private DateTimeOffset _lastLogTime = DateTimeOffset.MinValue;
        private int _lastDup = 0;
        private int _lastDrop = 0;
        private DateTimeOffset _lastMetricTime = DateTimeOffset.UtcNow;

        public FrameDropDetector(ILogger log)
        {
            _log = log;
        }

        public bool ProcessFFmpegLine(string line, int thresholdPerSecond, out int dupRate, out int dropRate)
        {
            dupRate = 0;
            dropRate = 0;

            var match = Regex.Match(line, @"frame=\s*(\d+).*?dup=(\d+)\s+drop=(\d+)");
            if (!match.Success)
                return false;

            var frameCount = int.Parse(match.Groups[1].Value);
            var dup = int.Parse(match.Groups[2].Value);
            var drop = int.Parse(match.Groups[3].Value);
            var now = DateTimeOffset.UtcNow;

            lock (_lock)
            {
                var dupDelta = dup - _lastDup;
                var dropDelta = drop - _lastDrop;
                var timeDelta = (now - _lastMetricTime).TotalSeconds;

                if (timeDelta > 0 && _lastDup > 0)
                {
                    dupRate = (int)(dupDelta / timeDelta);
                    dropRate = (int)(dropDelta / timeDelta);

                    var metrics = new FrameMetrics
                    {
                        Timestamp = now,
                        FrameCount = frameCount,
                        TotalDup = dup,
                        TotalDrop = drop,
                        DupDelta = dupDelta,
                        DropDelta = dropDelta,
                        DupRate = dupRate,
                        DropRate = dropRate,
                        TimeDelta = timeDelta
                    };

                    _metricsHistory.Add(metrics);
                    if (_metricsHistory.Count > 100)
                        _metricsHistory.RemoveAt(0);

                    if ((now - _lastLogTime).TotalSeconds >= 5)
                    {
                        _log.LogInformation(
                            "[FrameDropDetector] frame={Frame} dup={Dup}(+{DupDelta}, {DupRate}/s) drop={Drop}(+{DropDelta}, {DropRate}/s)",
                            frameCount, dup, dupDelta, dupRate, drop, dropDelta, dropRate);
                        _lastLogTime = now;
                    }

                    if (dupRate > thresholdPerSecond || dropRate > thresholdPerSecond)
                    {
                        _log.LogWarning(
                            "[FrameDropDetector] CHOPPY STREAM DETECTED! dupRate={DupRate}/s dropRate={DropRate}/s threshold={Threshold}/s",
                            dupRate, dropRate, thresholdPerSecond);
                        return true;
                    }
                }

                _lastDup = dup;
                _lastDrop = drop;
                _lastMetricTime = now;
            }

            return false;
        }

        public void Reset()
        {
            lock (_lock)
            {
                _metricsHistory.Clear();
                _lastDup = 0;
                _lastDrop = 0;
                _lastMetricTime = DateTimeOffset.UtcNow;
                _lastLogTime = DateTimeOffset.MinValue;
            }
        }

        public FrameMetricsSummary GetSummary()
        {
            lock (_lock)
            {
                if (_metricsHistory.Count == 0)
                    return new FrameMetricsSummary();

                return new FrameMetricsSummary
                {
                    TotalSamples = _metricsHistory.Count,
                    AverageDupRate = _metricsHistory.Average(m => m.DupRate),
                    AverageDropRate = _metricsHistory.Average(m => m.DropRate),
                    MaxDupRate = _metricsHistory.Max(m => m.DupRate),
                    MaxDropRate = _metricsHistory.Max(m => m.DropRate),
                    CurrentTotalDup = _lastDup,
                    CurrentTotalDrop = _lastDrop
                };
            }
        }

        private class FrameMetrics
        {
            public DateTimeOffset Timestamp { get; set; }
            public int FrameCount { get; set; }
            public int TotalDup { get; set; }
            public int TotalDrop { get; set; }
            public int DupDelta { get; set; }
            public int DropDelta { get; set; }
            public int DupRate { get; set; }
            public int DropRate { get; set; }
            public double TimeDelta { get; set; }
        }

        public class FrameMetricsSummary
        {
            public int TotalSamples { get; set; }
            public double AverageDupRate { get; set; }
            public double AverageDropRate { get; set; }
            public int MaxDupRate { get; set; }
            public int MaxDropRate { get; set; }
            public int CurrentTotalDup { get; set; }
            public int CurrentTotalDrop { get; set; }
        }
    }
}
