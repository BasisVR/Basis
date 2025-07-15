
using Basis.Scripts.Networking.NetworkedAvatar;
using System;

using UnityEngine;



public class RepositionManager : MonoBehaviour
{

    private BilliardsModule table;

    private int repositionCount;
    private bool[] repositioning;

    int repositionMode = 0;
    public void onUseDown()
    {
        repositionMode++;
        if (repositionMode > 2)
            repositionMode = 0;
    }
    public void onUseUp() { }

    public void _Init(BilliardsModule table_)
    {
        table = table_;

        repositioning = new bool[table.balls.Length];

        _OnGameStarted();
    }

    public void _OnGameStarted()
    {
        repositionCount = 0;
        Array.Clear(repositioning, 0, repositioning.Length);
    }

    public void _Tick()
    {
        if (repositionCount == 0) return;

        float tableWidth = table.k_TABLE_WIDTH;
        float tableHeight = table.k_TABLE_HEIGHT;
        float k_BALL_RADIUS = table.k_BALL_RADIUS;
        Transform tableSurface = table.tableSurface;
        for (int i = 0; i < repositioning.Length; i++)
        {
            if (!repositioning[i]) continue;
            if (i > 0 && !table.isPracticeMode) continue;

            GameObject ball = table.balls[i];
            Transform pickupTransform = ball.transform.GetChild(0);

            float maxX;
            if (table.isPracticeMode)
            {
                maxX = tableWidth - k_BALL_RADIUS;
            }
            else if (i != 0)
            {
                maxX = tableWidth - k_BALL_RADIUS;
            }
            else
            {
                maxX = table.repoMaxX;
            }

            Vector3 boundedLocation = tableSurface.InverseTransformPoint(pickupTransform.position);
            if (!table.isPracticeMode || repositionMode == 0)
            {
                boundedLocation.x = Mathf.Clamp(boundedLocation.x, -tableWidth + k_BALL_RADIUS, maxX);
                boundedLocation.z = Mathf.Clamp(boundedLocation.z, -tableHeight + k_BALL_RADIUS, tableHeight - k_BALL_RADIUS);
                boundedLocation.y = 0.0f;
            }
            else
            {
                float tableEdgeX = tableWidth + table.k_RAIL_DEPTH_WIDTH;
                float tableEdgeY = tableHeight + table.k_RAIL_DEPTH_HEIGHT;
                if (repositionMode == 1)
                {
                    // can be put on rails
                    boundedLocation.x = Mathf.Clamp(boundedLocation.x, -tableEdgeX, tableEdgeX);
                    boundedLocation.z = Mathf.Clamp(boundedLocation.z, -tableEdgeY, tableEdgeY);
                    if (Mathf.Abs(boundedLocation.x) > tableWidth - k_BALL_RADIUS || Mathf.Abs(boundedLocation.z) > tableHeight - k_BALL_RADIUS)
                    {
                        boundedLocation.y = table.k_RAIL_HEIGHT_UPPER;
                    }
                    else
                    {
                        boundedLocation.y = 0f;
                    }
                }
                else
                {
                    // can be put in air
                    boundedLocation.x = Mathf.Clamp(boundedLocation.x, -tableEdgeX, tableEdgeX);
                    boundedLocation.z = Mathf.Clamp(boundedLocation.z, -tableEdgeY, tableEdgeY);
                }
            }
            //confine do D
            if (!table.isPracticeMode && table.isSnooker6Red && i == 0)
            {
                boundedLocation = ConfineToD(boundedLocation, maxX);
            }

            bool collides = PreventCollision(tableSurface, boundedLocation, ball);

            if (!collides)
            {
                // no collisions, we can update the position and reset the pickup
                table.ballsP[i] = boundedLocation;

                pickupTransform.localPosition = Vector3.zero;
                pickupTransform.localRotation = Quaternion.identity;
            }
        }
    }
    public Vector3 ConfineToD(Vector3 BallPos, float maxX)
    {
        Vector3 midD = new Vector3(maxX, 0, 0);
        Vector3 distFromDCenter3 = BallPos - midD;
        if (distFromDCenter3.magnitude > table.k_SEMICIRCLERADIUS)
        {
            return midD + distFromDCenter3.normalized * table.k_SEMICIRCLERADIUS;
        }
        return BallPos;
    }
    public bool PreventCollision(Transform tableSurface, Vector3 ballPos, GameObject ball)
    {            // ensure no collisions
        bool collides = false;

        Collider[] colliders = Physics.OverlapSphere(tableSurface.TransformPoint(ballPos), table.k_BALL_RADIUS);
        for (int j = 0; j < colliders.Length; j++)
        {
            if (colliders[j] == null) continue;

            GameObject collided = colliders[j].gameObject;
            if (collided == ball) continue;

            int collidedBall = Array.IndexOf(table.balls, collided);
            if (collidedBall != -1)
            {
                collides = true;
                break;
            }

            if (collided.name == "table" || collided.name == "glass" || collided.name == ".4BALL_FILL")
            {
                collides = true;
                break;
            }
        }
        return collides;
    }

    public void _BeginReposition(Repositioner grip)
    {
        if (!canReposition(grip))
        {
            grip._Drop();
            return;
        }

        int idx = grip.idx;
        if (repositioning[idx]) return;

        repositioning[idx] = true;
        repositionCount++;
        return;
    }

    public void _EndReposition(Repositioner grip)
    {
        int idx = grip.idx;
        if (!repositioning[idx]) return;

        repositioning[idx] = false;
        repositionCount--;

        grip._Reset();

        table._TriggerPlaceBall(idx);
    }

    private bool canReposition(Repositioner grip)
    {
        BasisNetworkPlayer self = BasisNetworkPlayer.LocalPlayer;
        if (!table.gameLive)
        {
            return false;
        }
        if (!table._IsPlayer(self))
        {
            return false;
        }
        if (grip.idx > 0)
        {
            if (!table.isPracticeMode)
            {
                return false;
            }
        }

        return true;
    }
}
