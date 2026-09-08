using UnityEngine;
using UnityEngine.InputSystem;
using FishSwarm.Predators;

public class CameraController : MonoBehaviour
{
    // Attach this to the active Game camera. Disable CameraOrbit on the same camera,
    // otherwise both scripts will try to write the camera transform.

    [Header("Free camera")]
    [Tooltip("Movement speed for WASD/Space/Shift controls.")]
    public float moveSpeed = 25f;
    [Tooltip("Speed multiplier while Ctrl is held.")]
    public float fastMultiplier = 2.5f;
    [Tooltip("Mouse-look sensitivity while right mouse is held.")]
    public float lookSensitivity = 0.12f;
    [Tooltip("How far each mouse-wheel scroll step moves the camera along its view direction.")]
    public float scrollZoomSpeed = 20f; // change value if you want the zoom to be faster or slower

    [Header("Predator third-person")]
    [Tooltip("Distance behind the predator while predator view is active.")]
    public float predatorFollowDistance = 18f;
    [Tooltip("Height above the predator while predator view is active.")]
    public float predatorFollowHeight = 7f;
    [Tooltip("How quickly the camera catches up to the predator.")]
    public float predatorFollowSharpness = 8f;
    [Tooltip("How quickly the inferred predator heading turns. Lower values reduce wobble.")]
    public float predatorHeadingSharpness = 4f;
    [Tooltip("Look slightly ahead of the predator instead of directly at its centre.")]
    public float predatorLookAhead = 8f;

    [Header("Input")]
    public Key predatorViewKey = Key.F;
    [Tooltip("Selects the previous predator while predator view is active.")]
    public Key previousPredatorKey = Key.A;
    [Tooltip("Selects the next predator while predator view is active.")]
    public Key nextPredatorKey = Key.D;

    [Header("Predator view HUD")]
    [Tooltip("Shows the selected predator number and its current state.")]
    public bool showPredatorSelectionHud = true;

    float _yaw;
    float _pitch;
    bool _predatorView;
    Predator _selectedPredator;
    Vector3 _lastPredatorPosition;
    Vector3 _predatorForward = Vector3.forward;
    bool _hasPredatorHistory;
    bool _ignoreFirstMouseDelta;
    GUIStyle _predatorHudStyle;

    void Start()
    {
        // Store yaw/pitch separately so right-mouse drag can rotate from the current view
        // instead of rebuilding from a default orientation.
        Vector3 euler = transform.rotation.eulerAngles;
        _yaw = euler.y;
        _pitch = NormalizePitch(euler.x);
    }

    void Update()
    {
        // Input toggles are read in Update so single-frame key presses are not missed.
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (kb[Key.Escape].wasPressedThisFrame)
            Application.Quit();

        if (kb[predatorViewKey].wasPressedThisFrame)
            TogglePredatorView();

        // A/D remain free-camera strafe controls outside predator view.
        if (_predatorView)
        {
            if (kb[previousPredatorKey].wasPressedThisFrame)
                CyclePredator(-1);
            else if (kb[nextPredatorKey].wasPressedThisFrame)
                CyclePredator(1);
        }
    }

    void LateUpdate()
    {
        // Camera movement happens in LateUpdate so predator follow runs after predator movement.
        if (_predatorView)
            UpdatePredatorView();
        else
            UpdateFreeCamera();
    }

    void UpdateFreeCamera()
    {
        UpdateMouseLook();
        UpdateMovement();
        UpdateScrollZoom();
    }

    void UpdateMouseLook()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        if (mouse.rightButton.wasPressedThisFrame)
        {
            // Sync on RMB press and skip the first delta to avoid a sudden view snap
            // when the Game view captures the mouse.
            SyncAnglesFromTransform();
            _ignoreFirstMouseDelta = true;
        }

        if (!mouse.rightButton.isPressed) return;

        if (_ignoreFirstMouseDelta)
        {
            _ignoreFirstMouseDelta = false;
            return;
        }

        Vector2 delta = mouse.delta.ReadValue();
        _yaw += delta.x * lookSensitivity;
        _pitch = Mathf.Clamp(_pitch - delta.y * lookSensitivity, -89f, 89f);
        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    void UpdateMovement()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        // Build input in camera-local horizontal axes, with world up/down for vertical travel.
        Vector3 input = Vector3.zero;
        if (kb.wKey.isPressed) input += Vector3.forward;
        if (kb.sKey.isPressed) input += Vector3.back;
        if (kb.dKey.isPressed) input += Vector3.right;
        if (kb.aKey.isPressed) input += Vector3.left;
        if (kb.spaceKey.isPressed) input += Vector3.up;
        if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) input += Vector3.down;

        if (input.sqrMagnitude < 1e-6f) return;

        float speed = moveSpeed;
        if (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)
            speed *= fastMultiplier;

        Vector3 move =
            transform.forward * input.z +
            transform.right * input.x +
            Vector3.up * input.y;

        transform.position += move.normalized * speed * Time.deltaTime;
    }

    void UpdateScrollZoom()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f) return;

        // Scroll zoom is positional, not FOV-based, so it behaves like moving forward/backward.
        transform.position += transform.forward * (scroll * scrollZoomSpeed * Time.deltaTime);
    }

    void UpdatePredatorView()
    {
        Predator predator = CurrentPredator();
        if (predator == null)
        {
            _predatorView = false;
            return;
        }

        Vector3 predatorPos = predator.transform.position;
        Vector3 forward = PredatorForward(predatorPos);
        Vector3 desiredPos = predatorPos - forward * predatorFollowDistance + Vector3.up * predatorFollowHeight;
        // Looking ahead of the predator smooths the chase camera and avoids staring at its centre.
        Vector3 lookTarget = predatorPos + forward * predatorLookAhead;
        Quaternion desiredRot = Quaternion.LookRotation(lookTarget - desiredPos, Vector3.up);
        float t = 1f - Mathf.Exp(-predatorFollowSharpness * Time.deltaTime);

        transform.position = Vector3.Lerp(transform.position, desiredPos, t);
        transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, t);

        Vector3 euler = transform.rotation.eulerAngles;
        _yaw = euler.y;
        _pitch = NormalizePitch(euler.x);
    }

    Predator CurrentPredator()
    {
        if (Predator.All.Count == 0)
        {
            _selectedPredator = null;
            return null;
        }

        // Preserve the last selection across view toggles. If that predator was removed,
        // gracefully fall back to the first currently active predator.
        if (_selectedPredator == null || !Predator.All.Contains(_selectedPredator))
            _selectedPredator = Predator.All[0];

        return _selectedPredator;
    }

    void TogglePredatorView()
    {
        if (_predatorView)
        {
            _predatorView = false;
        }
        else
        {
            // Do not enter a view with no valid follow target.
            if (CurrentPredator() == null) return;
            _predatorView = true;
        }

        ResetPredatorTracking();
        SyncAnglesFromTransform();
    }

    void CyclePredator(int direction)
    {
        int count = Predator.All.Count;
        if (count == 0)
        {
            _selectedPredator = null;
            _predatorView = false;
            return;
        }

        Predator current = CurrentPredator();
        int index = Predator.All.IndexOf(current);
        index = (index + direction + count) % count;
        _selectedPredator = Predator.All[index];

        // Clear motion history so the new predator does not inherit the previous one's heading.
        ResetPredatorTracking();
    }

    void ResetPredatorTracking()
    {
        _hasPredatorHistory = false;
        if (_selectedPredator == null) return;

        _lastPredatorPosition = _selectedPredator.transform.position;
        Vector3 forward = _selectedPredator.transform.forward;
        _predatorForward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
    }

    void OnGUI()
    {
        if (!_predatorView || !showPredatorSelectionHud) return;

        Predator predator = CurrentPredator();
        int index = predator != null ? Predator.All.IndexOf(predator) : -1;
        if (index < 0) return;

        if (_predatorHudStyle == null)
        {
            _predatorHudStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 16,
                fontStyle = FontStyle.Bold,
            };
        }

        const float width = 300f;
        Rect rect = new Rect((Screen.width - width) * 0.5f, 16f, width, 34f);
        GUI.Box(rect,
            $"Predator {index + 1} / {Predator.All.Count}  •  {predator.state}  [A / D]",
            _predatorHudStyle);
    }

    Vector3 PredatorForward(Vector3 currentPosition)
    {
        // The predator may not always rotate to face its movement, so infer heading from position
        // deltas and smooth it to avoid camera wobble when the predator changes direction.
        Vector3 targetForward = _predatorForward;
        if (_hasPredatorHistory)
        {
            Vector3 delta = currentPosition - _lastPredatorPosition;
            if (delta.sqrMagnitude > 1e-6f)
                targetForward = delta.normalized;
        }
        else
        {
            targetForward = transform.forward.sqrMagnitude > 1e-6f ? transform.forward : Vector3.forward;
        }

        _lastPredatorPosition = currentPosition;
        _hasPredatorHistory = true;

        float t = 1f - Mathf.Exp(-predatorHeadingSharpness * Time.deltaTime);
        _predatorForward = Vector3.Slerp(_predatorForward, targetForward, t).normalized;
        return _predatorForward;
    }

    void SyncAnglesFromTransform()
    {
        // Keeps free-look state aligned after predator view or any external camera rotation.
        Vector3 euler = transform.rotation.eulerAngles;
        _yaw = euler.y;
        _pitch = NormalizePitch(euler.x);
    }

    static float NormalizePitch(float pitch)
    {
        return pitch > 180f ? pitch - 360f : pitch;
    }
}
