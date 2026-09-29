using System;

namespace Gelatinarm.Player
{
    public class ResumeStuckException : InvalidOperationException
    {
        public ResumeStuckException()
            : base(
                "Unable to resume playback at the saved position. This media may have encoding issues that prevent proper seeking. You can try playing it from the beginning instead.")
        {
        }
    }
}
