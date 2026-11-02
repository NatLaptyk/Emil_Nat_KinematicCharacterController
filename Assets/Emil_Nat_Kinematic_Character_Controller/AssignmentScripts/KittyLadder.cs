using UnityEngine;

/// <summary>
/// Marks a ladder the cat can climb. Lives on a GameObject with a trigger collider on the Ladder layer.
/// The climbable part is the straight line from BottomAnchor to TopAnchor; while climbing, the cat moves along that line.
/// The two exit transforms are where the cat is moved when it climbs off either end.
/// The GameObject's forward (blue arrow) must point from the cat into the ladder: that is the way the cat faces while climbing.
/// </summary>
public class KittyLadder : MonoBehaviour
{
    #region Inspector Settings

    // Where the cat's pivot sits at the very bottom of the climbable part
    public Transform BottomAnchor;

    // Where the cat's pivot sits at the very top of the climbable part
    public Transform TopAnchor;

    // Where the cat is placed after climbing off the bottom
    public Transform BottomExit;

    // Where the cat is placed after climbing off the top
    public Transform TopExit;

    #endregion

    #region Geometry

    /// <summary>
    /// Direction from the bottom anchor to the top anchor, length 1: the axis the cat moves along while climbing.
    /// </summary>
    public Vector3 ClimbDirection
    {
        get
        {
            return (TopAnchor.position - BottomAnchor.position).normalized;
        }
    }

    /// <summary>
    /// Finds the point on the climbable line closest to a position, and reports whether that position has gone
    /// past either end. The position is projected onto the line with a dot product, which gives how far along
    /// the line it sits, measured in metres from the bottom anchor.
    /// </summary>
    /// <param name="fromPosition">The position to test, usually the cat's current position.</param>
    /// <param name="overshoot">0 while between the anchors; metres past the top (positive) or below the bottom (negative).</param>
    /// <returns>The closest point on the line, clamped to the two anchors.</returns>
    public Vector3 ClosestPointOnLadder(Vector3 fromPosition, out float overshoot)
    {
        Vector3 bottom = BottomAnchor.position;
        Vector3 top = TopAnchor.position;
        Vector3 ladderLine = top - bottom;
        float ladderLength = ladderLine.magnitude;
        Vector3 ladderDirection = ladderLine / ladderLength;

        // How far along the ladder the position is, in metres from the bottom anchor
        float distanceAlongLadder = Vector3.Dot(fromPosition - bottom, ladderDirection);

        if (distanceAlongLadder < 0f)
        {
            overshoot = distanceAlongLadder;
            return bottom;
        }

        if (distanceAlongLadder > ladderLength)
        {
            overshoot = distanceAlongLadder - ladderLength;
            return top;
        }

        overshoot = 0f;
        return bottom + ladderDirection * distanceAlongLadder;
    }

    #endregion

    #region Editor

    /// <summary>
    /// Draws the climbable line and the two exits in the Scene view, so the ladder can be set up by eye.
    /// </summary>
    private void OnDrawGizmos()
    {
        if (BottomAnchor == null || TopAnchor == null)
        {
            return;
        }

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(BottomAnchor.position, TopAnchor.position);

        if (BottomExit != null)
        {
            Gizmos.DrawWireSphere(BottomExit.position, 0.15f);
        }

        if (TopExit != null)
        {
            Gizmos.DrawWireSphere(TopExit.position, 0.15f);
        }
    }

    #endregion
}