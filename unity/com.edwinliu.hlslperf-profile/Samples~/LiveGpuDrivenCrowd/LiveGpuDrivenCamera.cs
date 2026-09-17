using UnityEngine;

namespace EdwinLiu.HlslPerf.Samples
{
    [DisallowMultipleComponent]
    public sealed class LiveGpuDrivenCamera : MonoBehaviour
    {
        [SerializeField] private float moveSpeed = 70f;
        [SerializeField] private float fastMultiplier = 4f;
        [SerializeField] private float lookSensitivity = 0.16f;

        private float yaw;
        private float pitch;

        private void OnEnable()
        {
            Vector3 euler = transform.eulerAngles;
            yaw = euler.y;
            pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        private void Update()
        {
            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxisRaw("Mouse X") * lookSensitivity * 10f;
                pitch -= Input.GetAxisRaw("Mouse Y") * lookSensitivity * 10f;
                pitch = Mathf.Clamp(pitch, -85f, 85f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
                Cursor.lockState = CursorLockMode.Locked;
            }
            else if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
            }

            Vector3 local = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
            if (Input.GetKey(KeyCode.E)) local.y += 1f;
            if (Input.GetKey(KeyCode.Q)) local.y -= 1f;
            if (local.sqrMagnitude > 1f) local.Normalize();

            float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? fastMultiplier : 1f);
            transform.position += transform.TransformDirection(local) * speed * Time.unscaledDeltaTime;
        }
    }
}
