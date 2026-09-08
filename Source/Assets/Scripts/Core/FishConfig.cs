using UnityEngine;
using FishSwarm.Behavior;

namespace FishSwarm.Core
{
    /// <summary>
    /// Tunable steering parameters shared by every fish. Lives as a ScriptableObject asset so
    /// the flocking feel can be dialled in from one place. Behaviour states (Fleeing, etc.)
    /// added later will scale these weights rather than replace them.
    /// </summary>
    [CreateAssetMenu(fileName = "FishConfig", menuName = "Fish Swarm/Fish Config")]
    public class FishConfig : ScriptableObject
    {
        [Header("Perception")]
        [Tooltip("Neighbour query radius for alignment and cohesion.")]
        public float perceptionRadius = 4f;
        [Tooltip("Closer-range personal-space radius for separation (<= perception).")]
        public float separationRadius = 1.5f;
        [Tooltip("Rear blind cone in degrees for alignment/cohesion only. 0 = disabled, 35 = subtle, 60 = strong, 90 = ignore the full rear hemisphere. Separation still works in every direction.")]
        [Range(0f, 179f)] public float rearBlindAngle = 35f;

        [Tooltip("Fish spawn within this radius of the volume centre so they start as one school.")]
        [Range(0f, 179f)] public float spawnRadius = 50f;

        [Header("Steering weights")]
        public float separationWeight = 1.6f;
        public float alignmentWeight = 1.0f;
        public float cohesionWeight = 1.0f;
        [Tooltip("How hard fish turn away from the volume edges.")]
        public float boundaryWeight = 2.0f;

        [Header("Simulation LOD (sim-only; render LOD is separate)")]
        [Tooltip("Within this distance of the camera, fish always run the full neighbour scan.")]
        public float lodNearDistance = 60f;
        [Tooltip("Between near and mid distance, fish steer every lodMidStride frames.")]
        public float lodMidDistance = 150f;
        [Min(1)] public int lodMidStride = 2;
        [Tooltip("Beyond mid distance, fish steer every lodFarStride frames (reusing last acceleration).")]
        [Min(1)] public int lodFarStride = 4;
        [Tooltip("Fish within this distance of ANY predator always run at full rate, so flee/split " +
                 "stays correct. Keep this comfortably above predatorFleeRadius.")]
        public float lodPredatorRadius = 32f;

        [Header("Anti-clipping (separation strength)")]
        [Tooltip("Scales the raw inverse-distance separation sum into force units. Separation " +
                 "magnitude grows as ~1/distance, so this is what makes crowded fish push apart " +
                 "harder than lonely ones. Higher = more personal space.")]
        public float separationGain = 4f;
        [Tooltip("Clamp for the separation force. Deliberately set ABOVE maxSteerForce so near-contact " +
                 "fish can push harder than normal steering allows — this is what stops interpenetration.")]
        public float maxSeparationForce = 40f;

        [Header("School shape")]
        [Tooltip("Per-axis cohesion stiffness. Equilibrium extent runs roughly as 1/weight, so a " +
                 "non-uniform vector makes the school ellipsoidal instead of spherical. Lower Y = " +
                 "taller school; higher Y = flatter. NOTE: the FishConfig *asset* overrides this " +
                 "default, so edit the asset (or this, if using CreateDefault). " +
                 "e.g. (1, 1.3, 0.75) => a gently flattened, slightly elongated school with height.")]
        public Vector3 cohesionAxisWeights = new Vector3(1f, 1.3f, 0.75f);

        [Header("Movement")]
        public float maxSpeed = 8f;
        [Tooltip("Fish never drop below this speed, so the school always looks alive.")]
        public float minSpeed = 2f;
        [Tooltip("Maximum magnitude of any single steering force (Reynolds clamp).")]
        public float maxSteerForce = 8f;

        [Header("Boundary")]
        [Tooltip("Distance from a volume face at which fish begin steering back inward.")]
        public float boundaryMargin = 6f;

        [Header("Performance")]
        [Tooltip("Max neighbours each fish considers. Boids only need a handful, so capping this " +
                 "bounds per-fish cost in dense schools (and lets the grid early-exit). The grid " +
                 "returns the nearest ones; naive keeps its full O(n²) scan so the benchmark " +
                 "contrast stays honest.")]
        [Min(1)] public int maxNeighbors = 32;

        [Header("Predator response")]
        [Tooltip("Fish flee when a predator is within this distance.")]
        public float predatorFleeRadius = 16f;
        [Tooltip("Strength of the threat repulsion. Kept well above the S/A/C weights so it wins.")]
        public float threatWeight = 5f;

        [Header("Regrouping")]
        [Tooltip("A Regrouping fish returns to Schooling once its nearest neighbour is this close.")]
        public float regroupNeighborDistance = 4f;

        [Header("Foraging")]
        [Tooltip("Distance within which a fish counts as 'near' a foraging node.")]
        public float foragingNodeRadius = 8f;
        [Tooltip("Per-tick chance an unthreatened fish near a node starts foraging.")]
        [Range(0f, 1f)] public float foragingChancePerTick = 0.02f;
        [Tooltip("How long a fish forages before returning to the school (seconds).")]
        public float foragingDuration = 4f;
        [Tooltip("Strength of the pull toward the foraging node while foraging.")]
        public float foragingWeight = 1.5f;

        [Header("Behaviour tick")]
        [Tooltip("Fish re-evaluate their state every N frames, staggered by index (spreads the FSM " +
                 "cost and gives ~5–10 Hz updates). Higher = cheaper but laggier reactions.")]
        [Min(1)] public int behaviorTickBuckets = 10;

        [Header("Per-state steering multipliers (x=separation, y=alignment, z=cohesion)")]
        public Vector3 schoolingSAC = new Vector3(1f, 1f, 1f);
        [Tooltip("Low cohesion here is what lets the school break apart around a predator.")]
        public Vector3 fleeingSAC = new Vector3(1.5f, 0.5f, 0.2f);
        [Tooltip("High cohesion here is what pulls the scattered fish back together.")]
        public Vector3 regroupingSAC = new Vector3(1f, 1.2f, 2f);
        public Vector3 foragingSAC = new Vector3(1f, 0.5f, 0.3f);

        /// <summary>Per-state separation/alignment/cohesion multipliers.</summary>
        public Vector3 SacFor(FishState state)
        {
            switch (state)
            {
                case FishState.Fleeing: return fleeingSAC;
                case FishState.Regrouping: return regroupingSAC;
                case FishState.Foraging: return foragingSAC;
                default: return schoolingSAC;
            }
        }

        /// <summary>A runtime default instance used when no config asset is wired up.</summary>
        public static FishConfig CreateDefault() => CreateInstance<FishConfig>();
    }
}
