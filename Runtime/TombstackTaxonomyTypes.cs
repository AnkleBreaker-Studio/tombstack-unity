namespace AnkleBreaker.Tombstack
{
    /// <summary>Outcome of a progression step reported via <see cref="Tombstack.TrackProgression"/>
    /// (serialized as "start" / "fail" / "complete" on the <c>tmb.progression</c> event).</summary>
    public enum ProgressionStatus
    {
        /// <summary>The player entered the area / level / phase.</summary>
        Start,
        /// <summary>The attempt ended in failure (death, timeout, abandon).</summary>
        Fail,
        /// <summary>The attempt ended in success.</summary>
        Complete,
    }

    /// <summary>Direction of a virtual-economy movement reported via <see cref="Tombstack.TrackEconomy"/>
    /// (serialized as "source" / "sink" on the <c>tmb.economy</c> event).</summary>
    public enum ResourceFlow
    {
        /// <summary>Currency granted to the player (reward, drop, IAP grant).</summary>
        Source,
        /// <summary>Currency spent by the player (shop, upgrade, crafting fee).</summary>
        Sink,
    }
}
