using System;
using System.Collections.Generic;
using System.Text;

namespace PrivateBytesPerfCounterAnomalies.Domain.AnomalyDetection
{
    public class AndetResponseData
    {
        public double[] ExpectedValues { get; set; }
        public bool[] IsAnomaly { get; set; }
        public bool[] IsNegativeAnomaly { get; set; }
        public bool[] IsPositiveAnomaly { get; set; }
        public double[] LowerMargins { get; set; }
        public double[] UpperMargins { get; set; }
        public int Period { get; set; }
    }
}
