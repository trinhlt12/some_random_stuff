using UnityEngine;

namespace DayNightSky
{
    public enum RainTestMotion
    {
        Still,
        /// <summary>Turns on the spot (yaw) with a gentle pitch and roll sway.</summary>
        LookAround,
        /// <summary>Walks back and forth along the view direction.</summary>
        Walk,
        /// <summary>Walks and teleports every Teleport Interval seconds.</summary>
        WalkAndTeleport,
    }

    /// <summary>
    /// Test helper for rain: moves the camera without any input (works with either input system).
    /// Use the Inspector modes or <see cref="TeleportNow"/>. Not needed in a game.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RainTestCameraMotion : MonoBehaviour
    {
        public RainTestMotion motion = RainTestMotion.Still;
        [Tooltip("Degrees per second for Look Around.")]
        public float turnSpeed = 20f;
        [Tooltip("Pitch/roll sway amplitude for Look Around (degrees).")]
        public float sway = 20f;
        [Tooltip("Walking speed (m/s) for Walk modes.")]
        public float walkSpeed = 4f;
        [Tooltip("Half length of the walk (m).")]
        public float walkDistance = 20f;
        [Tooltip("Distance of a teleport jump (m).")]
        public float teleportJump = 200f;
        [Min(0.5f)] public float teleportInterval = 6f;

        Vector3 origin;
        Quaternion baseRotation;
        float time, nextTeleport;
        Vector3 teleportOffset;

        void OnEnable()
        {
            origin = transform.position;
            baseRotation = transform.rotation;
            time = 0f;
            nextTeleport = teleportInterval;
            teleportOffset = Vector3.zero;
        }

        void OnDisable()
        {
            transform.SetPositionAndRotation(origin, baseRotation);
        }

        /// <summary>Jumps the camera by Teleport Jump along world X (and back on the next call).</summary>
        public void TeleportNow()
        {
            teleportOffset = teleportOffset.sqrMagnitude > 0f ? Vector3.zero : Vector3.right * teleportJump;
            if (motion == RainTestMotion.Still || motion == RainTestMotion.LookAround)
                transform.position = origin + teleportOffset;
        }

        void Update()
        {
            time += Time.deltaTime;
            switch (motion)
            {
                case RainTestMotion.Still:
                    break;
                case RainTestMotion.LookAround:
                    var e = baseRotation.eulerAngles;
                    transform.rotation = Quaternion.Euler(e.x + Mathf.Sin(time * 0.5f) * sway * 0.5f,
                                                          e.y + time * turnSpeed,
                                                          Mathf.Sin(time * 0.8f) * sway);
                    break;
                case RainTestMotion.Walk:
                case RainTestMotion.WalkAndTeleport:
                    Vector3 fwd = baseRotation * Vector3.forward;
                    fwd.y = 0f;
                    fwd.Normalize();
                    float phase = Mathf.PingPong(time * walkSpeed, walkDistance * 2f) - walkDistance;
                    if (motion == RainTestMotion.WalkAndTeleport && time >= nextTeleport)
                    {
                        nextTeleport = time + teleportInterval;
                        TeleportNow();
                    }
                    transform.position = origin + teleportOffset + fwd * phase;
                    break;
            }
        }
    }
}
