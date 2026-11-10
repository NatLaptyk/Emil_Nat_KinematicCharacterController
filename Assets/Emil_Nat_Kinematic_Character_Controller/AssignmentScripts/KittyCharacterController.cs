using KinematicCharacterController;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Third-person character controller built on top of the Kinematic Character Controller (KCC) asset.
/// This class does not move the character by itself. It implements ICharacterController, which is the
/// contract the KinematicCharacterMotor uses to ask us questions every physics step: which way should
/// the character face (UpdateRotation), how fast should it move (UpdateVelocity), and so on.
/// The motor then does the hard part: moving the capsule, sliding along walls, stepping over stairs
/// and keeping the character glued to the ground. Our job here is only to decide the intent.
/// </summary>
[RequireComponent(typeof(KinematicCharacterMotor))]
public class KittyCharacterController : MonoBehaviour, ICharacterController
{
    #region Types

    // Which set of movement rules is in charge
    private enum MovementMode
    {
        Normal,
        Climbing
    }

    // The three parts of a climb: sliding onto the ladder, moving along it, sliding off it
    private enum ClimbPhase
    {
        Anchoring,
        Climbing,
        Releasing
    }

    #endregion

    #region Inspector Settings

    // Input System action that gives the move direction as a Vector2: x is left/right, y is forward/back
    public InputActionReference MoveAction;

    // Input System action for the jump button; read as a press, latched in Update
    public InputActionReference JumpAction;

    // Camera the player looks through; movement input is interpreted relative to where it faces
    public CinemachineCamera PlayerCamera;

    // Top speed, in metres per second, while standing on stable ground
    public float MaxGroundSpeed = 5f;

    // Top speed, in metres per second, that air control can steer the character towards while airborne
    public float MaxAirSpeed = 3f;

    // How quickly the ground velocity closes the gap to its target; higher feels snappier, lower feels slippery
    public float GroundAcceleration = 2.5f;

    // How quickly the air velocity closes the gap to its target; usually lower than on the ground
    public float AirAcceleration = 2f;

    // Upward speed, in metres per second, given to the character the instant it jumps
    public float JumpStrength = 10f;

    // Upward speed, in metres per second, given to the character by a jump started in the air
    public float AirJumpStrength = 10f;

    // Horizontal speed, in metres per second, given to an air jump in the direction the movement is pushed.
    public float AirJumpSideSpeed = 6f;

    // How long, in seconds, a jump press stays valid while the character cannot jump yet
    public float JumpBufferTime = 0.1f;

    // How quickly the character turns to face its movement direction; higher turns faster
    public float RotationRate = 5f;

    // Sideways speed, in metres per second, of a leap from a ladder
    public float LeapSideSpeed = 6f;

    // Upward speed, in metres per second, of a leap from a ladder;
    // with gravity it sets how long the leap stays in the air
    public float LeapUpSpeed = 4f;

    // Acceleration applied while airborne, in metres per second squared; points down so the character falls
    public Vector3 Gravity = new Vector3(0, -10, 0);

    // Time, in seconds, after walking off a ledge during which a jump is still accepted
    public float CoyoteTime = 0.2f;
    // Input System action for running; held = run, released = walk
    public InputActionReference RunAction;

    // Input System action that grabs a ladder in reach, or lets go while climbing
    public InputActionReference ClimbAction;

    // Top speed, in metres per second, on the ground while the run button is held
    public float MaxRunSpeed = 9f;

    // Speed, in metres per second, that air steering pulls the horizontal 
    // velocity toward while the run button is held
    public float MaxRunAirSpeed = 9f;

    // How many jumps the character may start in the air before it has to touch stable ground again
    public int MaxAirJumps = 1;

    // Speed along the ladder, in metres per second, at full stick
    public float ClimbSpeed = 3f;

    // Seconds taken to slide onto the ladder when grabbing it, and to slide to an exit point when leaving it
    public float AnchorDuration = 0.25f;

    // Physics layer(s) the ladder trigger colliders live on
    public LayerMask LadderLayers;

    // Calling the animator
    [Header("Animation")]

    // Animator on the cat model that plays the movement clips
    [SerializeField] private Animator _animator;

    #endregion

    #region Runtime State

    // Remaining coyote window in seconds: refilled while grounded, drains while airborne, spent by a jump
    private float _coyoteTimeCounter;

    // Air jumps left before the next landing; 
    // refilled on stable ground, spent one per air jump
    private int _airJumpsRemaining;

    // Seconds since the current jump request was made; compared against JumpBufferTime
    private float _timeSinceJumpRequested;

    // Direction the player wants to move this update, already rotated to match the camera; length 0 to 1
    private Vector3 _moveInput;

    // Direction the character should turn towards this update; currently the same as _moveInput
    private Vector3 _lookInput;

    // The KCC motor on this GameObject; it owns the capsule, the collisions and the ground detection
    private KinematicCharacterMotor _kinematicMotor;

    // Which movement rules are in charge right now
    private MovementMode _movementMode = MovementMode.Normal;

    // Which part of the climb we are in; only meaningful while _movementMode is Climbing
    private ClimbPhase _climbPhase;

    // The ladder being climbed; null while not climbing
    private KittyLadder _activeLadder;

    // Raw forward/back stick value, before any camera rotation;
    // moves the cat up and down the ladder
    private float _climbInput;

    // Raw left/right stick value, before any camera rotation;
    // picks the direction of a leap from a ladder
    private float _climbSideInput;

    // True from the moment the cat leaps off a ladder until it catches another one or lands
    private bool _isLeaping;

    // The ladder the cat just leapt from, ignored by the catch so it does not grab the same one again
    private KittyLadder _lastLadder;

    // True while the run button is held
    private bool _runHeld;

    // Set by Update when the climb button goes down, cleared by the motor update that handles it
    private bool _climbRequested;

    // Set by Update on the frame the jump button goes down; 
    // cleared by the jump that uses it, or by the buffer expiring
    private bool _jumpRequested;

    // Where the cat was when the current slide (anchoring or releasing) began
    private Vector3 _anchorStartPosition;
    private Quaternion _anchorStartRotation;

    // Where the current slide ends; while climbing, the rotation is the one that faces the ladder
    private Vector3 _anchorTargetPosition;
    private Quaternion _anchorTargetRotation;

    // Seconds since the current slide began
    private float _anchorTimer;

    // The cat's rotation before it grabbed the ladder, restored when it lets go mid-ladder
    private Quaternion _rotationBeforeClimb;

    // Reused buffer for the ladder overlap test; the motor writes the colliders it finds into it
    private readonly Collider[] _overlapResults = new Collider[8];

    #endregion

    #region Unity Functions

    /// <summary>
    /// Unity calls Awake once when the GameObject is loaded, before Start and before the first physics step.
    /// Per the course conventions the Unity message only delegates to a named method.
    /// </summary>
    private void Awake()
    {
        Initialize();
    }

    /// <summary>
    /// Unity calls Update once per rendered frame. Press-type input has to be caught here: a press is only visible
    /// for one frame, and a frame does not always contain a motor update.
    /// </summary>
    private void Update()
    {
        ReadPressedInputs();
    }
    #endregion

    #region Setup

    /// <summary>
    /// Fetches the KinematicCharacterMotor on this GameObject and registers this script as its controller.
    /// That registration is what makes the motor call our ICharacterController methods every physics step;
    /// without it the motor would have nobody to ask for a velocity and the character would never move.
    /// TryGetComponent is used so a missing motor produces a readable error here instead of a
    /// NullReferenceException somewhere inside the motor later.
    /// </summary>
    private void Initialize()
    {
        if (!TryGetComponent(out _kinematicMotor))
        {
            Debug.LogError("KittyCharacterController needs a KinematicCharacterMotor on the same GameObject.", this);
            return;
        }

        _kinematicMotor.CharacterController = this;
        //_animator = GetComponent<Animator>();
    }

    #endregion

    #region Input

    /// <summary>
    /// First callback of every motor update, before the motor probes the ground or moves anything.
    /// Reads the held inputs, then handles a pending climb press, then tries to catch a ladder if the cat
    /// is in the middle of a leap, so every later callback in the same update works from the same state.
    /// </summary>
    /// <param name="deltaTime">Duration of the current motor update in seconds (the fixed timestep, 0.02 by default).</param>
    public void BeforeCharacterUpdate(float deltaTime)
    {
        UpdateInputs();
        HandleClimbRequest();
        TryCatchLadder();
    }

    /// <summary>
    /// Reads the Input System actions and stores the results in the private input fields.
    /// The raw stick or WASD value is a 2D vector, so it is converted to 3D (x stays x, y becomes z)
    /// and clamped to a length of 1 so a diagonal is not faster than a straight line.
    /// It is then rotated to match the camera, so "forward" means "away from the camera"
    /// rather than "along the world's Z axis".
    /// </summary>
    private void UpdateInputs()
    {
        // Raw input in the stick's own 2D space
        Vector2 moveActionInput = MoveAction.action.ReadValue<Vector2>();

        // Raw forward/back for the ladder, before the camera rotation: push forward to climb up
        _climbInput = moveActionInput.y;

        //Raw left/right for leaping off a ladder,
        //before the camera rotation: left means left along the wall
        _climbSideInput = moveActionInput.x;

        // Lay it flat on the XZ ground plane; a keyboard gives whole numbers, a stick gives fractions
        _moveInput = new Vector3(moveActionInput.x, 0, moveActionInput.y);

        // Two keyboard keys at once would give a length of 1.41, which would be a free speed boost
        _moveInput = Vector3.ClampMagnitude(_moveInput, 1);

        // Rotate the input so it is relative to where the camera looks
        _moveInput = GetCameraPlanarRotation(PlayerCamera.State.GetFinalOrientation()) * _moveInput;

        // The character turns towards the direction it moves in
        _lookInput = _moveInput;

        // Held, not pressed: run for as long as the button is down
        _runHeld = (RunAction.action.ReadValue<float>() > 0);
    }

    /// <summary>
    /// Consumes a climb button press. Not climbing: looks for a ladder trigger overlapping the capsule and grabs it.
    /// Already climbing: lets go where the cat is, so gravity takes over.
    /// </summary>
    private void HandleClimbRequest()
    {
        if (!_climbRequested)
        {
            return;
        }

        _climbRequested = false;

        if (_movementMode == MovementMode.Climbing)
        {
            ReleaseLadder(_kinematicMotor.TransientPosition, _rotationBeforeClimb);
            return;
        }

        KittyLadder ladder = FindLadderInReach(null);

        if (ladder == null)
        {
            return;
        }

        GrabLadder(ladder);
    }

    /// <summary>
    /// Asks the motor which colliders on the ladder layers overlap the capsule right now, triggers included,
    /// and returns the first one that belongs to a ladder, skipping the one it is told to ignore.
    /// </summary>
    /// <param name="ignoredLadder">A ladder to leave out of the search, such as the one just leapt from; null to consider all of them.</param>
    /// <returns>The ladder in reach, or null if there is none.</returns>
    private KittyLadder FindLadderInReach(KittyLadder ignoredLadder)
    {
        int overlapCount = _kinematicMotor.CharacterOverlap(_kinematicMotor.TransientPosition, _kinematicMotor.TransientRotation, _overlapResults, LadderLayers, QueryTriggerInteraction.Collide);

        for (int i = 0; i < overlapCount; i++)
        {
            if (_overlapResults[i].TryGetComponent(out KittyLadder ladder))
            {
                if (ladder == ignoredLadder)
                {
                    continue;
                }

                return ladder;
            }

        }

        return null;
    }

    /// <summary>
    /// Runs every update while the cat is leaping between ladders: grabs the first ladder in reach other than
    /// the one it just left. Does nothing when the cat is not leaping or no other ladder is touching the capsule.
    /// </summary>
    private void TryCatchLadder()
    {
        if (!_isLeaping)
        {
            return;
        }

        KittyLadder ladder = FindLadderInReach(_lastLadder);

        if (ladder == null)
        {
            return;
        }

        GrabLadder(ladder);
    }

    /// <summary>
    /// Flattens the camera's rotation onto the ground plane so it can be used to rotate the movement input.
    /// A third-person camera usually looks down at the character; using its full rotation would tilt the
    /// input into the ground. Projecting the camera's forward vector onto the plane perpendicular to the
    /// character's up removes that tilt. If the camera looks straight down, its forward vector disappears
    /// when flattened, so the camera's up vector is used as a fallback.
    /// </summary>
    /// <param name="cameraRotation">Current world rotation of the camera.</param>
    /// <returns>A rotation whose forward lies flat on the ground and points the way the camera faces.</returns>
    private Quaternion GetCameraPlanarRotation(Quaternion cameraRotation)
    {
        // The camera's forward vector with the up/down component removed
        Vector3 cameraPlanarDirection = Vector3.ProjectOnPlane(cameraRotation * Vector3.forward, _kinematicMotor.CharacterUp).normalized;

        // Camera looking straight down or up: the flattened forward is zero, so use the camera's up instead
        if (cameraPlanarDirection.sqrMagnitude == 0)
        {
            cameraPlanarDirection = Vector3.ProjectOnPlane(cameraRotation * Vector3.up, _kinematicMotor.CharacterUp).normalized;
        }

        return Quaternion.LookRotation(cameraPlanarDirection, _kinematicMotor.CharacterUp);
    }

    /// <summary>
    /// Latches button presses that must be handled exactly once. A press is only visible during the frame it
    /// happens in, and a motor update does not run every frame, so the flag carries it to the next update.
    /// Each flag is cleared by the code that acts on it, never here.
    /// </summary>
    private void ReadPressedInputs()
    {
        if (ClimbAction.action.WasPressedThisFrame())
        {
            _climbRequested = true;
        }

        if (JumpAction.action.WasPressedThisFrame())
        {
            _jumpRequested = true;
            _timeSinceJumpRequested = 0f;
        }
    }

    #endregion

    #region Rotation

    /// <summary>
    /// Called by the motor every update to ask which way the character should face.
    /// currentRotation is passed by reference: whatever is left in it when this method returns is the
    /// rotation the motor applies. On a ladder the climbing rules decide. During a leap between ladders the
    /// rotation is left alone, so the cat keeps facing the wall. Otherwise the cat turns toward the input with
    /// Slerp, a fraction of the way each update, and keeps its last facing when there is no input.
    /// </summary>
    /// <param name="currentRotation">The character's rotation; modify it to turn the character.</param>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        if (_movementMode == MovementMode.Climbing)
        {
            ClimbingRotation(ref currentRotation);
            return;
        }
        // Mid-leap between ladders: keep facing the wall
        if (_isLeaping)
        {
            return;
        }

        if (_lookInput == Vector3.zero)
        {
            return;
        }

        // Blend a fraction of the way from the current facing towards the input direction
        Vector3 targetLookDirection = Vector3.Slerp(_kinematicMotor.CharacterForward, _lookInput, deltaTime * RotationRate);

        currentRotation = Quaternion.LookRotation(targetLookDirection, _kinematicMotor.CharacterUp);
    }

    /// <summary>
    /// Rotation while on a ladder. During a slide it is blended from where the slide started to the slide's target;
    /// while climbing it stays locked to the target of the anchoring slide, which faces the ladder.
    /// </summary>
    /// <param name="currentRotation">The character's rotation; modify it to turn the character.</param>
    private void ClimbingRotation(ref Quaternion currentRotation)
    {
        if (_climbPhase == ClimbPhase.Climbing)
        {
            currentRotation = _anchorTargetRotation;
            return;
        }

        float slideProgress = Mathf.Clamp01(_anchorTimer / AnchorDuration);

        currentRotation = Quaternion.Slerp(_anchorStartRotation, _anchorTargetRotation, slideProgress);
    }

    #endregion

    #region Climbing

    /// <summary>
    /// Switches to climbing. Any grab ends a leap, so the leap state is cleared first. Collision and ground
    /// solving are turned off so the motor moves the cat exactly where the climbing code asks, with no walls or
    /// floor interfering, and the cat starts sliding onto the closest point of the ladder line while turning
    /// to face the ladder.
    /// </summary>
    /// <param name="ladder">The ladder to climb.</param>
    private void GrabLadder(KittyLadder ladder)
    {
        // Any grab ends a leap, whether the cat caught the ladder or the player pressed the climb button
        _isLeaping = false;
        _lastLadder = null;
        _activeLadder = ladder;
        _rotationBeforeClimb = _kinematicMotor.TransientRotation;
        _movementMode = MovementMode.Climbing;

        _kinematicMotor.SetMovementCollisionsSolvingActivation(false);
        _kinematicMotor.SetGroundSolvingActivation(false);

        // Grabbing from the ground would otherwise leave a full coyote window for a jump straight off the ladder
        _coyoteTimeCounter = 0f;

        // The discard (out _) means we only want the point here, not the overshoot
        Vector3 pointOnLadder = _activeLadder.ClosestPointOnLadder(_kinematicMotor.TransientPosition, out _);
        Quaternion facingLadder = Quaternion.LookRotation(_activeLadder.transform.forward, _kinematicMotor.CharacterUp);

        StartSlide(ClimbPhase.Anchoring, pointOnLadder, facingLadder);
    }

    /// <summary>
    /// Throws the cat sideways off the ladder it is climbing. The ladder is remembered before climbing is exited,
    /// because exiting clears it. The velocity is assigned outright, since there is no momentum to keep from a
    /// ladder: sideways along the wall in the direction of the stick, plus a little upward speed for airtime.
    /// Marks the cat as leaping, which locks its facing and lets it catch the next ladder. Consumes the jump request.
    /// </summary>
    /// <param name="currentVelocity">The character's velocity to modify.</param>
    private void LeapFromLadder(ref Vector3 currentVelocity)
    {
        Vector3 wallRight = Vector3.Cross(_kinematicMotor.CharacterUp, _kinematicMotor.CharacterForward);
        Vector3 leapDirection = wallRight * Mathf.Sign(_climbSideInput);
        _lastLadder = _activeLadder;
        ExitClimbing();
        _kinematicMotor.ForceUnground();
        currentVelocity = leapDirection * LeapSideSpeed + _kinematicMotor.CharacterUp * LeapUpSpeed;
        _isLeaping = true;
        _jumpRequested = false;
    }

    /// <summary>
    /// Starts the slide away from the ladder toward a position and rotation. When it ends, the cat is back under
    /// the normal movement rules.
    /// </summary>
    /// <param name="targetPosition">Where the slide ends.</param>
    /// <param name="targetRotation">How the cat is rotated when the slide ends.</param>
    private void ReleaseLadder(Vector3 targetPosition, Quaternion targetRotation)
    {
        StartSlide(ClimbPhase.Releasing, targetPosition, targetRotation);
    }

    /// <summary>
    /// Enters a slide phase and records where the slide starts and ends, so the rotation and velocity code can
    /// interpolate between the two over AnchorDuration.
    /// </summary>
    /// <param name="phase">Anchoring or Releasing.</param>
    /// <param name="targetPosition">Where the cat should be when the slide ends.</param>
    /// <param name="targetRotation">How the cat should be rotated when the slide ends.</param>
    private void StartSlide(ClimbPhase phase, Vector3 targetPosition, Quaternion targetRotation)
    {
        _climbPhase = phase;
        _anchorTimer = 0f;
        _anchorStartPosition = _kinematicMotor.TransientPosition;
        _anchorStartRotation = _kinematicMotor.TransientRotation;
        _anchorTargetPosition = targetPosition;
        _anchorTargetRotation = targetRotation;
    }

    /// <summary>
    /// Runs the climb's phase changes once the motor has moved the cat for this update.
    /// Climbing: if the cat has gone past either end of the ladder line, start sliding to that end's exit point.
    /// Sliding: when the timer has run out, start climbing or leave the ladder; otherwise advance the timer.
    /// The timer is checked before it is advanced, so the last update of a slide uses progress 1 and lands exactly on the target.
    /// </summary>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    private void UpdateClimbing(float deltaTime)
    {
        if (_climbPhase == ClimbPhase.Climbing)
        {
            CheckForLadderEnds();
            return;
        }

        if (_anchorTimer >= AnchorDuration)
        {
            FinishSlide();
            return;
        }

        _anchorTimer += deltaTime;
    }

    /// <summary>
    /// Asks the ladder how far past its ends the cat is. The 5 cm tolerance stops a cat resting exactly on an anchor
    /// from being thrown off by rounding.
    /// </summary>
    private void CheckForLadderEnds()
    {
        _activeLadder.ClosestPointOnLadder(_kinematicMotor.TransientPosition, out float overshoot);

        if (overshoot > 0.05f)
        {
            ReleaseLadder(_activeLadder.TopExit.position, _activeLadder.TopExit.rotation);
            return;
        }

        if (overshoot < -0.05f)
        {
            ReleaseLadder(_activeLadder.BottomExit.position, _activeLadder.BottomExit.rotation);
        }
    }

    /// <summary>
    /// Called when a slide has reached its target: an anchoring slide turns into climbing, a releasing slide ends the climb.
    /// </summary>
    private void FinishSlide()
    {
        if (_climbPhase == ClimbPhase.Anchoring)
        {
            _climbPhase = ClimbPhase.Climbing;
            return;
        }

        ExitClimbing();
    }

    /// <summary>
    /// Leaves climbing: hands collision and ground solving back to the motor and forgets the ladder.
    /// </summary>
    private void ExitClimbing()
    {
        _kinematicMotor.SetMovementCollisionsSolvingActivation(true);
        _kinematicMotor.SetGroundSolvingActivation(true);
        _movementMode = MovementMode.Normal;
        _activeLadder = null;
    }

    /// <summary>
    /// Last callback of every motor update, after the character has been moved. Runs the jump buffer timer
    /// and the animation update in every mode, then the climbing phase changes while on a ladder, because
    /// those need the position the motor actually reached this update.
    /// </summary>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    public void AfterCharacterUpdate(float deltaTime)
    {
        UpdateJumpBuffer(deltaTime);
        UpdateAnimation();

        if (_movementMode != MovementMode.Climbing)
        {
            return;
        }

        UpdateClimbing(deltaTime);
    }

    /// <summary>
    /// Ages a pending jump request and drops it once it is older than JumpBufferTime, so a press made long
    /// before landing does not fire on touchdown. Does nothing while no request is pending.
    /// </summary>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    private void UpdateJumpBuffer(float deltaTime)
    {
        if (!_jumpRequested)
        {
            return;
        }

        _timeSinceJumpRequested += deltaTime;

        if (_timeSinceJumpRequested > JumpBufferTime)
        {
            _jumpRequested = false;
        }
    }
    /// <summary>
    /// Feeds the Animator the state it needs to pick a clip, once per motor update.
    /// isGrounded mirrors the motor's stable-ground result; isRun is true whenever there is movement input;
    /// isJumping is refreshed only while airborne (rising = true, falling = false) and keeps its last value
    /// on the ground, so the Animator can finish a landing transition before the next jump starts.
    /// </summary>
    private void UpdateAnimation()
    {
        Vector3 characterVelocity = _kinematicMotor.Velocity;
        Vector3 inputVelocity = _moveInput;

        _animator.SetBool("isGrounded", _kinematicMotor.GroundingStatus.IsStableOnGround);

        _animator.SetBool("isRun", _moveInput.sqrMagnitude > 0);

        if (!_kinematicMotor.GroundingStatus.IsStableOnGround)
        {
            _animator.SetBool("isJumping", _kinematicMotor.Velocity.y > 0);
        }
    }

    #endregion

    #region Velocity

    /// <summary>
    /// Called by the motor every update to ask how fast, and in which direction, the character should move.
    /// Like UpdateRotation, currentVelocity is passed by reference and the motor uses whatever is left in it.
    /// The motor has already checked the ground for this update, so GroundingStatus tells us whether to use
    /// the ground rules (no gravity, full speed) or the air rules (gravity, limited steering).
    /// Jumping runs last so it can overwrite the vertical velocity the other two produced.
    /// </summary>
    /// <param name="currentVelocity">The character's velocity in metres per second; modify it to move the character.</param>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        if (_movementMode == MovementMode.Climbing)
        {
            ClimbingVelocity(ref currentVelocity, deltaTime);
            return;
        }

        if (_kinematicMotor.GroundingStatus.IsStableOnGround)
        {
            GroundMovement(ref currentVelocity, deltaTime);
        }
        else
        {
            AirMovement(ref currentVelocity, deltaTime);
        }

        ApplyJumping(ref currentVelocity);
    }

    /// <summary>
    /// Velocity while on a ladder. Climbing: a jump press with the stick held sideways starts a leap to the
    /// next ladder; otherwise the cat moves along the ladder line at ClimbSpeed scaled by the stick.
    /// Sliding (anchoring or releasing): work out where the cat should be this update on its way to the target
    /// and ask the motor for the velocity that gets it exactly there. No gravity in any of these cases.
    /// </summary>
    /// <param name="currentVelocity">The character's velocity to modify.</param>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    private void ClimbingVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        if (_climbPhase == ClimbPhase.Climbing)
        {
            // Jump with the stick held sideways: leave this ladder and leap toward the next one
            if (_jumpRequested && Mathf.Abs(_climbSideInput) > 0.1f)
            {
                LeapFromLadder(ref currentVelocity);
                return;
            }

            currentVelocity = _activeLadder.ClimbDirection * _climbInput * ClimbSpeed;
            return;
        }

        float slideProgress = Mathf.Clamp01(_anchorTimer / AnchorDuration);
        Vector3 positionThisUpdate = Vector3.Lerp(_anchorStartPosition, _anchorTargetPosition, slideProgress);

        currentVelocity = _kinematicMotor.GetVelocityForMovePosition(_kinematicMotor.TransientPosition, positionThisUpdate, deltaTime);
    }

    /// <summary>
    /// Movement rules while standing on stable ground.
    /// The target velocity is the input direction scaled to the ground top speed. Lerp then moves the current
    /// velocity a fraction of the way towards that target every update, so the character speeds up when the
    /// player pushes and slows down when the player lets go, instead of changing speed instantly.
    /// No gravity is applied here: the motor keeps the character snapped to stable ground by itself.
    /// </summary>
    /// <param name="currentVelocity">The character's velocity to modify.</param>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    private void GroundMovement(ref Vector3 currentVelocity, float deltaTime)
    {
        float maxSpeed = _runHeld ? MaxRunSpeed : MaxGroundSpeed;
        Vector3 targetVelocity = _moveInput * maxSpeed;
        currentVelocity = Vector3.Lerp(currentVelocity, targetVelocity, deltaTime * GroundAcceleration);
    }

    /// <summary>
    /// Movement rules while airborne, whether jumping or falling.
    /// While the stick is pushed, air steering pulls the velocity toward the input direction at MaxAirSpeed,
    /// or MaxRunAirSpeed while the run button is held, so a running jump keeps its momentum. Without input the
    /// velocity is left alone: there is no air friction. 
    /// Steering is also switched off during a leap between ladders, so the leap follows a predictable arc.
    /// The correction only touches the horizontal part of the velocity:
    /// the difference between the target and the current velocity is projected onto the plane
    /// perpendicular to gravity before it is applied, so steering can never cancel gravity or add lift.
    /// Gravity is then added every update, which is what makes the character fall faster and faster.
    /// </summary>
    /// <param name="currentVelocity">The character's velocity to modify.</param>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    private void AirMovement(ref Vector3 currentVelocity, float deltaTime)
    {
        if (_moveInput.sqrMagnitude > 0 && !_isLeaping)
        {
            // Running carries into the air: aim for the run target while the button is held
            float maxSpeed = _runHeld ? MaxRunAirSpeed : MaxAirSpeed;

            Vector3 targetVelocity = _moveInput * maxSpeed;

            // Keep only the horizontal part of the correction so air control never fights gravity
            Vector3 velocityDiff = Vector3.ProjectOnPlane(targetVelocity - currentVelocity, Gravity);

            currentVelocity += velocityDiff * deltaTime * AirAcceleration;
        }

        // Gravity accumulates every update: the velocity points further down the longer the character falls
        currentVelocity += Gravity * deltaTime;
    }

    #endregion

    #region Jumping

    /// <summary>
    /// The jump itself, shared by ground and air jumps. ForceUnground stops the motor snapping the character
    /// back onto the floor on the next update. The vertical part of the velocity is replaced rather than added
    /// to, so a jump started while falling is as strong as one from the ground. Consumes the jump request.
    /// </summary>
    /// <param name="currentVelocity">The character's velocity to modify.</param>
    /// <param name="strength">Upward speed in metres per second after the jump.</param>
    private void PerformJump(ref Vector3 currentVelocity, float strength)
    {
        _kinematicMotor.ForceUnground();
        Vector3 verticalVelocity = Vector3.Project(currentVelocity, _kinematicMotor.CharacterUp);
        currentVelocity -= verticalVelocity;
        currentVelocity += _kinematicMotor.CharacterUp * strength;

        _jumpRequested = false;

    }

    /// <summary>
    /// The horizontal half of an air jump. Replaces the horizontal part of the velocity with the stick direction
    /// at AirJumpSideSpeed and leaves the vertical part untouched, so the cat can change direction in mid-air.
    /// Does nothing without input: the air jump then goes straight up and keeps its horizontal speed.
    /// </summary>
    /// <param name="currentVelocity">The character's velocity to modify.</param>
    private void RedirectHorizontalVelocity(ref Vector3 currentVelocity)
    {
        if (_moveInput.sqrMagnitude == 0)
        {
            return;
        }
        Vector3 verticalVelocity = Vector3.Project(currentVelocity, _kinematicMotor.CharacterUp);
        currentVelocity = verticalVelocity + _moveInput * AirJumpSideSpeed;
    }

    /// <summary>
    /// Decides whether a pending jump press can be used this update, and which kind of jump it is.
    /// A ground jump is allowed on stable ground or inside the coyote window and spends that window.
    /// Otherwise an air jump is allowed while air jumps remain, and spends one.
    /// If neither applies the request is left set, which is what lets a press made just before landing
    /// fire on touchdown (jump buffering); UpdateJumpBuffer expires it if the landing takes too long.
    /// </summary>
    /// <param name="currentVelocity">The character's velocity to modify.</param>
    private void ApplyJumping(ref Vector3 currentVelocity)
    {
        if (!_jumpRequested)
        {
            return;
        }

        bool canGroundJump = _kinematicMotor.GroundingStatus.IsStableOnGround || _coyoteTimeCounter > 0f;

        if (canGroundJump)
        {
            PerformJump(ref currentVelocity, JumpStrength);
            _coyoteTimeCounter = 0f;
            return;
        }

        if (_airJumpsRemaining > 0)
        {
            RedirectHorizontalVelocity(ref currentVelocity);
            PerformJump(ref currentVelocity, AirJumpStrength);
            _airJumpsRemaining--;
            return;
        }
        // No jump available: the request stays set so it can fire on landing, until UpdateJumpBuffer expires it
    }

    /// <summary>
    /// Called by the motor after it has probed the ground for this update, before UpdateRotation and UpdateVelocity.
    /// This is the earliest point in an update where GroundingStatus is up to date, which makes it the right place
    /// to refresh everything that depends on being grounded before the jump check reads it.
    /// Not called while climbing, because ground solving is switched off.
    /// </summary>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    public void PostGroundingUpdate(float deltaTime)
    {
        UpdateCoyoteTime(deltaTime);
    }

    /// <summary>
    /// Refreshes what touching stable ground gives back: the coyote window is refilled, the air jumps are
    /// restored, and a leap that missed its ladder is ended so the cat can turn and steer again.
    /// While airborne the coyote window drains instead; the rest only comes back on landing.
    /// </summary>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    private void UpdateCoyoteTime(float deltaTime)
    {
        if (_kinematicMotor.GroundingStatus.IsStableOnGround)
        {
            _coyoteTimeCounter = CoyoteTime;
            _airJumpsRemaining = MaxAirJumps;
            _isLeaping = false;
            _lastLadder = null;
            return;
        }

        _coyoteTimeCounter -= deltaTime;
    }

    #endregion

    #region Unused Motor Callbacks

    // ICharacterController is an interface, so every method it declares must exist here even when there is nothing to do in it
    // The methods below are left empty on purpose

    /// <summary>
    /// Asked by the motor for every collider it might hit, before it treats that collider as an obstacle.
    /// Returning false would make the character pass through that collider; everything is solid here.
    /// </summary>
    /// <param name="coll">The collider the motor is about to test against.</param>
    /// <returns>True if the character should collide with it, false to pass through.</returns>
    public bool IsColliderValidForCollisions(Collider coll)
    {
        return true;
    }

    /// <summary>
    /// Called when the motor's optional overlap check (DiscreteCollisionEvents on the motor) finds the capsule
    /// touching a collider, as opposed to hitting it while moving. Unused here.
    /// </summary>
    /// <param name="hitCollider">The collider that overlaps the character.</param>
    public void OnDiscreteCollisionDetected(Collider hitCollider)
    {
    }

    /// <summary>
    /// Called when the ground probe finds ground under the character. Unused here.
    /// </summary>
    /// <param name="hitCollider">The collider found under the character.</param>
    /// <param name="hitNormal">Direction the ground surface faces at the hit point.</param>
    /// <param name="hitPoint">World position where the ground was hit.</param>
    /// <param name="hitStabilityReport">The motor's verdict on whether the character can stand there.</param>
    public void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {
        if (hitStabilityReport.IsStable)
        {
            _animator.SetTrigger("isLanding");
        }
    }

    /// <summary>
    /// Called when the character hits something while moving, such as a wall. Unused here.
    /// </summary>
    /// <param name="hitCollider">The collider that was hit.</param>
    /// <param name="hitNormal">Direction the hit surface faces at the hit point.</param>
    /// <param name="hitPoint">World position of the hit.</param>
    /// <param name="hitStabilityReport">The motor's verdict on whether the hit surface counts as walkable ground.</param>
    public void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {
    }

    /// <summary>
    /// Gives this script a chance to overrule the motor's decision about whether a surface is stable ground,
    /// for example to make one specific slope walkable. Unused here, so the motor's own slope limit applies.
    /// </summary>
    /// <param name="hitCollider">The collider being evaluated.</param>
    /// <param name="hitNormal">Direction the surface faces at the hit point.</param>
    /// <param name="hitPoint">World position of the hit.</param>
    /// <param name="atCharacterPosition">Where the character would be when standing on this hit.</param>
    /// <param name="atCharacterRotation">How the character would be rotated when standing on this hit.</param>
    /// <param name="hitStabilityReport">The motor's verdict; change IsStable on it to override.</param>
    public void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, Vector3 atCharacterPosition, Quaternion atCharacterRotation, ref HitStabilityReport hitStabilityReport)
    {
    }

    #endregion
}