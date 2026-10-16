using System;
namespace SmartHdrManager;
public enum StreamState { Unknown, Idle, Active }
public static class StreamingEvidence
{
    // A complete host-start marker establishes the beginning of this log epoch.
    // Missing startup, truncated log, unbalanced disconnects or unreadable logs fail closed.
    public static StreamState Parse(string text)
    {
        int startup = text.LastIndexOf("Configuration UI available at", StringComparison.Ordinal);
        if (startup < 0) return StreamState.Unknown;
        int clients = 0;
        foreach (var line in text[startup..].Split('\n')) {
            if (line.Contains("CLIENT CONNECTED", StringComparison.Ordinal)) clients++;
            if (line.Contains("CLIENT DISCONNECTED", StringComparison.Ordinal)) {
                if (clients == 0) return StreamState.Unknown;
                clients--;
            }
        }
        return clients > 0 ? StreamState.Active : StreamState.Idle;
    }
}
