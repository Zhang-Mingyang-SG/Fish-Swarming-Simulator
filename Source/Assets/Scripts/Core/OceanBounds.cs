using UnityEngine;

namespace FishSwarm.Core
{
    /// <summary>
    /// Single source of truth for the simulation's ocean volume. The box is centred on this
    /// transform; other systems (fish spawning, boundary steering, predator patrol) read
    /// <see cref="Bounds"/> rather than hard-coding extents. Draws a wireframe gizmo so the
    /// volume is visible while editing.
    /// </summary>
    public class OceanBounds : MonoBehaviour
    {
        [Tooltip("Full extents (width, height, depth) of the ocean volume in world units.")]
        public Vector3 size = new Vector3(60f, 30f, 60f);

        [Tooltip("Gizmo colour for the volume wireframe.")]
        public Color gizmoColor = new Color(0.2f, 0.6f, 1f, 0.5f);

        /// <summary>World-space axis-aligned bounds of the ocean volume.</summary>
        public Bounds Bounds => new Bounds(transform.position, size);

        /// <summary>Clamp a world position so it stays inside the volume.</summary>
        public Vector3 Clamp(Vector3 worldPos)
        {
            Bounds b = Bounds;
            return new Vector3(
                Mathf.Clamp(worldPos.x, b.min.x, b.max.x),
                Mathf.Clamp(worldPos.y, b.min.y, b.max.y),
                Mathf.Clamp(worldPos.z, b.min.z, b.max.z));
        }

        /// <summary>A uniformly random point inside the volume.</summary>
        public Vector3 RandomPointInside()
        {
            Bounds b = Bounds;
            return new Vector3(
                Random.Range(b.min.x, b.max.x),
                Random.Range(b.min.y, b.max.y),
                Random.Range(b.min.z, b.max.z));
        }

        void OnDrawGizmos()
        {
            Gizmos.color = gizmoColor;
            Gizmos.DrawWireCube(transform.position, size);
        }
    }
}
