using System;
using System.Collections.Generic;
using StandMate.Core;

namespace StandMate.UI
{
    /// <summary>What the settings window needs from the running app.</summary>
    internal interface IAppHost
    {
        Settings Settings { get; }
        IReadOnlyList<Mate> Mates { get; }
        Mate ActiveMate { get; }

        /// <summary>Persist settings; when the schedule changed, restart the current phase with it.</summary>
        void SettingsChanged(bool scheduleChanged);

        void SetActiveMate(string id);
        void ReloadMates();
        void Preview(Mate mate, Phase phase);
        void StartPlacement(Action done);
        void TestSound(Mate mate, Phase phase);
        string StatusText();

        /// <summary>"Check for updates" state, shared so it survives the settings window being closed.</summary>
        UpdateController Updater { get; }
    }
}
