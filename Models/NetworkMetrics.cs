using System;

namespace Gelatinarm.Models
{
    public enum ConnectionType
    {
        None,
        Unknown,
        Ethernet,
        WiFi,
        Cellular
    }

    public class NetworkMetrics
    {
        public bool IsConnected { get; set; }
        public string NetworkName { get; set; }
        public double TransferRate { get; set; } // MB/s
        public double Latency { get; set; } // Default value in ms
        public byte SignalStrength { get; set; }
        public ConnectionType ConnectionType { get; set; } = ConnectionType.Unknown;
        public bool IsMetered { get; set; }
        public ulong BytesSent { get; set; }
        public ulong BytesReceived { get; set; }
        public DateTimeOffset LastUpdated { get; set; }

        // Additional properties used in services
        public DateTime Timestamp { get; set; }
        public bool IsAvailable { get; set; }
        public double Bandwidth { get; set; } // Mbps
        public byte QualityLevel { get; set; } // 0-100
    }
}
