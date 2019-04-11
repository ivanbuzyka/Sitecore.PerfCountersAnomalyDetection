using System;

namespace PrivateBytesPerfCounterAnomalies.Domain.AnomalyDetection
{
    public class PerfCounterRow
    {
        public DateTime Timestamp { get; set; }

        public long Value { get; set; }
    }
}