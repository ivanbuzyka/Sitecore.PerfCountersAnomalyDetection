using System;
using System.Collections.Generic;
using System.Text;

namespace PrivateBytesPerfCounterAnomalies.Domain.AnomalyDetection
{
    public class AndetDataCollection
    {
        public string Granularity { get; set; }

        public List<PerfCounterRow> Series { get; set; }
    }
}
