using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;
using KinematicCharacterController;
using System;

[RequireComponent(typeof(KinematicCharacterMotor))]
public class MyCharacterController : MonoBehaviour, ICharacterController
{
    public InputActionReference MoveAction;
    public InputActionReference JumpAction;
    public CinemachineCamera PlayerCamera;

    public float MaxGroundSpeed = 5f;
    public float MaxAirSpeed = 3f;

    public float GroundAcceleration = 2.5f;
    public float AirAcceleration = 2f;

    public float JumpStrength = 10f;

    public float RotationRate = 5f;

    public Vector3 Gravity = new Vector3(0, -10, 0);

    private Vector3 _moveInput;
    private Vector3 _lookInput;
    private bool _jumpPressed;

    private KinematicCharacterMotor _kinematicMotor;

    private void Start()
    {
        Initialize();
    }

    private void Initialize()
    {
        _kinematicMotor = GetComponent<KinematicCharacterMotor>();
        _kinematicMotor.CharacterController = this;
    }

    public void BeforeCharacterUpdate(float deltaTime)
    {
        UpdateInputs();
    }

    private void UpdateInputs()
    {
        Vector2 moveActionInput = MoveAction.action.ReadValue<Vector2>();

        _moveInput = new Vector3(moveActionInput.x, 0, moveActionInput.y);
        _moveInput = Vector3.ClampMagnitude(_moveInput, 1);

        _moveInput = GetCameraPlanarRotation(PlayerCamera.State.GetFinalOrientation()) * _moveInput;

        _lookInput = _moveInput;

        _jumpPressed = (JumpAction.action.ReadValue<float>() > 0);
    }

    private Quaternion GetCameraPlanarRotation(Quaternion cameraRotation)
    {
        Vector3 cameraPlanarDirection = Vector3.ProjectOnPlane(cameraRotation * Vector3.forward, _kinematicMotor.CharacterUp).normalized;

        if (cameraPlanarDirection.sqrMagnitude > 0)
        {
            cameraPlanarDirection = Vector3.ProjectOnPlane(cameraRotation * Vector3.up, _kinematicMotor.CharacterUp).normalized;
        }

        return Quaternion.LookRotation(cameraPlanarDirection, _kinematicMotor.CharacterUp);
    }

    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        if (_lookInput == Vector3.zero) return;

        Vector3 targetLookDirection = Vector3.Slerp(_kinematicMotor.CharacterForward, _lookInput, deltaTime * RotationRate);

        currentRotation = Quaternion.LookRotation(targetLookDirection, _kinematicMotor.CharacterUp);
    }

    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        if (_kinematicMotor.GroundingStatus.IsStableOnGround)
        {
            GroundMovement(ref currentVelocity, deltaTime);
        }
        else
        {
            AirMovement(ref currentVelocity, deltaTime);
        }

        ApplyJumping(ref currentVelocity, deltaTime);
    }

    private void GroundMovement(ref Vector3 currentVelocity, float deltaTime)
    {
        Vector3 targetVelocity = _moveInput * MaxGroundSpeed;
        currentVelocity = Vector3.Lerp(currentVelocity, targetVelocity, deltaTime * GroundAcceleration);
    }

    private void AirMovement(ref Vector3 currentVelocity, float deltaTime)
    {
        if (_moveInput.sqrMagnitude > 0)
        {
            Vector3 targetVelocity = _moveInput * MaxAirSpeed;

            Vector3 velocityDiff = Vector3.ProjectOnPlane(targetVelocity - currentVelocity, Gravity);

            currentVelocity += velocityDiff * deltaTime * AirAcceleration;
        }

        //Gravity
        currentVelocity += Gravity * deltaTime;
    }

    private void ApplyJumping(ref Vector3 currentVelocity, float deltaTime)
    {
        if (_jumpPressed && _kinematicMotor.GroundingStatus.IsStableOnGround)
        {
            _kinematicMotor.ForceUnground();

            Vector3 jumpForce = _kinematicMotor.CharacterUp * JumpStrength;

            currentVelocity += jumpForce;
        }
    }

    public void AfterCharacterUpdate(float deltaTime)
    {
    }

    public bool IsColliderValidForCollisions(Collider coll)
    {
        return true;
    }

    public void OnDiscreteCollisionDetected(Collider hitCollider)
    {
    }

    public void OnGroundHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {
    }

    public void OnMovementHit(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, ref HitStabilityReport hitStabilityReport)
    {
    }

    public void PostGroundingUpdate(float deltaTime)
    {
    }

    public void ProcessHitStabilityReport(Collider hitCollider, Vector3 hitNormal, Vector3 hitPoint, Vector3 atCharacterPosition, Quaternion atCharacterRotation, ref HitStabilityReport hitStabilityReport)
    {
    }
}
