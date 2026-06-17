using UnityEngine;

namespace Goliath.Addons
{
    /// <summary>
    /// Shared "a media overlay consumed a tap this frame" marker. Overlays (hub, video player) call
    /// <see cref="MarkConsumed"/> when they handle a tap so a player (e.g. the reader) can skip that
    /// same tap regardless of Update order.
    /// </summary>
    public static class MediaInput
    {
        public static int ConsumedFrame { get; private set; } = -1;

        public static void MarkConsumed()
        {
            ConsumedFrame = Time.frameCount;
        }
    }
}
