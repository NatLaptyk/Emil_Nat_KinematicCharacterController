using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;
using KinematicCharacterController;

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

    // Input System action for the jump button; reads as 0 while released and above 0 while held
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

    // How quickly the character turns to face its movement direction; higher turns faster
    public float RotationRate = 5f;

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

    // Speed along the ladder, in metres per second, at full stick
    public float ClimbSpeed = 3f;

    // Seconds taken to slide onto the ladder when grabbing it, and to slide to an exit point when leaving it
    public float AnchorDuration = 0.25f;

    // Physics layer(s) the ladder trigger colliders live on
    public LayerMask LadderLayers;

    #endregion

    #region Runtime State

    // Remaining coyote window in seconds: refilled while grounded, drains while airborne, spent by a jump
    private float _coyoteTimeCounter;

    // Direction the player wants to move this update, already rotated to match the camera; length 0 to 1
    private Vector3 _moveInput;

    // Direction the character should turn towards this update; currently the same as _moveInput
    private Vector3 _lookInput;

    // True for as long as the jump button is held down
    private bool _jumpPressed;

    // The KCC motor on this GameObject; it owns the capsule, the collisions and the ground detection
    private KinematicCharacterMotor _kinematicMotor;
    
    // Which movement rules are in charge right now
    private MovementMode _movementMode = MovementMode.Normal;

    // Which part of the climb we are in; only meaningful while _movementMode is Climbing
    private ClimbPhase _climbPhase;

    // The ladder being climbed; null while not climbing
    private KittyLadder _activeLadder;

    // Raw forward/back stick value, before any camera rotation; moves the cat up and down the ladder
    private float _climbInput;

    // True while the run button is held
    private bool _runHeld;

    // Set by Update when the climb button goes down, cleared by the motor update that handles it
    private bool _climbRequested;

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
    }

    #endregion

    #region Input

    /// <summary>
    /// First callback of every motor update, before the motor probes the ground or moves anything.
    /// This is the right moment to read the controls, so every later callback in the same update
    /// works from the same snapshot of the input.
    /// </summary>
    /// <param name="deltaTime">Duration of the current motor update in seconds (the fixed timestep, 0.02 by default).</param>
    public void BeforeCharacterUpdate(float deltaTime)
    {
        UpdateInputs();
        HandleClimbRequest();
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

        // Lay it flat on the XZ ground plane; a keyboard gives whole numbers, a stick gives fractions
        _moveInput = new Vector3(moveActionInput.x, 0, moveActionInput.y);

        // Two keyboard keys at once would give a length of 1.41, which would be a free speed boost
        _moveInput = Vector3.ClampMagnitude(_moveInput, 1);

        // Rotate the input so it is relative to where the camera looks
        _moveInput = GetCameraPlanarRotation(PlayerCamera.State.GetFinalOrientation()) * _moveInput;

        // The character turns towards the direction it moves in
        _lookInput = _moveInput;

        // ReadValue on a button is 0 or 1, so this stays true for the whole time the button is held
        _jumpPressed = (JumpAction.action.ReadValue<float>() > 0);

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

        KittyLadder ladder = FindLadderInReach();

        if (ladder == null)
        {
            return;
        }

        GrabLadder(ladder);
    }

    /// <summary>
    /// Asks the motor which colliders on the ladder layers overlap the capsule right now, triggers included,
    /// and returns the first one that belongs to a ladder.
    /// </summary>
    /// <returns>The ladder in reach, or null if there is none.</returns>
    private KittyLadder FindLadderInReach()
    {
        int overlapCount = _kinematicMotor.CharacterOverlap(_kinematicMotor.TransientPosition, _kinematicMotor.TransientRotation, _overlapResults, LadderLayers, QueryTriggerInteraction.Collide);

        for (int i = 0; i < overlapCount; i++)
        {
            if (_overlapResults[i].TryGetComponent(out KittyLadder ladder))
            {
                return ladder;
            }
        }

        return null;
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
    /// Latches button presses that must be handled exactly once. The flag stays true until a motor update consumes it,
    /// so a press is never lost between frames and never handled twice.
    /// </summary>
    private void ReadPressedInputs()
    {
        if (ClimbAction.action.WasPressedThisFrame())
        {
            _climbRequested = true;
        }
    }

    #endregion

    #region Rotation

    /// <summary>
    /// Called by the motor every update to ask which way the character should face.
    /// currentRotation is passed by reference: whatever is left in it when this method returns is the
    /// rotation the motor applies. Nothing is changed without input, so the character keeps facing its
    /// last direction when the player lets go of the stick. Slerp with a small factor moves the facing
    /// a fraction of the way towards the target every update, which gives a smooth turn instead of a snap.
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
    /// Switches to climbing. Collision and ground solving are turned off so the motor moves the cat exactly where the
    /// climbing code asks, with no walls or floor interfering, and the cat starts sliding onto the closest point of
    /// the ladder line while turning to face the ladder.
    /// </summary>
    /// <param name="ladder">The ladder to climb.</param>
    private void GrabLadder(KittyLadder ladder)
    {
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
    /// Last callback of every motor update, after the cat has been moved. Climbing's phase changes live here because
    /// they need the position the motor actually reached this update.
    /// </summary>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    public void AfterCharacterUpdate(float deltaTime)
    {
        if (_movementMode != MovementMode.Climbing)
        {
            return;
        }

        UpdateClimbing(deltaTime);
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
        /// Updates the character's velocity while climbing.
        /// </summary>
        /// <param name="currentVelocity">The character's velocity to modify.</param>
        /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
        private void ClimbingVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        if (_climbPhase == ClimbPhase.Climbing)
        {
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
    /// Air control only touches the horizontal part of the velocity: the difference between the target and
    /// the current velocity is projected onto the plane perpendicular to gravity before it is applied, so
    /// steering in the air can never cancel gravity or add lift. Gravity is then added every update, which is
    /// what makes the character fall faster and faster.
    /// </summary>
    /// <param name="currentVelocity">The character's velocity to modify.</param>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    private void AirMovement(ref Vector3 currentVelocity, float deltaTime)
    {
        if (_moveInput.sqrMagnitude > 0)
        {
            Vector3 targetVelocity = _moveInput * MaxAirSpeed;

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
    /// Launches the character upward when the jump button is held and a jump is allowed.
    /// A jump is allowed while standing on stable ground, or for a short moment after leaving it (coyote time),
    /// so a player who presses jump a few frames after walking off a ledge still gets the jump.
    /// ForceUnground tells the motor to stop snapping the capsule to the floor for a moment; without it the
    /// motor would pull the character straight back down on the next update and the jump would fail.
    /// The vertical velocity is replaced rather than added to: a coyote jump starts while already falling,
    /// and adding the jump to a downward velocity would give a visibly weaker jump.
    /// Finally the coyote counter is emptied so the held button cannot trigger a second jump on the next update.
    /// </summary>
    /// <param name="currentVelocity">The character's velocity to modify.</param>
    private void ApplyJumping(ref Vector3 currentVelocity)
    {
        if (!_jumpPressed)
        {
            return;
        }

        bool canJump = _kinematicMotor.GroundingStatus.IsStableOnGround || _coyoteTimeCounter > 0f;

        if (!canJump)
        {
            return;
        }

        // Skips ground probing for the next 0.1 s, otherwise the motor snaps the character straight back onto the floor
        _kinematicMotor.ForceUnground();

        // Replace the vertical velocity rather than add to it, so a coyote jump that starts mid-fall is as high as a grounded one
        Vector3 verticalVelocity = Vector3.Project(currentVelocity, _kinematicMotor.CharacterUp);
        currentVelocity -= verticalVelocity;
        currentVelocity += _kinematicMotor.CharacterUp * JumpStrength;

        // The window is spent: no second jump off the same ledge
        _coyoteTimeCounter = 0f;
    }

    /// <summary>
    /// Called by the motor after it has probed the ground for this update, before UpdateRotation and UpdateVelocity.
    /// This is the earliest point in an update where GroundingStatus is up to date, which makes it the right place
    /// to bring the coyote counter up to date before the jump check reads it.
    /// </summary>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    public void PostGroundingUpdate(float deltaTime)
    {
        UpdateCoyoteTime(deltaTime);
    }

    /// <summary>
    /// Keeps the coyote window full while the character stands on stable ground and drains it once it leaves.
    /// While grounded the counter is simply reset to CoyoteTime every update. The first update without stable
    /// ground stops resetting it, so it counts down from CoyoteTime towards zero, and ApplyJumping accepts a
    /// jump for as long as it is still above zero.
    /// </summary>
    /// <param name="deltaTime">Duration of the current motor update in seconds.</param>
    private void UpdateCoyoteTime(float deltaTime)
    {
        if (_kinematicMotor.GroundingStatus.IsStableOnGround)
        {
            _coyoteTimeCounter = CoyoteTime;
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