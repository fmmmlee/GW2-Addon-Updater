using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace GW2AddonManager
{
    [JsonObject]
    public record AddonState(string Path, string Nickname, SemanticVersion Version, bool Disabled);
}