namespace Gelatinarm.Music
{
    public static class MusicConstants
    {
        public const int MediaSourceClearDelayMs = 500;

        // The console refuses a FLAC file whose audio starts further in than about 4 MB, which a
        // large embedded cover causes: refused at 4.2 MB and above, played at 3.3 MB, whatever
        // the cover's size in pixels (tested on Xbox Series X). MP3 and M4A files played with
        // 9 MB and 5 MB covers. Such a file skips the direct attempt.
        public const int MaxFlacBytesBeforeAudio = 4000000;

        // Reading where a FLAC file's audio starts: the metadata block headers sit at the start
        // of the file, and one read reaches past everything but a cover
        public const int FlacHeaderReadBytes = 65536;
        public const int MaxFlacHeaderReads = 4;
        public const int FlacHeaderReadTimeoutSeconds = 3;

        public const int MaxDiscoveryQueryLimit = 100;

        /// <summary>
        ///     How long sign-out or a profile switch waits for the music "playback stopped"
        ///     report before carrying on, so an unreachable server cannot stall them
        /// </summary>
        public const int StopReportBeforeUserChangeTimeoutMs = 3000;

        public const int MiniPlayerUpdateIntervalMs = 500;
    }
}
