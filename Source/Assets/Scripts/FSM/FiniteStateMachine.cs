using FishSwarm.Behavior;

namespace FishSwarm.FSM
{
    /// <summary>
    /// Facts gathered from the simulation for one fish's behaviour decision.
    /// Kept as a blittable value type so it can be used from Burst jobs.
    /// </summary>
    public struct FishStateInputs
    {
        // Byte flags are used instead of bools so the inputs stay simple job-friendly values.
        public byte PredatorInRange;
        public float NearestNeighborDistance;
        public byte NearForagingNode;
        public float RandomValue;
    }

    /// <summary>
    /// Tunable thresholds used by the fish finite state machine.
    /// These values are copied from FishConfig into SteeringParams each frame.
    /// </summary>
    public struct FishStateSettings
    {
        public float RegroupNeighborDistance;
        public float ForagingChance;
        public float ForagingDuration;
    }

    /// <summary>
    /// Data-oriented finite state machine for fish behaviour.
    /// It decides only the behaviour state; steering forces decide movement.
    /// </summary>
    public static class FiniteStateMachine
    {
        public static FishState Tick(
            FishState current,
            FishStateInputs input,
            FishStateSettings settings,
            float tickDt,
            ref float stateTimer)
        {
            // Predator pressure has top priority. Any fish can be forced into Fleeing
            // regardless of what it was doing before.
            if (input.PredatorInRange != 0)
                return Enter(current, FishState.Fleeing, ref stateTimer);

            switch (current)
            {
                case FishState.Fleeing:
                    // Once the predator is no longer in range, the fish tries to rejoin the group.
                    return Enter(current, FishState.Regrouping, ref stateTimer);

                case FishState.Regrouping:
                    // Regrouping ends when the fish can "see" a close enough neighbour again.
                    // Blind-angle filtering affects this distance before it reaches the FSM.
                    if (input.NearestNeighborDistance < settings.RegroupNeighborDistance)
                        return Enter(current, FishState.Schooling, ref stateTimer);
                    return FishState.Regrouping;

                case FishState.Foraging:
                    // Foraging is temporary: leave when the timer expires or the feeding node is gone.
                    stateTimer += tickDt;
                    if (stateTimer > settings.ForagingDuration || input.NearForagingNode == 0)
                        return Enter(current, FishState.Schooling, ref stateTimer);
                    return FishState.Foraging;

                case FishState.Schooling:
                default:
                    // Schooling fish only break off to forage when near food and the random roll passes.
                    if (input.NearForagingNode != 0 && input.RandomValue < settings.ForagingChance)
                        return Enter(current, FishState.Foraging, ref stateTimer);
                    return FishState.Schooling;
            }
        }

        static FishState Enter(FishState current, FishState next, ref float stateTimer)
        {
            // Reset timers only on actual state changes; staying in the same state preserves duration.
            if (current != next)
                stateTimer = 0f;
            return next;
        }
    }
}
