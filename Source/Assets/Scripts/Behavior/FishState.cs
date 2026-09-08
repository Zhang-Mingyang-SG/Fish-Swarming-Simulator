namespace FishSwarm.Behavior
{
    /// <summary>
    /// Per-fish behavioural state. It only governs which steering weights are active — the school
    /// splitting and reforming is emergent from those weight changes plus the threat force, and is
    /// deliberately not scripted anywhere.
    /// </summary>
    public enum FishState
    {
        Schooling = 0,
        Fleeing = 1,
        Regrouping = 2,
        Foraging = 3,
    }
}
