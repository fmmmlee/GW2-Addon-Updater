using GW2AddonManager.Localization;
using System;
using System.Collections.Generic;

namespace GW2AddonManager
{
    [Serializable]
    public record Configuration(
        bool LaunchGame,
        string Culture,
        string GamePath)
    {
        public static Configuration Default => new Configuration(false, CultureConstants.English, null);
    }
}