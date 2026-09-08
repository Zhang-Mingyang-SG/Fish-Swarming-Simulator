using Unity.Collections;
using Unity.Mathematics;
using FishSwarm.Behavior;
using FishSwarm.FSM;
using Random = Unity.Mathematics.Random;

namespace FishSwarm.Jobs
{
    /// <summary>
    /// Per-instance render data for the GPU-indirect path. Two float4s (32 bytes, 16-byte aligned)
    /// so the layout matches <c>StructuredBuffer&lt;FishInstance&gt;</c> in FishIndirect.shader.
    /// </summary>
    public struct FishInstance
    {
        public float4 pos; // xyz = world position
        public float4 fwd; // xyz = heading (normalised), w = state index
    }

    /// <summary>
    /// Burst-compatible per-fish simulation, written with <c>Unity.Mathematics</c> and only
    /// blittable value/native types. Every method here runs Burst-compiled + parallel when called
    /// from a scheduled <c>[BurstCompile]</c> job (Burst ON), and as plain single-threaded IL when
    /// called from a main-thread loop (Burst OFF) — one implementation, honest comparison.
    ///
    /// The grid is a <b>counting sort</b>: fish are bucketed into a bounded, collision-free cell
    /// index (cell size = perception radius) and the position/velocity/state arrays are physically
    /// <b>reordered into cell order</b> each frame. A neighbour scan then reads a contiguous run of
    /// memory per cell instead of chasing scattered indices — the fix for the memory-bandwidth-bound
    /// neighbour query. <see cref="CellKey"/> is unique per cell so runs never collide.
    /// </summary>
    public static class FishSteering
    {
        // ---- Grid cell helpers -------------------------------------------------------------

        public static int3 CellCoords(float3 pos, in SteeringParams p)
        {
            int3 c = (int3)math.floor((pos - p.gridOrigin) / p.cellSize);
            return math.clamp(c, int3.zero, p.gridDim - 1);
        }

        public static int CellKey(int3 c, in SteeringParams p)
        {
            return c.x + p.gridDim.x * (c.y + p.gridDim.y * c.z);
        }

        /// <summary>Per-fish: compute the flattened cell index of a position (parallel-safe).</summary>
        public static void BuildCellIndex(int i,
            in NativeArray<float3> positions, in SteeringParams p, NativeArray<int> cellIndex)
        {
            cellIndex[i] = CellKey(CellCoords(positions[i], p), p);
        }

        /// <summary>
        /// Counting sort: from <paramref name="cellIndex"/>, build per-cell start offsets
        /// (<paramref name="cellStart"/>, length cellCount+1) and scatter the source arrays into
        /// cell order in the destination arrays. Single-threaded, two O(n) passes + O(cellCount).
        /// <paramref name="cellCounts"/> (length cellCount) is scratch, reused as the write cursor.
        /// </summary>
        public static void CountingSort(int count, int cellCount,
            in NativeArray<int> cellIndex,
            NativeArray<int> cellCounts, NativeArray<int> cellStart,
            in NativeArray<float3> srcPos, in NativeArray<float3> srcVel,
            in NativeArray<FishState> srcState, in NativeArray<float> srcTimer,
            in NativeArray<float3> srcAccel,
            NativeArray<float3> dstPos, NativeArray<float3> dstVel,
            NativeArray<FishState> dstState, NativeArray<float> dstTimer,
            NativeArray<float3> dstAccel)
        {
            for (int c = 0; c < cellCount; c++) cellCounts[c] = 0;
            for (int i = 0; i < count; i++) cellCounts[cellIndex[i]]++;

            int acc = 0;
            for (int c = 0; c < cellCount; c++) { cellStart[c] = acc; acc += cellCounts[c]; }
            cellStart[cellCount] = acc;

            for (int c = 0; c < cellCount; c++) cellCounts[c] = cellStart[c]; // reuse as write cursor
            for (int i = 0; i < count; i++)
            {
                int c = cellIndex[i];
                int slot = cellCounts[c]++;
                dstPos[slot] = srcPos[i];
                dstVel[slot] = srcVel[i];
                dstState[slot] = srcState[i];
                dstTimer[slot] = srcTimer[i];
                // Acceleration must follow the fish: LOD-skipped fish reuse their own last value,
                // and slots reshuffle every frame.
                dstAccel[slot] = srcAccel[i];
            }
        }

        // ---- Neighbour accumulation --------------------------------------------------------

        struct Accum
        {
            public float3 sep;
            public float3 aliSum;
            public float3 cohSum;
            public int flock;
            public float nearestSqr;
        }

        static bool AddNeighbor(ref Accum a, float3 myPos, float3 forward, int j,
            in NativeArray<float3> pos, in NativeArray<float3> vel,
            float perSqr, float sepSqr, int maxN, in SteeringParams p)
        {
            float3 off = myPos - pos[j];
            float d2 = math.lengthsq(off);
            if (d2 < perSqr && d2 > 1e-6f)
            {
                // Collision spacing is omnidirectional: even fish behind us should still push away.
                if (d2 < sepSqr) a.sep += off / d2;

                // The blind angle affects social perception only. Hidden neighbours do not steer
                // alignment/cohesion and do not count as "near" for regrouping FSM decisions.
                if (p.rearBlindDot > -1f && math.dot(math.normalize(-off), forward) < p.rearBlindDot)
                    return false;

                if (d2 < a.nearestSqr) a.nearestSqr = d2;
                a.aliSum += vel[j];
                a.cohSum += pos[j];
                a.flock++;
                if (a.flock >= maxN) return true; // cap reached
            }
            return false;
        }

        // Scan a cell's contiguous run of fish (cell-sorted arrays). Sequential memory reads.
        static bool ScanCell(ref Accum a, int cellKey, float3 myPos, float3 forward, int self,
            in NativeArray<int> cellStart,
            in NativeArray<float3> pos, in NativeArray<float3> vel,
            float perSqr, float sepSqr, int maxN, in SteeringParams p)
        {
            int start = cellStart[cellKey];
            int end = cellStart[cellKey + 1];
            for (int k = start; k < end; k++)
            {
                if (k != self && AddNeighbor(ref a, myPos, forward, k, pos, vel, perSqr, sepSqr, maxN, p))
                    return true;
            }
            return false;
        }

        // ---- Steering forces ---------------------------------------------------------------

        static float3 ClampMag(float3 v, float max)
        {
            float m2 = math.lengthsq(v);
            if (m2 > max * max) return v * (max / math.sqrt(m2));
            return v;
        }

        // Reynolds steer: full-speed desired toward dir, minus current velocity, clamped.
        static float3 Steer(float3 dir, float3 vel, in SteeringParams p)
        {
            if (math.lengthsq(dir) < 1e-8f) return float3.zero;
            float3 desired = math.normalize(dir) * p.maxSpeed;
            return ClampMag(desired - vel, p.maxSteerForce);
        }

        static float3 Boundary(float3 pos, float3 vel, in SteeringParams p)
        {
            float3 mn = p.boundsCenter - p.boundsSize * 0.5f;
            float3 mx = p.boundsCenter + p.boundsSize * 0.5f;
            float m = p.boundaryMargin;

            float3 inward = float3.zero;
            if (pos.x < mn.x + m) inward.x += 1f; else if (pos.x > mx.x - m) inward.x -= 1f;
            if (pos.y < mn.y + m) inward.y += 1f; else if (pos.y > mx.y - m) inward.y -= 1f;
            if (pos.z < mn.z + m) inward.z += 1f; else if (pos.z > mx.z - m) inward.z -= 1f;

            if (math.lengthsq(inward) < 1e-8f) return float3.zero;
            return Steer(inward, vel, p) * p.boundaryWeight;
        }

        static float3 Threat(float3 pos, float3 vel,
            in NativeArray<float3> predators, int predatorCount, in SteeringParams p, out byte inRange)
        {
            inRange = 0;
            float3 flee = float3.zero;
            float r = p.predatorFleeRadius;
            float r2 = r * r;
            for (int k = 0; k < predatorCount; k++)
            {
                float3 off = pos - predators[k];
                float d2 = math.lengthsq(off);
                if (d2 < r2 && d2 > 1e-6f)
                {
                    inRange = 1;
                    float d = math.sqrt(d2);
                    flee += (off / d) * (1f - d / r); // away, ramps up as predator nears
                }
            }
            if (math.lengthsq(flee) < 1e-8f) return float3.zero;
            return Steer(flee, vel, p) * p.threatWeight;
        }

        /// <summary>
        /// How many frames between full neighbour scans for this fish. 1 = every frame ("hero").
        /// Predator proximity always forces 1 so flee/split behaviour stays exact; otherwise it's
        /// distance to the camera. Sim-only — every fish still integrates (moves) every frame.
        /// </summary>
        static int LodStride(float3 pos, in NativeArray<float3> predators, int predatorCount, in SteeringParams p)
        {
            for (int k = 0; k < predatorCount; k++)
                if (math.lengthsq(pos - predators[k]) < p.lodPredatorRadiusSqr)
                    return 1;

            float camSqr = math.lengthsq(pos - p.cameraPos);
            if (camSqr < p.lodNearSqr) return 1;
            if (camSqr < p.lodMidSqr) return p.lodMidStride;
            return p.lodFarStride;
        }

        static int NearestNode(float3 pos, in NativeArray<float3> nodes, int nodeCount, in SteeringParams p)
        {
            float best = p.foragingNodeRadius * p.foragingNodeRadius;
            int nearest = -1;
            for (int k = 0; k < nodeCount; k++)
            {
                float d2 = math.lengthsq(nodes[k] - pos);
                if (d2 < best) { best = d2; nearest = k; }
            }
            return nearest;
        }

        // ---- Per-fish steering step (arrays are cell-sorted; i is a sorted slot) -----------

        public static void Step(int i,
            in NativeArray<float3> positions, in NativeArray<float3> velocities,
            in NativeArray<FishState> states,
            in NativeArray<float3> predators, int predatorCount,
            in NativeArray<float3> nodes, int nodeCount,
            in NativeArray<int> cellStart, bool useGrid,
            in SteeringParams p,
            NativeArray<float3> accelerations,
            NativeArray<float> nearestNeighborSqr,
            NativeArray<byte> predatorInRange,
            NativeArray<byte> steered)
        {
            float3 myPos = positions[i];
            float3 myVel = velocities[i];
            int state = (int)states[i];

            // Simulation LOD: distant fish skip the (expensive) neighbour scan on most frames and
            // coast on the acceleration carried through the counting sort. Fish in the same cell
            // share a phase, so the branch stays coherent and the load spreads across frames.
            if (p.lodEnabled != 0)
            {
                int stride = LodStride(myPos, predators, predatorCount, p);
                if (stride > 1 && (uint)(p.frame + CellKey(CellCoords(myPos, p), p)) % (uint)stride != 0)
                {
                    // Not scanned this frame: coast on the carried acceleration and, crucially, tell
                    // the FSM to skip too — its neighbour/predator inputs are stale, so ticking on
                    // them would be wrong (a Regrouping fish could never see it rejoined). The FSM
                    // just holds this fish's state until steering runs again.
                    steered[i] = 0;
                    return; // accelerations[i], nearestNeighborSqr[i], predatorInRange[i] all kept
                }
            }

            float perSqr = p.perceptionRadius * p.perceptionRadius;
            float sepSqr = p.separationRadius * p.separationRadius;
            int maxN = p.maxNeighbors;

            Accum a = new Accum { nearestSqr = float.MaxValue };
            float3 forward = math.lengthsq(myVel) > 1e-8f ? math.normalize(myVel) : new float3(0f, 0f, 1f);

            if (useGrid)
            {
                // Own cell first (closest, symmetric), then the 26 neighbours; early-out at the cap.
                int3 cc = CellCoords(myPos, p);
                bool done = ScanCell(ref a, CellKey(cc, p), myPos, forward, i, cellStart, positions, velocities, perSqr, sepSqr, maxN, p);
                for (int dz = -1; dz <= 1 && !done; dz++)
                for (int dy = -1; dy <= 1 && !done; dy++)
                for (int dx = -1; dx <= 1 && !done; dx++)
                {
                    if ((dx | dy | dz) == 0) continue; // centre already scanned
                    int3 nc = cc + new int3(dx, dy, dz);
                    if (math.any(nc < 0) || math.any(nc >= p.gridDim)) continue;
                    done = ScanCell(ref a, CellKey(nc, p), myPos, forward, i, cellStart, positions, velocities, perSqr, sepSqr, maxN, p);
                }
            }
            else
            {
                int count = positions.Length;
                for (int j = 0; j < count; j++)
                {
                    if (j == i) continue;
                    if (AddNeighbor(ref a, myPos, forward, j, positions, velocities, perSqr, sepSqr, maxN, p))
                        break;
                }
            }

            float3 sac = p.SacFor(state);
            float3 force = float3.zero;

            if (a.flock > 0)
            {
                float3 avgVel = a.aliSum / a.flock;
                force += Steer(avgVel, myVel, p) * (p.alignmentWeight * sac.y);

                // Anisotropic cohesion: scale the force per axis so the school is ellipsoidal.
                float3 toCentre = (a.cohSum / a.flock) - myPos;
                float3 coh = Steer(toCentre, myVel, p) * p.cohesionAxisWeights;
                force += coh * (p.cohesionWeight * sac.z);
            }

            // Separation keeps its accumulated magnitude (|a.sep| ~ sum of 1/d) rather than being
            // normalised away by Steer(). That magnitude blows up as fish approach contact, giving a
            // strong short-range repulsion — a "soft collision" that costs nothing extra, since the
            // neighbour scan already gathered it. Clamped separately, above maxSteerForce, so
            // crowded fish can push harder than ordinary steering permits.
            if (math.lengthsq(a.sep) > 1e-8f)
            {
                float3 sepForce = ClampMag(a.sep * p.separationGain, p.maxSeparationForce);
                force += sepForce * (p.separationWeight * sac.x);
            }

            force += Boundary(myPos, myVel, p);
            force += Threat(myPos, myVel, predators, predatorCount, p, out byte inRange);

            // Foraging pull toward the nearest node while in the Foraging state.
            if (state == (int)FishState.Foraging)
            {
                int node = NearestNode(myPos, nodes, nodeCount, p);
                if (node >= 0)
                    force += Steer(nodes[node] - myPos, myVel, p) * p.foragingWeight;
            }

            accelerations[i] = force;
            nearestNeighborSqr[i] = a.nearestSqr;
            predatorInRange[i] = inRange;
            steered[i] = 1;
        }

        // ---- Integration -------------------------------------------------------------------

        public static void Integrate(int i,
            NativeArray<float3> positions, NativeArray<float3> velocities,
            in NativeArray<float3> accelerations, float dt, in SteeringParams p)
        {
            float3 v = velocities[i] + accelerations[i] * dt;
            float speed = math.length(v);
            if (speed < 1e-5f)
            {
                float3 prev = velocities[i];
                v = (math.lengthsq(prev) > 1e-10f ? math.normalize(prev) : new float3(0f, 0f, 1f)) * p.minSpeed;
            }
            else
            {
                v = v / speed * math.clamp(speed, p.minSpeed, p.maxSpeed);
            }

            velocities[i] = v;
            positions[i] = positions[i] + v * dt;
        }

        /// <summary>Instanced-render path: build a TRS matrix from position + heading.</summary>
        public static void BuildMatrix(int i,
            in NativeArray<float3> positions, in NativeArray<float3> velocities,
            NativeArray<float4x4> matrices, float3 scale)
        {
            float3 v = velocities[i];
            quaternion rot = math.lengthsq(v) > 1e-8f
                ? quaternion.LookRotationSafe(v, math.up())
                : quaternion.identity;
            matrices[i] = float4x4.TRS(positions[i], rot, scale);
        }

        /// <summary>Indirect-render path: pack position + heading + state for the GPU (no matrix).</summary>
        public static void PackInstance(int i,
            in NativeArray<float3> positions, in NativeArray<float3> velocities,
            in NativeArray<FishState> states, NativeArray<FishInstance> instances)
        {
            float3 v = velocities[i];
            float3 f = math.lengthsq(v) > 1e-8f ? math.normalize(v) : new float3(0f, 0f, 1f);
            instances[i] = new FishInstance
            {
                pos = new float4(positions[i], 0f),
                fwd = new float4(f, (float)(int)states[i]),
            };
        }

        // ---- Behaviour tick (staggered) ----------------------------------------------------

        public static void TickBehavior(int i,
            in NativeArray<float3> positions, in NativeArray<float> nearestNeighborSqr,
            in NativeArray<byte> predatorInRange, in NativeArray<float3> nodes, int nodeCount,
            NativeArray<FishState> states, NativeArray<float> stateTimer,
            in SteeringParams p, float tickDt, uint frameSeed)
        {
            bool nearNode = NearestNode(positions[i], nodes, nodeCount, p) >= 0;
            float nnDist = math.sqrt(nearestNeighborSqr[i]);

            var rng = new Random((uint)(i * 747796405) ^ frameSeed | 1u);
            float timer = stateTimer[i];

            var input = new FishStateInputs
            {
                PredatorInRange = predatorInRange[i],
                NearestNeighborDistance = nnDist,
                NearForagingNode = nearNode ? (byte)1 : (byte)0,
                RandomValue = rng.NextFloat(),
            };

            var settings = new FishStateSettings
            {
                RegroupNeighborDistance = p.regroupNeighborDistance,
                ForagingChance = p.foragingChancePerTick,
                ForagingDuration = p.foragingDuration,
            };

            states[i] = FiniteStateMachine.Tick(
                states[i],
                input,
                settings,
                tickDt,
                ref timer);

            stateTimer[i] = timer;
        }

        // ---- Colour bucketing (counting sort by state) -------------------------------------

        /// <summary>
        /// Reorder <paramref name="matrices"/> into <paramref name="sorted"/> grouped by state, and
        /// write each state's start offset + count so rendering can draw one colour per contiguous
        /// run. Two O(n) passes, single-threaded.
        /// </summary>
        public static void BucketByState(int count,
            in NativeArray<FishState> states, in NativeArray<float4x4> matrices,
            NativeArray<float4x4> sorted, NativeArray<int> stateStart, NativeArray<int> stateCount)
        {
            int c0 = 0, c1 = 0, c2 = 0, c3 = 0;
            for (int i = 0; i < count; i++)
            {
                switch ((int)states[i])
                {
                    case 1: c1++; break;
                    case 2: c2++; break;
                    case 3: c3++; break;
                    default: c0++; break;
                }
            }

            int s0 = 0, s1 = s0 + c0, s2 = s1 + c1, s3 = s2 + c2;
            stateStart[0] = s0; stateStart[1] = s1; stateStart[2] = s2; stateStart[3] = s3;
            stateCount[0] = c0; stateCount[1] = c1; stateCount[2] = c2; stateCount[3] = c3;

            int w0 = s0, w1 = s1, w2 = s2, w3 = s3;
            for (int i = 0; i < count; i++)
            {
                switch ((int)states[i])
                {
                    case 1: sorted[w1++] = matrices[i]; break;
                    case 2: sorted[w2++] = matrices[i]; break;
                    case 3: sorted[w3++] = matrices[i]; break;
                    default: sorted[w0++] = matrices[i]; break;
                }
            }
        }
    }
}
