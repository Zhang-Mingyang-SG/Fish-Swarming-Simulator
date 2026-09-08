using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using FishSwarm.Core;
using FishSwarm.FSM;

namespace FishSwarm.Predators
{
    /// <summary>
    /// Predator controller with its own FSM. It targets the sampled school centre rather than
    /// individual fish, which keeps predator logic cheap for the instanced fish simulation.
    /// </summary>
    public class Predator : MonoBehaviour
    {
        public static readonly List<Predator> All = new List<Predator>();

        [Header("State")]
        public PredatorState state = PredatorState.Patrol;
        [Tooltip("Fish manager used as the prey target source. Auto-resolved if left empty.")]
        public FishManager fishManager;

        [Header("Sensing")]
        [Tooltip("Radius shown as a gizmo and used as the predator FSM detection range. Keep this " +
                 "ABOVE the fish predatorFleeRadius so the predator commits to a chase before the " +
                 "fish scatter — that staging is what makes the pre-split ripple read on screen.")]
        public float threatRadius = 22f;
        [Tooltip("Distance at which an existing Chase gives up. Keep this above threatRadius to " +
                 "prevent rapid Patrol/Chase switching near the detection boundary.")]
        public float loseSightRadius = 30f;
        [Tooltip("Distance at which Chase becomes Lunge.")]
        public float lungeRange = 8f;
        [Tooltip("Radius used to average the nearest fish cluster's centroid. Roughly one sub-group " +
                 "wide, so the predator aims at a real cluster rather than the whole-school mean.")]
        public float preyClusterRadius = 22f;

        [Header("Timing")]
        public float lungeDuration = 0.8f;
        public float recoverDuration = 1.2f;
        [Tooltip("Minimum time spent patrolling after recovery before prey can trigger another chase.")]
        public float patrolCooldownDuration = 8f;

        [Header("Movement")]
        [FormerlySerializedAs("speed")]
        public float patrolSpeed = 12f;
        public float chaseSpeed = 18f;
        public float lungeSpeed = 32f;
        public float recoverSpeed = 6f;
        [Tooltip("How quickly the predator turns toward its desired heading.")]
        public float turnSharpness = 8f;

        [Header("Movement Noise")]
        [Tooltip("Slow, broad wandering during Patrol. Higher values make the patrol path less linear.")]
        public float patrolNoise = 0.4f;
        [Tooltip("Small tracking imperfections during Chase. Keep this below patrol noise so the predator still pursues prey clearly.")]
        public float chaseNoise = 0.12f;
        [Tooltip("Unsteady movement during Recover, helping the slowdown read as a distinct state.")]
        public float recoverNoise = 0.3f;
        [Tooltip("How quickly the noise pattern changes. Lower values produce broader, smoother curves.")]
        public float noiseFrequency = 0.5f;

        [Header("State Visuals")]
        [Tooltip("Temporary renderer tint used while the predator is committed to a lunge.")]
        public Color lungeColor = new Color(1f, 0.05f, 0.05f, 1f);

        [Header("Patrol")]
        [Tooltip("If enabled, Patrol follows the ping-pong sweep. If disabled, Patrol idles.")]
        public bool autoPatrol = true;
        [Tooltip("Half-length of the ping-pong sweep, measured from the start position.")]
        public float patrolDistance = 55f;
        public Vector3 patrolAxis = Vector3.right;

        Vector3 _start;
        Vector3 _heading = Vector3.right;
        Vector3 _lungeHeading = Vector3.right;
        float _stateTimer;
        float _attackCooldownTimer;
        Renderer[] _renderers;
        Color[] _originalBaseColors;
        Color[] _originalColors;
        MaterialPropertyBlock _colorBlock;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        void OnEnable()
        {
            All.Add(this);
            _start = transform.position;
            if (fishManager == null) fishManager = FindFirstObjectByType<FishManager>();
            CacheRendererColors();
            ApplyStateColor();
        }

        void OnDisable()
        {
            // Do not leave a lunge override behind if this component is disabled mid-attack.
            ApplyRendererColor(false);
            All.Remove(this);
        }

        void Update()
        {
            if (fishManager == null) fishManager = FindFirstObjectByType<FishManager>();

            if (_attackCooldownTimer > 0f)
                _attackCooldownTimer = Mathf.Max(0f, _attackCooldownTimer - Time.deltaTime);

            Vector3 preyTarget = fishManager != null
                ? fishManager.ClusterTargetNear(transform.position, preyClusterRadius)
                : transform.position;
            var input = new PredatorStateInputs
            {
                // Suppress detection during the post-attack cooldown so Recover must lead into
                // a genuine Patrol interval instead of immediately beginning another Chase.
                HasPreyTarget = _attackCooldownTimer <= 0f && fishManager != null && fishManager.Count > 0,
                DistanceToPrey = Vector3.Distance(transform.position, preyTarget),
            };

            var settings = new PredatorStateSettings
            {
                DetectionRadius = threatRadius,
                LoseSightRadius = Mathf.Max(threatRadius, loseSightRadius),
                LungeRange = lungeRange,
                LungeDuration = lungeDuration,
                RecoverDuration = recoverDuration,
            };

            PredatorState previous = state;
            state = PredatorStateMachine.Tick(state, input, settings, Time.deltaTime, ref _stateTimer);
            if (previous != state)
            {
                if (state == PredatorState.Lunge)
                    _lungeHeading = DesiredHeadingTo(preyTarget);
                else if (previous == PredatorState.Lunge && state == PredatorState.Recover)
                {
                    // Include recovery in the lockout, then preserve the full requested cooldown
                    // for Patrol after Recover completes.
                    _attackCooldownTimer = recoverDuration + patrolCooldownDuration;
                }

                // Update only on transitions; renderer properties do not need to be written every frame.
                ApplyStateColor();
            }

            MoveForState(preyTarget);
        }

        void MoveForState(Vector3 preyTarget)
        {
            switch (state)
            {
                case PredatorState.Chase:
                    // Chase retains a little natural variation without losing sight of its target.
                    MoveAlong(AddMovementNoise(DesiredHeadingTo(preyTarget), chaseNoise), chaseSpeed);
                    break;

                case PredatorState.Lunge:
                    // A noise-free, committed line makes the sudden lunge visually distinct.
                    MoveAlong(_lungeHeading, lungeSpeed);
                    break;

                case PredatorState.Recover:
                    // Resume noisier movement while slowing down so recovery does not resemble a slow lunge.
                    MoveAlong(AddMovementNoise(_heading, recoverNoise), recoverSpeed);
                    break;

                case PredatorState.Patrol:
                default:
                    UpdatePatrol(preyTarget);
                    break;
            }
        }

        void UpdatePatrol(Vector3 preyTarget)
        {
            if (!autoPatrol) return;

            if (_attackCooldownTimer <= 0f && fishManager != null && fishManager.Count > 0)
            {
                // Patrol doubles as a search state. Following the nearest sampled cluster from
                // outside the detection radius prevents a displaced school from permanently
                // escaping the predator's small local sensing range. The FSM will switch to
                // Chase naturally once this search brings the predator within threatRadius.
                MoveAlong(AddMovementNoise(DesiredHeadingTo(preyTarget), patrolNoise), patrolSpeed);
                return;
            }

            // With no prey available, retain the original route around the spawn position.
            Vector3 axis = patrolAxis.sqrMagnitude > 1e-6f ? patrolAxis.normalized : Vector3.right;
            float offset = Mathf.PingPong(Time.time * patrolSpeed, patrolDistance * 2f) - patrolDistance;
            Vector3 target = _start + axis * offset;
            // Patrol has the strongest noise, turning the rigid ping-pong sweep into a smooth wander.
            MoveAlong(AddMovementNoise(DesiredHeadingTo(target), patrolNoise), patrolSpeed);
        }

        void MoveAlong(Vector3 desiredHeading, float speed)
        {
            if (desiredHeading.sqrMagnitude < 1e-6f) return;

            float t = 1f - Mathf.Exp(-turnSharpness * Time.deltaTime);
            _heading = Vector3.Slerp(_heading, desiredHeading.normalized, t).normalized;
            transform.position += _heading * speed * Time.deltaTime;
            transform.rotation = Quaternion.LookRotation(_heading, Vector3.up);
        }

        Vector3 AddMovementNoise(Vector3 heading, float strength)
        {
            if (heading.sqrMagnitude < 1e-6f || strength <= 0f)
                return heading;

            heading.Normalize();

            // Each predator receives a different slice of the noise field, while sampling it
            // continuously over time produces smooth turns rather than frame-to-frame jitter.
            float seed = GetInstanceID() * 0.0137f;
            float noiseTime = Time.time * noiseFrequency;
            float horizontal = Mathf.PerlinNoise(seed, noiseTime) * 2f - 1f;
            float vertical = Mathf.PerlinNoise(seed + 20f, noiseTime) * 2f - 1f;

            Vector3 side = Vector3.Cross(Vector3.up, heading);
            if (side.sqrMagnitude < 1e-6f)
                side = Vector3.right;
            else
                side.Normalize();

            // Vertical noise is deliberately weaker to favour fish-like sweeping turns over bobbing.
            return (heading +
                    side * horizontal * strength +
                    Vector3.up * vertical * strength * 0.4f).normalized;
        }

        Vector3 DesiredHeadingTo(Vector3 target)
        {
            Vector3 toTarget = target - transform.position;
            return toTarget.sqrMagnitude > 1e-6f ? toTarget.normalized : _heading;
        }

        void CacheRendererColors()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _originalBaseColors = new Color[_renderers.Length];
            _originalColors = new Color[_renderers.Length];
            _colorBlock = new MaterialPropertyBlock();

            for (int i = 0; i < _renderers.Length; i++)
            {
                Material material = _renderers[i].sharedMaterial;
                _originalBaseColors[i] = material != null && material.HasProperty(BaseColorId)
                    ? material.GetColor(BaseColorId)
                    : Color.white;
                _originalColors[i] = material != null && material.HasProperty(ColorId)
                    ? material.GetColor(ColorId)
                    : Color.white;
            }
        }

        void ApplyStateColor()
        {
            ApplyRendererColor(state == PredatorState.Lunge);
        }

        void ApplyRendererColor(bool useLungeColor)
        {
            if (_renderers == null || _colorBlock == null) return;

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer targetRenderer = _renderers[i];
                if (targetRenderer == null) continue;

                Material material = targetRenderer.sharedMaterial;
                if (material == null) continue;

                // A property block changes this predator only; shared materials used by other
                // predators remain untouched and no per-renderer material copies are allocated.
                targetRenderer.GetPropertyBlock(_colorBlock);
                if (material.HasProperty(BaseColorId))
                    _colorBlock.SetColor(BaseColorId, useLungeColor ? lungeColor : _originalBaseColors[i]);
                if (material.HasProperty(ColorId))
                    _colorBlock.SetColor(ColorId, useLungeColor ? lungeColor : _originalColors[i]);
                targetRenderer.SetPropertyBlock(_colorBlock);
            }
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.2f, 0.15f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, threatRadius);

            // The wider, fainter ring visualises the hysteresis used to retain a chase.
            Gizmos.color = new Color(1f, 0.2f, 0.15f, 0.2f);
            Gizmos.DrawWireSphere(transform.position, Mathf.Max(threatRadius, loseSightRadius));

            Gizmos.color = new Color(1f, 0.8f, 0.1f, 0.5f);
            Gizmos.DrawWireSphere(transform.position, lungeRange);
        }
    }
}
