using System.Collections.Generic;
using UnityEngine;

namespace FishSwarm.Behavior
{
    /// <summary>
    /// A seabed feeding spot. Unthreatened fish that wander near one may break formation to peck at
    /// it (the Foraging state). Self-registers so the FishManager can read all nodes cheaply. The
    /// seabed placement/visuals are dressed up in Step 4; this is just the marker + registration.
    /// </summary>
    public class ForagingNode : MonoBehaviour
    {
        public static readonly List<ForagingNode> All = new List<ForagingNode>();

        void OnEnable() { All.Add(this); }
        void OnDisable() { All.Remove(this); }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.3f, 0.85f, 0.35f, 0.7f);
            Gizmos.DrawWireSphere(transform.position, 10f);
        }
    }
}
