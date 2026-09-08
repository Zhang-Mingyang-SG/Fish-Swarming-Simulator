using Unity.Mathematics;
using FishSwarm.Core;

namespace FishSwarm.Jobs
{
    /// <summary>
    /// Blittable snapshot of <see cref="FishConfig"/> plus the derived grid layout, rebuilt each
    /// frame and passed into the Burst jobs by value. Jobs can't touch the managed ScriptableObject,
    /// so everything the steering maths needs lives here as plain value types.
    /// </summary>
    public struct SteeringParams
    {
        // Perception / flocking
        public float perceptionRadius, separationRadius;
        // Dot threshold for the rear blind cone:
        // -1.0001 = disabled, about -0.82 = 35 degrees behind, 0 = rear hemisphere.
        public float rearBlindDot;
        public float separationWeight, alignmentWeight, cohesionWeight, boundaryWeight;
        public float3 cohesionAxisWeights;

        // Anti-clipping: separation keeps its accumulated magnitude, scaled + clamped separately.
        public float separationGain, maxSeparationForce;

        // Movement
        public float maxSpeed, minSpeed, maxSteerForce, boundaryMargin;
        public int maxNeighbors;

        // Predator / regroup / foraging
        public float predatorFleeRadius, threatWeight;
        public float regroupNeighborDistance;
        public float foragingNodeRadius, foragingChancePerTick, foragingDuration, foragingWeight;

        // Behaviour
        public int behaviorTickBuckets;
        public float3 schoolingSAC, fleeingSAC, regroupingSAC, foragingSAC;

        // Ocean volume
        public float3 boundsCenter, boundsSize;

        // Uniform grid layout (cell size = perception radius, bounded to the ocean volume).
        public float3 gridOrigin;
        public float cellSize;
        public int3 gridDim;

        // Simulation LOD. Far fish re-run the neighbour scan only every Nth frame and coast on
        // their last acceleration in between; predator-adjacent fish are always full rate.
        public byte lodEnabled;
        public float3 cameraPos;
        public float lodNearSqr, lodMidSqr, lodPredatorRadiusSqr;
        public int lodMidStride, lodFarStride;
        public int frame; // phase source, combined with cell key so cells tick together

        /// <summary>Per-state (separation, alignment, cohesion) multipliers. State int matches FishState.</summary>
        public readonly float3 SacFor(int state)
        {
            switch (state)
            {
                case 1: return fleeingSAC;    // Fleeing
                case 2: return regroupingSAC; // Regrouping
                case 3: return foragingSAC;   // Foraging
                default: return schoolingSAC; // Schooling
            }
        }

        /// <summary>Build the per-frame snapshot from the config + ocean bounds + LOD state.</summary>
        public static SteeringParams From(FishConfig c, float3 boundsCenter, float3 boundsSize,
            float3 cameraPos, bool lodEnabled, int frame)
        {
            float cell = math.max(c.perceptionRadius, 0.0001f);
            float3 origin = boundsCenter - boundsSize * 0.5f;
            int3 dim = math.max(1, (int3)math.ceil(boundsSize / cell));

            return new SteeringParams
            {
                perceptionRadius = c.perceptionRadius,
                separationRadius = c.separationRadius,
                rearBlindDot = c.rearBlindAngle > 0f ? math.cos(math.radians(180f - c.rearBlindAngle)) : -1.0001f,
                separationWeight = c.separationWeight,
                alignmentWeight = c.alignmentWeight,
                cohesionWeight = c.cohesionWeight,
                boundaryWeight = c.boundaryWeight,
                cohesionAxisWeights = c.cohesionAxisWeights,
                separationGain = c.separationGain,
                maxSeparationForce = c.maxSeparationForce,

                maxSpeed = c.maxSpeed,
                minSpeed = c.minSpeed,
                maxSteerForce = c.maxSteerForce,
                boundaryMargin = c.boundaryMargin,
                maxNeighbors = c.maxNeighbors,

                predatorFleeRadius = c.predatorFleeRadius,
                threatWeight = c.threatWeight,
                regroupNeighborDistance = c.regroupNeighborDistance,
                foragingNodeRadius = c.foragingNodeRadius,
                foragingChancePerTick = c.foragingChancePerTick,
                foragingDuration = c.foragingDuration,
                foragingWeight = c.foragingWeight,

                behaviorTickBuckets = math.max(1, c.behaviorTickBuckets),
                schoolingSAC = c.schoolingSAC,
                fleeingSAC = c.fleeingSAC,
                regroupingSAC = c.regroupingSAC,
                foragingSAC = c.foragingSAC,

                boundsCenter = boundsCenter,
                boundsSize = boundsSize,
                gridOrigin = origin,
                cellSize = cell,
                gridDim = dim,

                lodEnabled = (byte)(lodEnabled ? 1 : 0),
                cameraPos = cameraPos,
                lodNearSqr = c.lodNearDistance * c.lodNearDistance,
                lodMidSqr = c.lodMidDistance * c.lodMidDistance,
                lodPredatorRadiusSqr = c.lodPredatorRadius * c.lodPredatorRadius,
                lodMidStride = math.max(1, c.lodMidStride),
                lodFarStride = math.max(1, c.lodFarStride),
                frame = frame,
            };
        }
    }
}
