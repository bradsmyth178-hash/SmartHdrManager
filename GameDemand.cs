using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartHdrManager;
public record GameObservation(string Display, string Policy, bool Enabled);
public static class GameDemand
{
    public static bool? Decide(string selected, IEnumerable<GameObservation> games, bool remoteOrVirtual, bool paused, bool pending)
    {
        if (string.IsNullOrEmpty(selected) || remoteOrVirtual || paused) return null;
        var eligible = games.Where(g => g.Enabled && string.Equals(g.Display, selected, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (pending || eligible.Any(g => g.Policy is "Native HDR" or "Auto HDR")) return true;
        if (eligible.Any(g => g.Policy == "SDR")) return false;
        return null;
    }
}
