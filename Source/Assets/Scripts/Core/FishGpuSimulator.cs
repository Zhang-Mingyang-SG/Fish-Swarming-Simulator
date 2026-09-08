using UnityEngine;
using UnityEngine.Rendering;

namespace FishSwarm.Core
{
    /// <summary>
    /// GPU-resident fish simulation driver. All per-fish data lives in GPU buffers; the CPU only
    /// sets uniforms, uploads predator positions, and dispatches. Each frame builds a spatial grid
    /// (fixed 2^20 hash table + counting sort via a 2-level group-shared prefix sum), reorders the
    /// data into cell order, runs steering + FSM + predator threat, and reduces the school centroid
    /// (read back async so the CPU predator can hunt). The <see cref="Instances"/> buffer is the same
    /// FishInstance layout the indirect render shader reads.
    /// </summary>
    public class FishGpuSimulator
    {
        const int InstanceStride = 32; // FishInstance = 2 x float4
        const int Float4Stride = 16;
        const int UintStride = 4;
        const int GroupSize = 256;
        const int MaxPredators = 16;
        const int MaxNodes = 16;         // foraging nodes uploaded to the GPU FSM
        const int MaxSamples = 256;      // strided position sample read back for predator cluster targeting

        const int TableSize = 1 << 20;   // must match TABLE_SIZE in FishSim.compute
        const int ScanBlock = 1024;      // must match BLOCK in FishSim.compute
        const int NumBlocks = TableSize / ScanBlock; // 1024

        readonly ComputeShader _cs;
        readonly int _kInit, _kClear, _kCount, _kScanBlocks, _kScanSums, _kAddOffsets, _kInitCursor, _kScatter, _kStep, _kReduce, _kSample;

        GraphicsBuffer _positions, _velocities, _sortedPos, _sortedVel, _instances;
        GraphicsBuffer _accel, _sortedAccel;
        GraphicsBuffer _cellCounts, _cellStart, _cellCursor, _blockSums, _blockOffsets;
        GraphicsBuffer _predators, _nodes, _partials, _samples;
        readonly Vector4[] _predatorScratch = new Vector4[MaxPredators];
        int _count, _maxGroups;

        Vector3 _schoolCenter;
        bool _readbackPending, _disposed;

        // Position sample, finished async on the CPU. _sampleData holds the latest read-back points;
        // _sampleCount is how many are valid this frame.
        readonly Vector3[] _sampleData = new Vector3[MaxSamples];
        int _sampleCount;
        bool _sampleReadbackPending;

        public GraphicsBuffer Instances => _instances;
        public int Count => _count;
        public bool IsAllocated => _instances != null;
        public Vector3 SchoolCenter => _schoolCenter;
        public Vector3[] Samples => _sampleData;
        public int SampleCount => _sampleCount;

        public FishGpuSimulator(ComputeShader cs)
        {
            _cs = cs;
            _kInit = cs.FindKernel("Init");
            _kClear = cs.FindKernel("ClearCounts");
            _kCount = cs.FindKernel("Count");
            _kScanBlocks = cs.FindKernel("ScanBlocks");
            _kScanSums = cs.FindKernel("ScanBlockSums");
            _kAddOffsets = cs.FindKernel("AddBlockOffsets");
            _kInitCursor = cs.FindKernel("InitCursor");
            _kScatter = cs.FindKernel("Scatter");
            _kStep = cs.FindKernel("StepSorted");
            _kReduce = cs.FindKernel("ReducePositions");
            _kSample = cs.FindKernel("SamplePositions");
        }

        public void Allocate(int count, Bounds bounds, FishConfig cfg, float spawnRadius, int seed)
        {
            Release();
            _disposed = false;
            _count = Mathf.Max(0, count);
            int cap = Mathf.Max(1, _count);
            _maxGroups = (cap + GroupSize - 1) / GroupSize;
            _schoolCenter = bounds.center;

            _positions = Structured(cap, Float4Stride);
            _velocities = Structured(cap, Float4Stride);
            _sortedPos = Structured(cap, Float4Stride);
            _sortedVel = Structured(cap, Float4Stride);
            _accel = Structured(cap, Float4Stride);
            _sortedAccel = Structured(cap, Float4Stride);
            _instances = Structured(cap, InstanceStride);

            _cellCounts = Structured(TableSize, UintStride);
            _cellStart = Structured(TableSize, UintStride);
            _cellCursor = Structured(TableSize, UintStride);
            _blockSums = Structured(NumBlocks, UintStride);
            _blockOffsets = Structured(NumBlocks, UintStride);

            _predators = Structured(MaxPredators, Float4Stride);
            _nodes = Structured(MaxNodes, Float4Stride);
            _partials = Structured(_maxGroups, Float4Stride);
            _samples = Structured(MaxSamples, Float4Stride);
            _sampleCount = 0;
            _sampleReadbackPending = false;

            SetParams(bounds, cfg);
            _cs.SetFloat("_SpawnRadius", spawnRadius);
            _cs.SetInt("_Seed", seed == 0 ? 12345 : seed);
            BindAll(_kInit);
            Dispatch(_kInit, _count);
        }

        public void Step(Bounds bounds, FishConfig cfg, float dt, Vector4[] predators, int predatorCount,
            Vector4[] nodes, int nodeCount, Vector3 cameraPos, bool lodEnabled, int frame)
        {
            if (_count == 0) return;
            SetParams(bounds, cfg);
            _cs.SetFloat("_Dt", dt);
            _cs.SetInt("_LodEnabled", lodEnabled ? 1 : 0);
            _cs.SetVector("_CameraPos", cameraPos);
            _cs.SetInt("_Frame", frame);

            int pc = Mathf.Clamp(predatorCount, 0, MaxPredators);
            if (pc > 0) _predators.SetData(predators, 0, 0, pc);
            _cs.SetInt("_PredatorCount", pc);

            int nc = Mathf.Clamp(nodeCount, 0, MaxNodes);
            if (nc > 0) _nodes.SetData(nodes, 0, 0, nc);
            _cs.SetInt("_NodeCount", nc);

            // 1) Clear + count agents per bucket.
            BindAll(_kClear);   Dispatch(_kClear, TableSize);
            BindAll(_kCount);   Dispatch(_kCount, _count);

            // 2) Exclusive prefix sum of the counts -> cell start offsets (2-level scan).
            BindAll(_kScanBlocks); _cs.Dispatch(_kScanBlocks, NumBlocks, 1, 1);
            BindAll(_kScanSums);   _cs.Dispatch(_kScanSums, 1, 1, 1);
            BindAll(_kAddOffsets); Dispatch(_kAddOffsets, TableSize);

            // 3) Scatter agents into cell-sorted order.
            BindAll(_kInitCursor); Dispatch(_kInitCursor, TableSize);
            BindAll(_kScatter);    Dispatch(_kScatter, _count);

            // 4) Steering + FSM + threat + write render buffer, on the sorted data.
            BindAll(_kStep); Dispatch(_kStep, _count);

            // 5) Reduce the school centroid (read back async for the predator).
            int groups = (_count + GroupSize - 1) / GroupSize;
            BindAll(_kReduce); _cs.Dispatch(_kReduce, Mathf.Max(1, groups), 1, 1);
            RequestCentroidReadback(groups);

            // 6) Strided position sample (read back async for predator cluster targeting).
            int sampleN = Mathf.Min(_count, MaxSamples);
            int sampleStride = Mathf.Max(1, _count / MaxSamples);
            _cs.SetInt("_SampleCount", sampleN);
            _cs.SetInt("_SampleStride", sampleStride);
            BindAll(_kSample); Dispatch(_kSample, sampleN);
            RequestSampleReadback(sampleN);

            // Ping-pong: the sorted+moved buffers become current for next frame.
            (_positions, _sortedPos) = (_sortedPos, _positions);
            (_velocities, _sortedVel) = (_sortedVel, _velocities);
            (_accel, _sortedAccel) = (_sortedAccel, _accel);
        }

        void RequestCentroidReadback(int numGroups)
        {
            if (_readbackPending || _partials == null) return;
            _readbackPending = true;
            int ng = numGroups, cnt = _count;
            AsyncGPUReadback.Request(_partials, req =>
            {
                _readbackPending = false;
                if (_disposed || req.hasError || cnt == 0) return;
                var data = req.GetData<Vector4>();
                Vector3 sum = Vector3.zero;
                int n = Mathf.Min(ng, data.Length);
                for (int i = 0; i < n; i++) { Vector4 v = data[i]; sum.x += v.x; sum.y += v.y; sum.z += v.z; }
                _schoolCenter = sum / cnt;
            });
        }

        void RequestSampleReadback(int sampleN)
        {
            if (_sampleReadbackPending || _samples == null || sampleN <= 0) return;
            _sampleReadbackPending = true;
            int n = sampleN;
            AsyncGPUReadback.Request(_samples, req =>
            {
                _sampleReadbackPending = false;
                if (_disposed || req.hasError) return;
                var data = req.GetData<Vector4>();
                int m = Mathf.Min(n, data.Length);
                for (int i = 0; i < m; i++) { Vector4 v = data[i]; _sampleData[i] = new Vector3(v.x, v.y, v.z); }
                _sampleCount = m;
            });
        }

        void SetParams(Bounds b, FishConfig cfg)
        {
            _cs.SetInt("_Count", _count);
            _cs.SetVector("_BoundsMin", b.min);
            _cs.SetVector("_BoundsMax", b.max);
            _cs.SetFloat("_MinSpeed", cfg.minSpeed);
            _cs.SetFloat("_MaxSpeed", cfg.maxSpeed);
            _cs.SetVector("_GridOrigin", b.min);
            _cs.SetFloat("_CellSize", Mathf.Max(cfg.perceptionRadius, 0.0001f));
            _cs.SetFloat("_PerceptionSqr", cfg.perceptionRadius * cfg.perceptionRadius);
            _cs.SetInt("_NumBlocks", NumBlocks);

            // Steering (M3).
            _cs.SetFloat("_SeparationRadiusSqr", cfg.separationRadius * cfg.separationRadius);
            _cs.SetFloat("_SeparationWeight", cfg.separationWeight);
            _cs.SetFloat("_AlignmentWeight", cfg.alignmentWeight);
            _cs.SetFloat("_CohesionWeight", cfg.cohesionWeight);
            _cs.SetFloat("_BoundaryWeight", cfg.boundaryWeight);
            _cs.SetVector("_CohesionAxisWeights", cfg.cohesionAxisWeights);
            _cs.SetFloat("_MaxSteerForce", cfg.maxSteerForce);
            _cs.SetInt("_MaxNeighbors", cfg.maxNeighbors);
            _cs.SetFloat("_SeparationGain", cfg.separationGain);
            _cs.SetFloat("_MaxSeparationForce", cfg.maxSeparationForce);
            _cs.SetFloat("_RearBlindDot",
                cfg.rearBlindAngle > 0f ? Mathf.Cos(Mathf.Deg2Rad * (180f - cfg.rearBlindAngle)) : -1.0001f);
            _cs.SetFloat("_BoundaryMargin", cfg.boundaryMargin);

            // Predator threat + FSM (M4).
            _cs.SetFloat("_PredatorFleeRadius", cfg.predatorFleeRadius);
            _cs.SetFloat("_ThreatWeight", cfg.threatWeight);
            _cs.SetFloat("_RegroupNeighborDistance", cfg.regroupNeighborDistance);
            _cs.SetVector("_SchoolingSAC", cfg.schoolingSAC);
            _cs.SetVector("_FleeingSAC", cfg.fleeingSAC);
            _cs.SetVector("_RegroupingSAC", cfg.regroupingSAC);

            // Foraging (M4b). The trigger is a per-tick chance on the CPU; the GPU FSM runs every full
            // frame, so scale it down by the behaviour-tick bucket count to keep the same overall rate.
            _cs.SetVector("_ForagingSAC", cfg.foragingSAC);
            _cs.SetFloat("_ForagingNodeRadiusSqr", cfg.foragingNodeRadius * cfg.foragingNodeRadius);
            _cs.SetFloat("_ForagingChancePerFrame", cfg.foragingChancePerTick / Mathf.Max(1, cfg.behaviorTickBuckets));
            _cs.SetFloat("_ForagingDuration", cfg.foragingDuration);
            _cs.SetFloat("_ForagingWeight", cfg.foragingWeight);

            // LOD (M5). Runtime camera/frame/enable are set per-call in Step.
            _cs.SetFloat("_LodNearSqr", cfg.lodNearDistance * cfg.lodNearDistance);
            _cs.SetFloat("_LodMidSqr", cfg.lodMidDistance * cfg.lodMidDistance);
            _cs.SetFloat("_LodPredatorRadiusSqr", cfg.lodPredatorRadius * cfg.lodPredatorRadius);
            _cs.SetInt("_LodMidStride", Mathf.Max(1, cfg.lodMidStride));
            _cs.SetInt("_LodFarStride", Mathf.Max(1, cfg.lodFarStride));
        }

        void BindAll(int kernel)
        {
            _cs.SetBuffer(kernel, "_Positions", _positions);
            _cs.SetBuffer(kernel, "_Velocities", _velocities);
            _cs.SetBuffer(kernel, "_SortedPos", _sortedPos);
            _cs.SetBuffer(kernel, "_SortedVel", _sortedVel);
            _cs.SetBuffer(kernel, "_Accel", _accel);
            _cs.SetBuffer(kernel, "_SortedAccel", _sortedAccel);
            _cs.SetBuffer(kernel, "_Instances", _instances);
            _cs.SetBuffer(kernel, "_CellCounts", _cellCounts);
            _cs.SetBuffer(kernel, "_CellStart", _cellStart);
            _cs.SetBuffer(kernel, "_CellCursor", _cellCursor);
            _cs.SetBuffer(kernel, "_BlockSums", _blockSums);
            _cs.SetBuffer(kernel, "_BlockOffsets", _blockOffsets);
            _cs.SetBuffer(kernel, "_Predators", _predators);
            _cs.SetBuffer(kernel, "_Nodes", _nodes);
            _cs.SetBuffer(kernel, "_Partials", _partials);
            _cs.SetBuffer(kernel, "_Samples", _samples);
        }

        void Dispatch(int kernel, int elements)
        {
            int groups = (elements + GroupSize - 1) / GroupSize;
            _cs.Dispatch(kernel, Mathf.Max(1, groups), 1, 1);
        }

        static GraphicsBuffer Structured(int count, int stride) =>
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);

        public void Release()
        {
            _disposed = true;
            // flush pending reads before freeing _partials / _samples
            if (_readbackPending || _sampleReadbackPending) AsyncGPUReadback.WaitAllRequests();

            _positions?.Release(); _velocities?.Release();
            _sortedPos?.Release(); _sortedVel?.Release();
            _accel?.Release(); _sortedAccel?.Release();
            _instances?.Release();
            _cellCounts?.Release(); _cellStart?.Release(); _cellCursor?.Release();
            _blockSums?.Release(); _blockOffsets?.Release();
            _predators?.Release(); _nodes?.Release(); _partials?.Release(); _samples?.Release();

            _positions = _velocities = _sortedPos = _sortedVel = _instances = null;
            _accel = _sortedAccel = null;
            _cellCounts = _cellStart = _cellCursor = _blockSums = _blockOffsets = null;
            _predators = _nodes = _partials = _samples = null;
            _count = 0;
        }
    }
}
