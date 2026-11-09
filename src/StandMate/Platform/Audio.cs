using System;
using System.IO;

namespace StandMate.Platform
{
    /// <summary>
    /// Plays WAV/MP3/WMA through Windows' built-in MCI (winmm) - no audio libraries, no extra RAM.
    /// Must be used from the UI thread.
    /// </summary>
    internal static class Audio
    {
        private const string Alias = "standmate_snd";
        private static bool _open;

        /// <param name="notifyWindow">If set, the window receives MM_MCINOTIFY when playback finishes.</param>
        public static bool Play(string path, int volume0to100, IntPtr notifyWindow = default)
        {
            Stop();
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || volume0to100 <= 0) return false;

            // "mpegvideo" (DirectShow) handles wav, mp3 and wma, and supports volume.
            if (Send($"open \"{path}\" type mpegvideo alias {Alias}") != 0) return false;
            _open = true;
            int vol = Math.Max(0, Math.Min(1000, volume0to100 * 10));
            Send($"setaudio {Alias} volume to {vol}");
            string notify = notifyWindow != IntPtr.Zero ? " notify" : "";
            return Send($"play {Alias} from 0{notify}", notifyWindow) == 0;
        }

        /// <summary>Plays the already-open sound again from the start (used to repeat).</summary>
        public static void Replay(IntPtr notifyWindow)
        {
            if (!_open) return;
            Send($"play {Alias} from 0 notify", notifyWindow);
        }

        public static void Stop()
        {
            if (!_open) return;
            Send($"stop {Alias}");
            Send($"close {Alias}");
            _open = false;
        }

        private static int Send(string cmd, IntPtr hwnd = default) => Native.mciSendString(cmd, null, 0, hwnd);
    }
}
