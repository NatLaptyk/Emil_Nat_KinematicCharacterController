using UnityEngine;
using KinematicCharacterController;
using UnityEngine.InputSystem;
using Unity.Cinemachine;

[RequireComponent(typeof(KinematicCharacterMotor))] 
public class KittyCharacterController : MonoBehaviour, ICharacterController
{
    public InputActionReference MoveInput;
    public InputActionReference JumpInput;
    public CinemachineCamera PlayerCamera;

    public void BeforeCharacterUpdate(float deltaTime)
    {
       
    }

    public void UpdateRotation(ref Quaternion currentRotation, float deltaTime)
    {
        
    }

    public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime)
    {
        
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
