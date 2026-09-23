using UnityEngine;

namespace Week1.Runtime
{
    /// <summary>
    /// Slowly orbits the camera around the village so the scene presents itself on Play.
    /// Arrow keys nudge the orbit; it resumes drifting once released.
    /// </summary>
    public class OrbitCameraController : MonoBehaviour
    {
        public Vector3 target = Vector3.zero;
        public float radius = 140f;
        public float height = 50f;
        public float degreesPerSecond = 3.5f;
        public float manualSpeed = 30f;

        float _angle;

        void Start()
        {
            Vector3 offset = transform.position - target;
            _angle = Mathf.Atan2(offset.z, offset.x) * Mathf.Rad2Deg;
        }

        void Update()
        {
            _angle += (degreesPerSecond + Input.GetAxis("Horizontal") * manualSpeed) * Time.deltaTime;

            float r = _angle * Mathf.Deg2Rad;
            transform.position = target + new Vector3(Mathf.Cos(r) * radius, height, Mathf.Sin(r) * radius);
            transform.rotation = Quaternion.LookRotation((target - transform.position).normalized, Vector3.up);
        }
    }
}
