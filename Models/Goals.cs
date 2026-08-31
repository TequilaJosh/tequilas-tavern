using System.Collections.Generic;

namespace GameTracker.Models
{
    /// <summary>A streamer goal shown as a progress bar on the overlay.</summary>
    public class StreamGoal
    {
        public string Name { get; set; } = string.Empty;   // e.g. "Follower goal"
        public int Current { get; set; }
        public int Target { get; set; } = 100;
        public string Color { get; set; } = "#9fb4ff";     // bar fill colour
        public bool Show { get; set; } = true;             // include on the overlay
    }
}
