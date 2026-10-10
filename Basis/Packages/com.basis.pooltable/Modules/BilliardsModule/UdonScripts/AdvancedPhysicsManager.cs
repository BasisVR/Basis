using System;
using Basis.Shims;
using UnityEngine;

[Cilboxable]
public class AdvancedPhysicsManager : MonoBehaviour
{
    public string PHYSICSNAME = "<color=#FFD700>Advanced V0.5M</color>";
    [SerializeField] AudioClip[] hitSounds;
    [SerializeField] AudioClip[] bounceSounds;
    [SerializeField] AudioClip[] cushionSounds;
    [Tooltip("Clamp the cue-ball collision point to center + Radius*this (Limits max applicable spin, as miss-cue isn't possible)")]
    public float CueMaxHitRadius = 0.6f;
    public bool isHandleCollison5_2 = false;
    [Tooltip("Friction between balls, altering it will adjust how much throw balls receive in collisions. (Ball dirtiness)\nRecommended range 0.5 - 1.5")]
    public float muFactor_for_5_2 = 0.7f;

    [NonSerialized] public float inV0;
    [NonSerialized] public BilliardsModule table_;

    private const float k_FIXED_TIME_STEP = 1 / 80f;
    private const float k_GRAVITY = 9.80665f;
    private const float k_MAX_DELTA = 0.1f;
    private const float k_EQUAL_SQ = 1E-05f * 1E-05f;

    private Color markerColorYes = new Color(0.0f, 1.0f, 0.0f, 1.0f);
    private Color markerColorNo = new Color(1.0f, 0.0f, 0.0f, 1.0f);
    private Material markerMaterial;

    private BilliardsModule table;
    private Transform table_Surface;
    private Transform space;
    private GameObject[] balls;
    private Transform[] ballTransforms;
    private AudioSource[] ballAudio;
    private int ballCount;

    private float[] px, py, pz, vx, vy, vz, wx, wy, wz;
    private float[] rotX, rotY, rotZ;
    private int[] inBounds, inPocketBounds, transitioning, moved;
    private float[] vertX = new float[5], vertY = new float[5], vertZ = new float[5];

    private float accumulatedTime;
    private float pocketedTime;
    private bool jumpShotFlewOver, cueBallHasCollided;
    private float railX, railY, railZ;
    private float dX, dY, dZ;
    private float tvx, tvy, tvz, twx, twy, twz;

    private float k_BALL_DSQRPE = 0.003598f;
    private float k_BALL_DIAMETRESQ = 0.0036f;
    private float k_BALL_DIAMETRE = 0.06f;
    private float k_BALL_RADIUS = 0.03f;
    private float k_BALL_RADIUS_SQRPE;
    private float k_BALL_DSQR = 0.0036f;
    private float k_BALL_MASS = 0.16f;
    private float k_BALL_RSQR = 0.0009f;
    private float k_BALL_E = 0.98f;
    private float muFactor = 1f;
    private float k_F_SLIDE = 0.2f, k_F_ROLL = 0.008f, k_F_SPIN = 0.022f, k_F_SPIN_RATE = 5.0122876f, K_BOUNCE_FACTOR = 0.5f;
    private bool isDRate = true, isHanModel = true, isDynamicRestitution, isCushionFrictionConstant, useRailLower;
    private float k_E_C = 0.85f, k_Cushion_MU = 0.2f;
    private float k_INNER_RADIUS_CORNER, k_INNER_RADIUS_CORNER2, k_INNER_RADIUS_CORNER_SQ, k_INNER_RADIUS_CORNER_SQ2;
    private float k_INNER_RADIUS_SIDE, k_INNER_RADIUS_SIDE2, k_INNER_RADIUS_SIDE_SQ, k_INNER_RADIUS_SIDE_SQ2;
    private float k_FACING_ANGLE_CORNER, k_FACING_ANGLE_SIDE;
    private float k_TABLE_WIDTH, k_TABLE_HEIGHT, k_POCKET_WIDTH_CORNER, k_POCKET_HEIGHT_CORNER, k_POCKET_RADIUS_SIDE, k_POCKET_DEPTH_SIDE, k_CUSHION_RADIUS;
    private float k_RAIL_HEIGHT_UPPER, k_RAIL_HEIGHT_LOWER_CACHED, k_RAIL_HEIGHT_LOWER, k_RAIL_DEPTH_WIDTH, k_RAIL_DEPTH_HEIGHT, k_POCKET_RESTITUTION;
    private bool furthest_vE, furthest_vF, closest_vE, closest_vF;
    private float r_k_CUSHION_RADIUS, vertRadiusSQRPE, vertRadius, k_MINOR_REGION_CONST;
    private float tableEdgeX, tableEdgeY, tableBoundsX, tableBoundsY, caromEdgeX, caromEdgeZ;

    private float vEx, vEy, vEz, vE2x, vE2y, vE2z, vFx, vFy, vFz, vF2x, vF2y, vF2z;
    private float vAx, vAy, vAz, vBx, vBy, vBz, vCx, vCy, vCz, vDx, vDy, vDz;
    private float pKx, pKy, pKz, pLx, pLy, pLz, pNx, pNy, pNz, pPx, pPy, pPz, pQx, pQy, pQz, pRx, pRy, pRz;
    private float ADx, ADy, ADz, ADNx, ADNy, ADNz, BYx, BYy, BYz, BYNx, BYNy, BYNz, CZx, CZy, CZz, CZNx, CZNy, CZNz;

    private float cueLlposX, cueLlposY, cueLlposZ, cueDirX, cueDirY, cueDirZ, cueHitX, cueHitY, cueHitZ, cue_fdir;
    private float cueJX, cueJY, cueJZ, cueQX, cueQY, cueQZ, cueA, cueB, cueC, cueTheta, cueCos, cueSin;
    private float qrx, qry, qrz;
    private float[] ray = new float[BasisSphereCastShim.RayLength];
    private float tenToMinus3;

    public void _Init()
    {
        table = table_;
        table_Surface = table.tableSurface;
        space = transform;

        _InitConstants();

        balls = table.balls;
        ballCount = balls.Length;
        px = new float[ballCount]; py = new float[ballCount]; pz = new float[ballCount];
        vx = new float[ballCount]; vy = new float[ballCount]; vz = new float[ballCount];
        wx = new float[ballCount]; wy = new float[ballCount]; wz = new float[ballCount];
        rotX = new float[ballCount]; rotY = new float[ballCount]; rotZ = new float[ballCount];
        inBounds = new int[ballCount];
        inPocketBounds = new int[ballCount];
        transitioning = new int[ballCount];
        moved = new int[ballCount];
        ballTransforms = new Transform[ballCount];
        ballAudio = new AudioSource[ballCount];
        for (int i = 0; i < ballCount; i++)
        {
            inBounds[i] = 1;
            ballTransforms[i] = balls[i].transform;
            ballAudio[i] = balls[i].GetComponent<AudioSource>();
        }
    }

    public void _FixedTick()
    {
        if (table.gameLive)
        {
            tickCue();
        }

        if (!table.isLocalSimulationRunning) return;

        float newAccumulatedTime = accumulatedTime + Time.fixedDeltaTime;
        if (newAccumulatedTime < 0f) newAccumulatedTime = 0f;
        else if (newAccumulatedTime > k_MAX_DELTA) newAccumulatedTime = k_MAX_DELTA;
        if (newAccumulatedTime >= k_FIXED_TIME_STEP)
        {
            loadAll();
            while (newAccumulatedTime >= k_FIXED_TIME_STEP)
            {
                table._BeginPerf(table.PERF_PHYSICS_MAIN);
                tickOnce();
                table._EndPerf(table.PERF_PHYSICS_MAIN);
                newAccumulatedTime -= k_FIXED_TIME_STEP;
            }
            storeAll();
            BasisTransformBatchShim.RotateWorld(ballTransforms, space, rotX, rotY, rotZ);
            for (int i = 0; i < ballCount; i++)
            {
                rotX[i] = 0f;
                rotY[i] = 0f;
                rotZ[i] = 0f;
            }
        }

        accumulatedTime = newAccumulatedTime;
    }

    private void loadAll()
    {
        BasisVectorArrayShim.Split(table.ballsP, px, py, pz);
        BasisVectorArrayShim.Split(table.ballsV, vx, vy, vz);
        BasisVectorArrayShim.Split(table.ballsW, wx, wy, wz);
    }

    private void storeAll()
    {
        BasisVectorArrayShim.Join(px, py, pz, table.ballsP);
        BasisVectorArrayShim.Join(vx, vy, vz, table.ballsV);
        BasisVectorArrayShim.Join(wx, wy, wz, table.ballsW);
    }

    private void triggerPocketBall(int id, bool outOfBounds)
    {
        storeAll();
        table._TriggerPocketBall(id, outOfBounds);
        loadAll();
    }

    private void triggerSimulationEnded()
    {
        storeAll();
        table._TriggerSimulationEnded(false);
        loadAll();
    }

    private void play(int id, AudioClip clip, float volume)
    {
        if (clip == null) return;
        AudioSource source = ballAudio[id];
        if (source != null) source.PlayOneShot(clip, volume);
    }

    private void tickOnce()
    {
        bool ballsMoving = false;

        uint sn_pocketed = table.ballsPocketedLocal;

        table._BeginPerf(table.PERF_PHYSICS_BALL);
        for (int i = 0; i < ballCount; i++) moved[i] = 0;

        uint ball_bit = 0x1u;
        bool is4Ball = table.is4Ball;
        for (int i = 0; i < ballCount; i++)
        {
            float moveTimeLeft = k_FIXED_TIME_STEP;
            int collidedBall = -1;
            int predictedHitBall = -1;
            int checkBall = -1;
            int numSteps = 0;
            while (moveTimeLeft > 0f)
            {
                numSteps++;
                if ((ball_bit & sn_pocketed) == 0U)
                {
                    float deltaTime = moveTimeLeft;
                    float startX = px[i], startY = py[i], startZ = pz[i];

                    float mx = vx[i] * deltaTime, my = vy[i] * deltaTime, mz = vz[i] * deltaTime;
                    float msq = mx * mx + my * my + mz * mz;
                    if (msq != 0)
                    {
                        calculateDeltaPosition(sn_pocketed, i, deltaTime, ref predictedHitBall, collidedBall > -2, inPocketBounds[i] != 0);
                        px[i] = px[i] + dX;
                        py[i] = py[i] + dY;
                        pz[i] = pz[i] + dZ;
                    }

                    bool doColCheck = false;
                    if (predictedHitBall > -1 && predictedHitBall != collidedBall)
                    {
                        checkBall = predictedHitBall;
                        doColCheck = true;
                    }
                    collidedBall = predictedHitBall;

                    float cvx = vx[i], cvy = vy[i], cvz = vz[i];
                    float cwx = wx[i], cwy = wy[i], cwz = wz[i];
                    if (!(cvx * cvx + cvy * cvy + cvz * cvz < k_EQUAL_SQ) || !(cwx * cwx + cwy * cwy + cwz * cwz < k_EQUAL_SQ))
                    {
                        bool hitCushion;
                        if (is4Ball)
                        {
                            hitCushion = _phy_ball_table_carom(i);
                        }
                        else
                        {
                            hitCushion = _phy_ball_table_std(i);
                        }

                        if (predictedHitBall != -1 || hitCushion)
                        {
                            float ax = px[i] - startX, ay = py[i] - startY, az = pz[i] - startZ;
                            float actualMoveDistance = Mathf.Sqrt(ax * ax + ay * ay + az * az);
                            if (msq == 0)
                                moveTimeLeft = 0;
                            else
                                moveTimeLeft *= 1 - (actualMoveDistance / Mathf.Sqrt(msq));
                        }
                        else moveTimeLeft = 0;

                        if (_phy_ball_pockets(i, is4Ball))
                        {
                            moveTimeLeft = 0;
                            moved[i] = 0;
                        }
                        else
                        {
                            moved[i] = updateVelocity(i, deltaTime - moveTimeLeft, hitCushion, inPocketBounds[i] != 0) ? 1 : 0;

                            if (doColCheck) { stepOneBall(i, sn_pocketed, checkBall); }

                            if (inBounds[i] == 0 && moved[i] == 0 && !table.isPracticeMode)
                            {
                                table._TriggerBallFallOffFoul();
                                triggerPocketBall(i, true);
                            }
                        }
                    }
                    else
                    {
                        moveTimeLeft = 0;
                        moved[i] = 0;
                    }

                    if (moved[i] != 0) ballsMoving = true;
                }
                else
                {
                    moveTimeLeft = 0;
                }
                if (numSteps > 2) break;
            }
            ball_bit <<= 1;
        }
        table._EndPerf(table.PERF_PHYSICS_BALL);

        if (!ballsMoving)
        {
            if (Time.time - pocketedTime > 1f)
            {
                triggerSimulationEnded();
                return;
            }
        }

        if (is4Ball) return;

        if (table.isSnooker6Red)
        {
            if (!cueBallHasCollided && py[0] > 0)
            {
                ball_bit = 0x1U;
                float cueX = px[0], cueZ = pz[0];
                bool flewOverThisFrame = false;
                for (int i = 1; i < ballCount; i++)
                {
                    ball_bit <<= 1;
                    if ((ball_bit & sn_pocketed) > 0U) continue;
                    float ddx = cueX - px[i];
                    float ddz = cueZ - pz[i];
                    if (Mathf.Sqrt(ddx * ddx + ddz * ddz) < k_BALL_DIAMETRE)
                    {
                        jumpShotFlewOver = true;
                        flewOverThisFrame = true;
                    }
                }
                if (jumpShotFlewOver && !flewOverThisFrame)
                {
                    table._TriggerJumpShotFoul();
                    jumpShotFlewOver = false;
                }
            }
        }
    }

    private void calculateDeltaPosition(uint sn_pocketed, int id, float timeStep, ref int predictedHitBall, bool doTable, bool inPocket)
    {
        float posX = px[id], posY = py[id], posZ = pz[id];
        float odX = vx[id] * timeStep, odY = vy[id] * timeStep, odZ = vz[id] * timeStep;
        float odSq = odX * odX + odY * odY + odZ * odZ;
        if (odSq < k_EQUAL_SQ)
        {
            dX = odX; dY = odY; dZ = odZ;
            return;
        }
        float reachSq = odSq * 1.002f;
        float[] r = ray;
        r[BasisSphereCastShim.RayOriginX] = posX;
        r[BasisSphereCastShim.RayOriginY] = posY;
        r[BasisSphereCastShim.RayOriginZ] = posZ;
        r[BasisSphereCastShim.RayDirectionX] = vx[id];
        r[BasisSphereCastShim.RayDirectionY] = vy[id];
        r[BasisSphereCastShim.RayDirectionZ] = vz[id];
        r[BasisSphereCastShim.RayDistance] = float.MaxValue;

        uint skip = sn_pocketed | (1u << id);
        if (predictedHitBall >= 0) skip |= 1u << predictedHitBall;
        bool overlapping;
        int ballHit = BasisSphereCastShim.NormalizeAndSweepSpheres(r, px, py, pz, ballCount, skip, k_BALL_DSQRPE, k_BALL_DIAMETRESQ, out overlapping);
        float nX = r[BasisSphereCastShim.RayDirectionX], nY = r[BasisSphereCastShim.RayDirectionY], nZ = r[BasisSphereCastShim.RayDirectionZ];
        float minnmag = r[BasisSphereCastShim.RayDistance];
        int hitid = -1;
        if (overlapping)
        {
            predictedHitBall = ballHit;
            dX = 0f; dY = 0f; dZ = 0f;
            return;
        }
        if (ballHit >= 0) hitid = ballHit;

        bool hitTable = false;
        if (doTable)
        {
            float sx = posX >= 0F ? 1F : -1F;
            float sz = posZ >= 0F ? 1F : -1F;
            float nvX = nX * sx, nvY = nY * 1f, nvZ = nZ * sz;
            if (inPocket)
            {
                float aX = posX * sx, aY = posY * 1f, aZ = posZ * sz;

                float e1x = aX - vEx, e1y = aY - vEy, e1z = aZ - vEz;
                float f1x = aX - vFx, f1y = aY - vFy, f1z = aZ - vFz;
                float ppX, ppY, ppZ, pocketRad;
                if (e1x * e1x + e1y * e1y + e1z * e1z < f1x * f1x + f1y * f1y + f1z * f1z)
                {
                    if (closest_vE)
                    {
                        ppX = vE2x; ppY = vE2y; ppZ = vE2z;
                        pocketRad = k_INNER_RADIUS_CORNER2;
                    }
                    else
                    {
                        ppX = vEx; ppY = vEy; ppZ = vEz;
                        pocketRad = k_INNER_RADIUS_CORNER;
                    }
                }
                else
                {
                    if (closest_vF)
                    {
                        ppX = vF2x; ppY = vF2y; ppZ = vF2z;
                        pocketRad = k_INNER_RADIUS_SIDE2;
                    }
                    else
                    {
                        ppX = vFx; ppY = vFy; ppZ = vFz;
                        pocketRad = k_INNER_RADIUS_SIDE;
                    }
                }

                float edX = aX - ppX, edY = 0f, edZ = aZ - ppZ;
                float edMag = Mathf.Sqrt(edX * edX + edY * edY + edZ * edZ);
                if (edMag > 1E-05f)
                {
                    edX = edX / edMag; edY = edY / edMag; edZ = edZ / edMag;
                }
                else
                {
                    edX = 0f; edY = 0f; edZ = 0f;
                }
                float peX = ppX + edX * pocketRad, peZ = ppZ + edZ * pocketRad;
                float peY = -k_BALL_RADIUS;
                if (aX * edX + aY * edY + aZ * edZ < 0)
                {
                    if (BasisSphereCastShim.RaySphere(aX, aY, aZ, nvX, nvY, nvZ, peX, peY, peZ, k_BALL_RADIUS_SQRPE, ref minnmag))
                    {
                        hitTable = true;
                        hitid = -100;
                    }
                }
            }
            else
            {
                if (odY < 0 && inBounds[id] != 0)
                {
                    float s = -(k_BALL_RADIUS + 0.001f);
                    if (BasisSphereCastShim.SpherePlane(posX, posY, posZ, nX, nY, nZ, 0f * s, 1f * s, 0f * s, 0f, 1f, 0f, k_BALL_RADIUS, ref minnmag))
                    {
                        hitTable = true;
                        hitid = -1;
                    }
                }
                if (odX > 0)
                {
                    float gap = (pRx + 0.001f) - k_BALL_RADIUS - posX - 1E-06f;
                    if (posX + k_BALL_RADIUS <= pRx && !(gap > 0f && gap * gap > reachSq))
                    {
                        if (BasisSphereCastShim.SpherePlane(posX, posY, posZ, nX, nY, nZ, pRx + 0.001f, pRy, pRz, -1f, -0f, -0f, k_BALL_RADIUS, ref minnmag))
                        {
                            hitTable = true;
                            hitid = -1;
                        }
                    }
                }
                else
                {
                    float gap = posX + (pRx + 0.001f) - k_BALL_RADIUS - 1E-06f;
                    if (posX - k_BALL_RADIUS >= -pRx && !(gap > 0f && gap * gap > reachSq))
                    {
                        if (BasisSphereCastShim.SpherePlane(posX, posY, posZ, nX, nY, nZ, -pRx - 0.001f, -pRy, -pRz, 1f, 0f, 0f, k_BALL_RADIUS, ref minnmag))
                        {
                            hitTable = true;
                            hitid = -1;
                        }
                    }
                }
                if (odZ > 0)
                {
                    float gap = (pNz + 0.001f) - k_BALL_RADIUS - posZ - 1E-06f;
                    if (posZ + k_BALL_RADIUS <= pNz && !(gap > 0f && gap * gap > reachSq))
                    {
                        if (BasisSphereCastShim.SpherePlane(posX, posY, posZ, nX, nY, nZ, pNx, pNy, pNz + 0.001f, -0f, -0f, -1f, k_BALL_RADIUS, ref minnmag))
                        {
                            hitTable = true;
                            hitid = -1;
                        }
                    }
                }
                else
                {
                    float gap = posZ + (pNz + 0.001f) - k_BALL_RADIUS - 1E-06f;
                    if (posZ - k_BALL_RADIUS >= -pNz && !(gap > 0f && gap * gap > reachSq))
                    {
                        if (BasisSphereCastShim.SpherePlane(posX, posY, posZ, nX, nY, nZ, -pNx, -pNy, -pNz - 0.001f, 0f, 0f, 1f, k_BALL_RADIUS, ref minnmag))
                        {
                            hitTable = true;
                            hitid = -1;
                        }
                    }
                }
            }
            if (posY < k_RAIL_HEIGHT_UPPER && vertexInReach(posX * sx, posZ * sz, odX, odY, odZ))
            {
                int vertex = BasisSphereCastShim.RaySpheres(posX * sx, 0f * 1f, posZ * sz, nvX, nvY, nvZ, vertX, vertY, vertZ, 5, vertRadiusSQRPE, ref minnmag);
                if (vertex >= 0)
                {
                    hitTable = true;
                    hitid = -2 - vertex;
                }
            }
        }

        if (hitid > -1 || hitTable)
        {
            if (minnmag * minnmag < odX * odX + odY * odY + odZ * odZ)
            {
                predictedHitBall = hitid;
                dX = nX * minnmag; dY = nY * minnmag; dZ = nZ * minnmag;
                return;
            }
        }

        dX = odX; dY = odY; dZ = odZ;
    }

    private bool vertexInReach(float ox, float oz, float odX, float odY, float odZ)
    {
        float travel = (odX < 0f ? -odX : odX) + (odY < 0f ? -odY : odY) + (odZ < 0f ? -odZ : odZ);
        float reach = vertRadius + travel * 1.002f + 1E-06f;
        float reachSq = reach * reach;
        for (int k = 0; k < 5; k++)
        {
            float ex = ox - vertX[k], ey = vertY[k], ez = oz - vertZ[k];
            if (ex * ex + ey * ey + ez * ez <= reachSq) return true;
        }
        return false;
    }

    private void stepOneBall(int id, uint sn_pocketed, int checkBall)
    {
        uint ball_bit = 1u << checkBall;
        if ((ball_bit & sn_pocketed) != 0U) return;

        float deltaX = px[checkBall] - px[id], deltaY = py[checkBall] - py[id], deltaZ = pz[checkBall] - pz[id];
        float dist = deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
        if (dist < k_BALL_DIAMETRESQ)
        {
            dist = Mathf.Sqrt(dist);
            float nX = deltaX / dist, nY = deltaY / dist, nZ = deltaZ / dist;

            float push = k_BALL_DIAMETRE - dist;
            float resX = nX * push, resY = nY * push, resZ = nZ * push;
            px[checkBall] = px[checkBall] + resX; py[checkBall] = py[checkBall] + resY; pz[checkBall] = pz[checkBall] + resZ;
            px[id] = px[id] - resX; py[id] = py[id] - resY; pz[id] = pz[id] - resZ;
            moved[checkBall] = 1;
            moved[id] = 1;

            float cuePrevX = vx[0], cuePrevZ = vz[0];

            float vdX = vx[id] - vx[checkBall], vdY = vy[id] - vy[checkBall], vdZ = vz[id] - vz[checkBall];

            if (vdX * nX + vdY * nY + vdZ * nZ < 0) return;

            if (isHandleCollison5_2)
            {
                handleCollision5_2(checkBall, id, nX, nY, nZ);
            }
            else
            {
                handleCollision6(checkBall, id, nX, nY, nZ);
            }

            float dot = vdX * nX + vdY * nY + vdZ * nZ;
            int hitCount = hitSounds != null ? hitSounds.Length : 0;
            if (hitCount > 0)
            {
                float volume = dot < 0F ? 0F : (dot > 1F ? 1F : dot);
                play(id, hitSounds[id % (hitCount < 3 ? hitCount : 3)], volume);
            }

            if (table.isSnooker6Red)
            {
                if (!cueBallHasCollided && id == 0 && py[0] > 0)
                {
                    float biX = vx[id], biY = 0f, biZ = vz[id];
                    float bcX = vx[checkBall], bcY = 0f, bcZ = vz[checkBall];
                    float magId = Mathf.Sqrt(biX * biX + biY * biY + biZ * biZ);
                    float magCheck = Mathf.Sqrt(bcX * bcX + bcY * bcY + bcZ * bcZ);
                    float scale = magId / magCheck;
                    biX = biX * scale; biY = biY * scale; biZ = biZ * scale;
                    if (magCheck > 1E-05f)
                    {
                        bcX = bcX / magCheck; bcY = bcY / magCheck; bcZ = bcZ / magCheck;
                    }
                    else
                    {
                        bcX = 0f; bcY = 0f; bcZ = 0f;
                    }
                    float velDot = biX * bcX + biY * bcY + biZ * bcZ;

                    bool dotBehind = cuePrevX * deltaX + 0f * deltaY + cuePrevZ * deltaZ < 0;

                    if (velDot > 1 || dotBehind)
                    {
                        table._TriggerJumpShotFoul();
                    }
                    cueBallHasCollided = true;
                }
            }
            storeAll();
            table._TriggerCollision(id, checkBall);
        }
    }

    private void handleCollision5_2(int i, int id, float nX, float nY, float nZ)
    {
        float e = k_BALL_E;
        float R = k_BALL_RADIUS;
        float M = k_BALL_MASS;
        float I = (2f * M * (R * R));

        float laIdX = -nX * R, laIdY = -nY * R, laIdZ = -nZ * R;
        float laIX = nX * R, laIY = nY * R, laIZ = nZ * R;

        float wIdX = wx[id], wIdY = wy[id], wIdZ = wz[id];
        float wIX = wx[i], wIY = wy[i], wIZ = wz[i];
        float cIdX = wIdY * laIdZ - wIdZ * laIdY, cIdY = wIdZ * laIdX - wIdX * laIdZ, cIdZ = wIdX * laIdY - wIdY * laIdX;
        float cIX = wIY * laIZ - wIZ * laIY, cIY = wIZ * laIX - wIX * laIZ, cIZ = wIX * laIY - wIY * laIX;
        float rvX = (vx[id] + cIdX) - (vx[i] + cIX);
        float rvY = (vy[id] + cIdY) - (vy[i] + cIY);
        float rvZ = (vz[id] + cIdZ) - (vz[i] + cIZ);

        float J = ((1f + e) / 2f) * (rvX * nX + rvY * nY + rvZ * nZ);
        float fnX = nX * J, fnY = nY * J, fnZ = nZ * J;

        vx[id] = vx[id] - fnX; vy[id] = vy[id] - fnY; vz[id] = vz[id] - fnZ;
        vx[i] = vx[i] + fnX; vy[i] = vy[i] + fnY; vz[i] = vz[i] + fnZ;

        float mu = muFactor_for_5_2 * (9.951e-3f + 0.108f * Mathf.Exp(-1.088f * (Mathf.Sqrt(fnX * fnX + fnY * fnY + fnZ * fnZ))));
        float mu_s = 0.108f;

        float rn = rvX * nX + rvY * nY + rvZ * nZ;
        float tfX = rvX - nX * rn, tfY = rvY - nY * rn, tfZ = rvZ - nZ * rn;

        if (tfX == 0f && tfY == 0f && tfZ == 0f)
        {
            return;
        }
        float tfMag = Mathf.Sqrt(tfX * tfX + tfY * tfY + tfZ * tfZ);
        if (tfMag > 1E-05f)
        {
            tfX = tfX / tfMag; tfY = tfY / tfMag; tfZ = tfZ / tfMag;
        }
        else
        {
            tfX = 0f; tfY = 0f; tfZ = 0f;
        }

        float JT = -(rvX * tfX + rvY * tfY + rvZ * tfZ) / 2f;

        float ftX, ftY, ftZ;
        float absJT = JT < 0f ? -JT : JT + 0f;
        if (absJT <= J * -mu_s)
        {
            ftX = tfX * JT; ftY = tfY * JT; ftZ = tfZ * JT;
        }
        else
        {
            float negJ = -J;
            float negMu = -mu;
            ftX = tfX * negJ * negMu; ftY = tfY * negJ * negMu; ftZ = tfZ * negJ * negMu;
        }

        vx[id] = vx[id] - ftX; vy[id] = vy[id] - ftY; vz[id] = vz[id] - ftZ;
        vx[i] = vx[i] + ftX; vy[i] = vy[i] + ftY; vz[i] = vz[i] + ftZ;

        float tIdX = laIdY * ftZ - laIdZ * ftY, tIdY = laIdZ * ftX - laIdX * ftZ, tIdZ = laIdX * ftY - laIdY * ftX;
        float tIX = laIY * ftZ - laIZ * ftY, tIY = laIZ * ftX - laIX * ftZ, tIZ = laIX * ftY - laIY * ftX;
        float invI = 1f / I;

        wx[id] = wx[id] - tIdX * invI; wy[id] = wy[id] - tIdY * invI; wz[id] = wz[id] - tIdZ * invI;
        wx[i] = wx[i] + tIX * invI; wy[i] = wy[i] + tIY * invI; wz[i] = wz[i] + tIZ * invI;
    }

    private void handleCollision6(int i, int id, float nX, float nY, float nZ)
    {
        float e = k_BALL_E;
        float R = k_BALL_RADIUS;
        float M = k_BALL_MASS;
        float I = ((2f / 5f) * M * (R * R));

        float wIdX = wx[id], wIdY = wy[id], wIdZ = wz[id];
        float wIX = wx[i], wIY = wy[i], wIZ = wz[i];

        float vrX = vx[id] - vx[i], vrY = vy[id] - vy[i], vrZ = vz[id] - vz[i];

        float aX = -nX * R, aY = -nY * R, aZ = -nZ * R;
        float bX = nX * R, bY = nY * R, bZ = nZ * R;
        float c1X = wIdY * aZ - wIdZ * aY, c1Y = wIdZ * aX - wIdX * aZ, c1Z = wIdX * aY - wIdY * aX;
        float c2X = wIY * bZ - wIZ * bY, c2Y = wIZ * bX - wIX * bZ, c2Z = wIX * bY - wIY * bX;
        float vcX = (vrX + c1X) - c2X, vcY = (vrY + c1Y) - c2Y, vcZ = (vrZ + c1Z) - c2Z;

        float mu = 9.951f * (tenToMinus3) + 0.108f * Mathf.Exp(-1.088f * Mathf.Sqrt(vcX * vcX + vcY * vcY + vcZ * vcZ));
        mu = muFactor * mu;

        float v_rel_normal = vcX * nX + vcY * nY + vcZ * nZ;

        float vtX = vcX - nX * v_rel_normal, vtY = vcY - nY * v_rel_normal, vtZ = vcZ - nZ * v_rel_normal;

        float q1X = nY * wIdZ - nZ * wIdY, q1Y = nZ * wIdX - nX * wIdZ, q1Z = nX * wIdY - nY * wIdX;
        float r1X = q1Y * nZ - q1Z * nY, r1Y = q1Z * nX - q1X * nZ, r1Z = q1X * nY - q1Y * nX;
        float q2X = nY * wIZ - nZ * wIY, q2Y = nZ * wIX - nX * wIZ, q2Z = nX * wIY - nY * wIX;
        float r2X = q2Y * nZ - q2Z * nY, r2Y = q2Z * nX - q2X * nZ, r2Z = q2X * nY - q2Y * nX;
        float dot1 = nX * r1X + nY * r1Y + nZ * r1Z;
        float dot2 = nX * r2X + nY * r2Y + nZ * r2Z;

        float J_normal = -(1 + e) * v_rel_normal / (1 / M + 1 / M + (R * R * dot1 / I) + (R * R * dot2 / I));

        float absJn = J_normal < 0f ? -J_normal : J_normal + 0f;
        float J_tangential = -mu * absJn;

        vx[id] = vx[id] + (nX * J_normal) / M; vy[id] = vy[id] + (nY * J_normal) / M; vz[id] = vz[id] + (nZ * J_normal) / M;
        vx[i] = vx[i] - (nX * J_normal) / M; vy[i] = vy[i] - (nY * J_normal) / M; vz[i] = vz[i] - (nZ * J_normal) / M;

        float vtMag = Mathf.Sqrt(vtX * vtX + vtY * vtY + vtZ * vtZ);
        float tnX, tnY, tnZ;
        if (vtMag > 1E-05f)
        {
            tnX = vtX / vtMag; tnY = vtY / vtMag; tnZ = vtZ / vtMag;
        }
        else
        {
            tnX = 0f; tnY = 0f; tnZ = 0f;
        }
        float jtX = tnX * J_tangential, jtY = tnY * J_tangential, jtZ = tnZ * J_tangential;

        vx[id] = vx[id] + jtX / M; vy[id] = vy[id] + jtY / M; vz[id] = vz[id] + jtZ / M;
        vx[i] = vx[i] - jtX / M; vy[i] = vy[i] - jtY / M; vz[i] = vz[i] - jtZ / M;

        float cwX = nY * jtZ - nZ * jtY, cwY = nZ * jtX - nX * jtZ, cwZ = nX * jtY - nY * jtX;
        wx[id] = wx[id] + (cwX * R) / I; wy[id] = wy[id] + (cwY * R) / I; wz[id] = wz[id] + (cwZ * R) / I;
        wx[i] = wx[i] - (cwX * R) / I; wy[i] = wy[i] - (cwY * R) / I; wz[i] = wz[i] - (cwZ * R) / I;
    }

    private bool updateVelocity(int id, float timeStep, bool hitWall, bool inPocket)
    {
        float t = timeStep;
        bool ballMoving = false;
        float frameGravity = k_GRAVITY * t;

        float g = k_GRAVITY;
        float R = k_BALL_RADIUS;
        float Rate = k_F_SPIN_RATE;
        float mu_spf = k_F_SPIN;
        float DARate;
        float mu_sp;
        float mu_s = k_F_SLIDE;
        float mu_r = k_F_ROLL;

        float Vx = vx[id], Vy = vy[id], Vz = vz[id];
        float Wx = wx[id], Wy = wy[id], Wz = wz[id];

        float vxzSq = Vx * Vx + 0f * 0f + Vz * Vz;
        float absWy = Wy < 0f ? -Wy : Wy + 0f;

        if (isDRate)
        {
            if (vxzSq < 0.0001f && absWy > 50f)
            {
                Rate = 300f;
            }
            else
            {
                Rate = k_F_SPIN_RATE;
            }

            DARate = (2f * Rate * R) / (5f * g);
            mu_sp = DARate;
        }
        else
        {
            if (vxzSq < 0.0001f && absWy > 50f)
            {
                mu_spf = 0.3f;
            }
            else
            {
                mu_spf = k_F_SPIN;
            }
            mu_sp = mu_spf;
        }

        float floor = inBounds[id] != 0 || transitioning[id] != 0 ? 0 : k_RAIL_HEIGHT_UPPER;

        if (py[id] < floor + 0.001 && Vy <= 0 && !inPocket)
        {
            float negR = -R;
            float cX = negR * Wz - 0f * Wy, cY = 0f * Wx - 0f * Wz, cZ = 0f * Wy - negR * Wx;
            float u0x = Vx + cX, u0y = 0f + cY, u0z = Vz + cZ;

            float absolute_u0 = Mathf.Sqrt(u0x * u0x + u0y * u0y + u0z * u0z);

            if (absolute_u0 <= 0.1f)
            {
                float xzMag = Mathf.Sqrt(vxzSq);
                float nvX, nvY, nvZ;
                if (xzMag > 1E-05f)
                {
                    nvX = Vx / xzMag; nvY = 0f / xzMag; nvZ = Vz / xzMag;
                }
                else
                {
                    nvX = 0f; nvY = 0f; nvZ = 0f;
                }
                float roll = -mu_r * g * t;
                Vx = Vx + nvX * roll; Vy = Vy + nvY * roll; Vz = Vz + nvZ * roll;

                Wx = -Vz * 1f / R;

                if (0.3f > (Wy < 0f ? -Wy : Wy + 0f))
                {
                    Wy = 0.0f;
                }
                else
                {
                    float w_perp = (5f * mu_sp * g) / (2f * R);
                    Wy -= (Wy >= 0F ? 1F : -1F) * w_perp * t;
                }

                Wz = Vx * 1f / R;

                if (vxzSq < 0.0001f && Mathf.Sqrt(Wx * Wx + Wy * Wy + Wz * Wz) < 0.04f)
                {
                    Wx = 0f; Wy = 0f; Wz = 0f;
                    Vx = 0f; Vy = 0f; Vz = 0f;
                }
                else
                {
                    ballMoving = true;
                }
            }
            else
            {
                float nvX = u0x / absolute_u0, nvY = u0y / absolute_u0, nvZ = u0z / absolute_u0;

                float slide = -mu_s * g * t;
                Vx = Vx + nvX * slide; Vy = Vy + nvY * slide; Vz = Vz + nvZ * slide;

                float spin = (-5.0f * mu_s * g) / (2.0f * R) * t;
                float upX = 1f * nvZ - 0f * nvY, upY = 0f * nvX - 0f * nvZ, upZ = 0f * nvY - 1f * nvX;
                Wx = Wx + upX * spin; Wy = Wy + upY * spin; Wz = Wz + upZ * spin;

                ballMoving = true;
            }
        }
        else
        {
            ballMoving = true;
        }

        float cpx = px[id], cpz = pz[id];
        float absX = cpx < 0f ? -cpx : cpx + 0f;
        float absZ = cpz < 0f ? -cpz : cpz + 0f;
        if (absX < k_TABLE_WIDTH + k_RAIL_DEPTH_WIDTH && absZ < k_TABLE_HEIGHT + k_RAIL_DEPTH_HEIGHT)
        {
            if (py[id] < floor && !inPocket)
            {
                Vy = -Vy * K_BOUNCE_FACTOR;
                if (Vy < frameGravity)
                {
                    Vy = 0f;
                    py[id] = floor;
                }
                else
                {
                    py[id] = (-(py[id] - floor) * K_BOUNCE_FACTOR) + floor;
                    if (Vy > 0.2 && !hitWall && bounceSounds != null && bounceSounds.Length > 0)
                    {
                        float volume = Vy < 0F ? 0F : (Vy > 1F ? 1F : Vy);
                        play(id, bounceSounds[UnityEngine.Random.Range(0, bounceSounds.Length)], volume);
                    }
                }
                if (transitioning[id] != 0)
                {
                    transitioning[id] = 0;
                    inBounds[id] = 1;
                }
            }
        }
        else
        {
            railX = px[id]; railY = py[id]; railZ = pz[id];
            if (absX > k_TABLE_WIDTH + k_RAIL_DEPTH_WIDTH)
            {
                railX = k_TABLE_WIDTH + k_RAIL_DEPTH_WIDTH;
                railX *= cpx >= 0F ? 1F : -1F;
            }
            if (absZ > k_TABLE_HEIGHT + k_RAIL_DEPTH_HEIGHT)
            {
                railZ = k_TABLE_HEIGHT + k_RAIL_DEPTH_HEIGHT;
                railZ *= cpz >= 0F ? 1F : -1F;
            }
            railY = k_RAIL_HEIGHT_UPPER - k_BALL_RADIUS;
            transitionCollision(id, ref Vx, ref Vy, ref Vz);
        }
        if (py[id] > 0 || inPocket)
            Vy -= frameGravity;

        float Max = 250f;
        float negMax = -Max;
        if (Wx < negMax) Wx = negMax; else if (Wx > Max) Wx = Max;
        if (Wy < negMax) Wy = negMax; else if (Wy > Max) Wy = Max;
        if (Wz < negMax) Wz = negMax; else if (Wz > Max) Wz = Max;

        wx[id] = Wx; wy[id] = Wy; wz[id] = Wz;
        vx[id] = Vx; vy[id] = Vy; vz[id] = Vz;

        rotX[id] = rotX[id] - Wx * t;
        rotY[id] = rotY[id] - Wy * t;
        rotZ[id] = rotZ[id] - Wz * t;

        return ballMoving;
    }

    private bool transitionCollision(int id, ref float sX, ref float sY, ref float sZ)
    {
        float deltaX = railX - px[id], deltaY = railY - py[id], deltaZ = railZ - pz[id];
        float dist = Mathf.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
        if (dist < k_BALL_RADIUS)
        {
            float nX = deltaX / dist, nY = deltaY / dist, nZ = deltaZ / dist;

            float push = k_BALL_RADIUS - dist;
            px[id] = px[id] - nX * push; py[id] = py[id] - nY * push; pz[id] = pz[id] - nZ * push;

            float dot = sX * nX + sY * nY + sZ * nZ;

            sX = sX - nX * dot; sY = sY - nY * dot; sZ = sZ - nZ * dot;
            return true;
        }
        return false;
    }

    private bool transitionCollisionBall(int id)
    {
        float sX = vx[id], sY = vy[id], sZ = vz[id];
        bool hit = transitionCollision(id, ref sX, ref sY, ref sZ);
        vx[id] = sX; vy[id] = sY; vz[id] = sZ;
        return hit;
    }

    private void bounceCushion(int id, float nX, float nY, float nZ, bool isPocketBounce)
    {
        if (isHanModel)
        {
            hanCushionModel(id, nX, nY, nZ, isPocketBounce);
            return;
        }

        float railCollisionHeight = py[id] + k_BALL_RADIUS;
        if (railCollisionHeight < k_RAIL_HEIGHT_LOWER)
            railCollisionHeight = k_RAIL_HEIGHT_LOWER - railCollisionHeight;
        else if (railCollisionHeight > k_RAIL_HEIGHT_UPPER)
            railCollisionHeight = k_RAIL_HEIGHT_UPPER - railCollisionHeight;
        else
            railCollisionHeight = 0;

        float normalizedHeight = railCollisionHeight / k_BALL_RADIUS;

        if ((normalizedHeight < 0f ? -normalizedHeight : normalizedHeight + 0f) > 1) { return; }

        if (tvx * nX + tvy * nY + tvz * nZ > 0.0f)
        {
            return;
        }

        Quaternion rq = Quaternion.AngleAxis(Mathf.Atan2(-nZ, -nX) * Mathf.Rad2Deg, Vector3.up);
        Quaternion rb = Quaternion.Inverse(rq);
        rotate(rq, tvx, tvy, tvz);
        float Vx = qrx, Vz = qrz;
        rotate(rq, twx, twy, twz);
        float Wx = qrx, Wy = qry, Wz = qrz;

        float k, k_A, k_B, c, s_x, s_z;

        const float e = 0.7f;

        k_A = (7f / (2f * k_BALL_MASS));
        k_B = (1f / k_BALL_MASS);

        const float cosA = 0.95976971915f;
        const float sinA = 0.28078832987f;

        const float sinA2 = sinA * sinA;
        const float cosA2 = cosA * cosA;

        float v1x = -Vx * ((((2.0f / 7.0f) * sinA2) * cosA2) + (1 + e)) - (((2.0f / 7.0f) * k_BALL_RADIUS) * sinA) * Wz;
        float v1z = (5.0f / 7.0f) * Vz + ((2.0f / 7.0f) * k_BALL_RADIUS) * (Wx * sinA - Wy * cosA) - Vz;
        float v1y = 0.0f;

        s_x = Vx * sinA + Wz;
        s_z = -Vz - Wy * cosA + Wx * sinA;

        k = s_z * (5f / 7f);

        c = Vx * cosA;

        float w1x = k * sinA;
        float w1z = (5.0f / (2.0f * k_BALL_MASS)) * (-s_x / k_A + ((sinA * c * 1.79f) / k_B) * (cosA - sinA));
        float w1y = k * cosA;

        rotate(rb, v1x, v1y, v1z);
        tvx = tvx + qrx; tvy = tvy + qry; tvz = tvz + qrz;
        rotate(rb, w1x, w1y, w1z);
        twx = twx + qrx; twy = twy + qry; twz = twz + qrz;
    }

    private void hanCushionModel(int id, float nX, float nY, float nZ, bool isPocketBounce)
    {
        if (tvx * nX + tvy * nY + tvz * nZ > 0f)
        {
            return;
        }

        float psi = Mathf.Atan2(-nZ, -nX) * Mathf.Rad2Deg;
        Quaternion rq = Quaternion.AngleAxis(psi, Vector3.up);

        Quaternion rb = Quaternion.Inverse(rq);
        rotate(rq, tvx, tvy, tvz);
        float Vx = qrx, Vy = qry, Vz = qrz;
        rotate(rq, twx, twy, twz);
        float Wx = qrx, Wy = qry, Wz = qrz;

        float theta, phi, h, e, M, R, I, k_A, k_B, c, s_x, s_z, mu, PY, PX, PZ, P_yE, P_yS;
        R = k_BALL_RADIUS;
        M = k_BALL_MASS;
        h = k_RAIL_HEIGHT_LOWER;
        float ballCenter = py[id] + R;
        if (ballCenter > k_RAIL_HEIGHT_LOWER)
        {
            float ballUpperContactHeight = k_RAIL_HEIGHT_UPPER + R;
            float lerpT = (ballCenter - k_RAIL_HEIGHT_LOWER) / (ballUpperContactHeight - k_RAIL_HEIGHT_LOWER);
            float clamped = lerpT < 0F ? 0F : (lerpT > 1F ? 1F : lerpT);
            h = k_RAIL_HEIGHT_LOWER + (k_RAIL_HEIGHT_UPPER - k_RAIL_HEIGHT_LOWER) * clamped;
        }

        float negX = -nX, negY = -nY, negZ = -nZ;
        float angleDenominator = Mathf.Sqrt((tvx * tvx + tvy * tvy + tvz * tvz) * (negX * negX + negY * negY + negZ * negZ));
        float angleOfIncidence = 0f;
        if (!(angleDenominator < 1E-15f))
        {
            float cosine = (tvx * negX + tvy * negY + tvz * negZ) / angleDenominator;
            if (cosine < -1f) cosine = -1f;
            else if (cosine > 1f) cosine = 1f;
            angleOfIncidence = Mathf.Acos(cosine) * Mathf.Rad2Deg;
        }
        phi = angleOfIncidence * Mathf.Deg2Rad;

        if (isCushionFrictionConstant)
        {
            mu = k_Cushion_MU * phi;
        }
        else { mu = 0.471f - 0.241f * phi; }

        float P = (h - (py[id] + R));

        theta = Mathf.Asin(P / R);

        theta = theta < 0.4f ? theta : 0.4f;

        float cosTheta = Mathf.Cos(theta);
        float sinTheta = theta;

        float cosPhi = Mathf.Cos(phi);
        float sinPhi = Mathf.Sin(phi);

        s_x = Vx * sinTheta - Vy * cosTheta + R * Wz;
        s_z = -Vz - R * Wy * cosTheta + R * Wx * sinTheta;

        c = (Vx * cosTheta) - (Vy * sinTheta);
        if (isDynamicRestitution)
        {
            float speed = Mathf.Sqrt(Vx * Vx + Vy * Vy + Vz * Vz);
            e = 0.72f - (0.02f * -(speed < 0f ? -speed : speed + 0f));
        }
        else { e = k_E_C; }

        if (isPocketBounce)
        {
            e *= k_POCKET_RESTITUTION;
        }

        I = 2f / 5f * M * R * R;
        k_A = 1f / M + R * R / I;
        k_B = 1f / M;

        float rawYE = (1f + e) * c / k_B;
        P_yE = rawYE < 0f ? -rawYE : rawYE + 0f;
        P_yS = (Mathf.Sqrt((s_x * s_x) + (s_z * s_z)) / k_A);

        if (P_yS <= P_yE)
        {
            PX = -s_x / k_A * sinTheta - (1f + e) * c / k_B * cosTheta;
            PZ = s_z / k_A;
            PY = s_x / k_A * cosTheta - (1f + e) * c / k_B * sinTheta;
        }
        else
        {
            PX = -mu * (1f + e) * c / k_B * cosPhi * sinTheta - (1f + e) * c / k_B * cosTheta;
            PZ = mu * (1f + e) * c / k_B * sinPhi;
            PY = mu * (1f + e) * c / k_B * cosPhi * cosTheta - (1f + e) * c / k_B * sinTheta;
        }

        float v1x = Vx + (PX / M);
        float v1z = Vz + (PZ / M);
        float v1y = Vy + (PY / M) * 0.4f;

        float w1x, w1y, w1z;
        if (py[id] > 0.01f)
        {
            w1x = 0f + (Wx + 0f);
            w1z = 0f + (Wz + 0f);
            w1y = 0f + (Wy + 0f);
        }
        else
        {
            w1x = 0f + (Wx - (R / I) * (PZ * sinTheta));
            w1z = 0f + (Wz + (R / I) * (PX * sinTheta - PY * cosTheta));
            w1y = 0f + (Wy + (R / I) * (PZ * cosTheta));
        }

        rotate(rb, v1x, v1y, v1z);
        tvx = qrx; tvy = qry; tvz = qrz;
        rotate(rb, w1x, w1y, w1z);
        twx = qrx; twy = qry; twz = qrz;
    }

    private bool _phy_ball_pockets(int id, bool is4ball)
    {
        inPocketBounds[id] = 0;
        float Ax = px[id], Ay = py[id], Az = pz[id];
        float aX = Ax < 0f ? -Ax : Ax + 0f;
        float aY = 0f;
        float aZ = Az < 0f ? -Az : Az + 0f;

        if (!is4ball)
        {
            float e1x = aX - vEx, e1y = aY - vEy, e1z = aZ - vEz;
            float e2x = aX - vE2x, e2y = aY - vE2y, e2z = aZ - vE2z;
            if (e1x * e1x + e1y * e1y + e1z * e1z < k_INNER_RADIUS_CORNER_SQ && e2x * e2x + e2y * e2y + e2z * e2z < k_INNER_RADIUS_CORNER_SQ2)
            {
                inPocketBounds[id] = 1;
                if (Ay < -k_BALL_RADIUS)
                {
                    triggerPocketBall(id, false);
                    pocketedTime = Time.time;
                    return true;
                }
                else if (Ay < 0.001f)
                {
                    if (closest_vE)
                    {
                        pocketEdgeTransition(id, Ax, Ay, Az, aX, aY, aZ, vE2x, vE2y, vE2z, k_INNER_RADIUS_CORNER2);
                    }
                    else
                    {
                        pocketEdgeTransition(id, Ax, Ay, Az, aX, aY, aZ, vEx, vEy, vEz, k_INNER_RADIUS_CORNER);
                    }
                }
            }

            float f1x = aX - vFx, f1y = aY - vFy, f1z = aZ - vFz;
            float f2x = aX - vF2x, f2y = aY - vF2y, f2z = aZ - vF2z;
            if (f1x * f1x + f1y * f1y + f1z * f1z < k_INNER_RADIUS_SIDE_SQ && f2x * f2x + f2y * f2y + f2z * f2z < k_INNER_RADIUS_SIDE_SQ2)
            {
                inPocketBounds[id] = 1;
                if (Ay < -k_BALL_RADIUS)
                {
                    triggerPocketBall(id, false);
                    pocketedTime = Time.time;
                    return true;
                }
                else if (Ay < 0.001f)
                {
                    if (closest_vF)
                    {
                        pocketEdgeTransition(id, Ax, Ay, Az, aX, aY, aZ, vF2x, vF2y, vF2z, k_INNER_RADIUS_SIDE2);
                    }
                    else
                    {
                        pocketEdgeTransition(id, Ax, Ay, Az, aX, aY, aZ, vFx, vFy, vFz, k_INNER_RADIUS_SIDE);
                    }
                }
            }
        }

        if (aZ > tableEdgeY)
        {
            if (aZ > tableBoundsY || (Ay < 0 && inPocketBounds[id] == 0))
            {
                table._TriggerBallFallOffFoul();
                triggerPocketBall(id, true);
                pocketedTime = Time.time;
                return true;
            }
        }

        if (aX > tableEdgeX)
        {
            if (aX > tableBoundsX || (Ay < 0 && inPocketBounds[id] == 0))
            {
                table._TriggerBallFallOffFoul();
                triggerPocketBall(id, true);
                pocketedTime = Time.time;
                return true;
            }
        }
        return false;
    }

    private void pocketEdgeTransition(int id, float Ax, float Ay, float Az, float aX, float aY, float aZ, float ppX, float ppY, float ppZ, float radius)
    {
        float sx = Ax >= 0F ? 1F : -1F;
        float sz = Az >= 0F ? 1F : -1F;
        float rdX = aX - ppX, rdY = 0f, rdZ = aZ - ppZ;
        if (aX * rdX + aY * rdY + aZ * rdZ < 0)
        {
            float rdMag = Mathf.Sqrt(rdX * rdX + rdY * rdY + rdZ * rdZ);
            float ndX, ndY, ndZ;
            if (rdMag > 1E-05f)
            {
                ndX = rdX / rdMag; ndY = rdY / rdMag; ndZ = rdZ / rdMag;
            }
            else
            {
                ndX = 0f; ndY = 0f; ndZ = 0f;
            }
            railX = (ppX + ndX * radius) * sx;
            railY = (ppY + ndY * radius) * 1f;
            railZ = (ppZ + ndZ * radius) * sz;
            float negR = -k_BALL_RADIUS;
            railY = negR < Ay ? negR : Ay;
            transitionCollisionBall(id);
        }
    }

    private bool _phy_ball_table_carom(int id)
    {
        if (py[id] > k_RAIL_HEIGHT_UPPER)
        {
            inBounds[id] = 0;
            return false;
        }
        bool shouldBounce = false;
        float sx = px[id] >= 0F ? 1F : -1F;
        float sz = pz[id] >= 0F ? 1F : -1F;
        float npX = px[id] * sx, npY = py[id] * 1f, npZ = pz[id] * sz;
        float NX = 0f, NY = 0f, NZ = 0f;

        tvx = vx[id]; tvy = vy[id]; tvz = vz[id];
        twx = wx[id]; twy = wy[id]; twz = wz[id];

        if (npX > caromEdgeX)
        {
            npX = caromEdgeX;
            NX = -1f; NY = 0f; NZ = 0f;
            bounceCushion(id, NX * sx, NY * sx, NZ * sx, false);
            shouldBounce = true;
        }

        if (npZ > caromEdgeZ)
        {
            npZ = caromEdgeZ;
            NX = 0f; NY = 0f; NZ = -1f;
            bounceCushion(id, NX * sz, NY * sz, NZ * sz, false);
            shouldBounce = true;
        }
        if (shouldBounce)
        {
            if (inBounds[id] != 0)
            {
                px[id] = npX * sx; py[id] = npY * 1f; pz[id] = npZ * sz;
                vx[id] = tvx; vy[id] = tvy; vz[id] = tvz;
                wx[id] = twx; wy[id] = twy; wz[id] = twz;
                table._TriggerBounceCushion(id);
                inBounds[id] = 1;
            }
            else
            {
                shouldBounce = false;
                float posX = npX * sx, posY = npY * 1f, posZ = npZ * sz;

                float mdX = px[id] - posX, mdY = py[id] - posY, mdZ = pz[id] - posZ;
                float moveDistanceMag = Mathf.Sqrt(mdX * mdX + mdY * mdY + mdZ * mdZ);
                transitioning[id] = moveDistanceMag < k_BALL_RADIUS ? 1 : 0;
                float ndX, ndY, ndZ;
                if (moveDistanceMag > 1E-05f)
                {
                    ndX = mdX / moveDistanceMag; ndY = mdY / moveDistanceMag; ndZ = mdZ / moveDistanceMag;
                }
                else
                {
                    ndX = 0f; ndY = 0f; ndZ = 0f;
                }
                railX = posX + ndX * k_BALL_RADIUS;
                railY = posY + ndY * k_BALL_RADIUS;
                railZ = posZ + ndZ * k_BALL_RADIUS;
                railY = k_RAIL_HEIGHT_UPPER - k_BALL_RADIUS;
            }
        }
        else
        {
            transitioning[id] = 0;
            inBounds[id] = 1;
        }

        if (transitioning[id] != 0)
        {
            shouldBounce = transitionCollisionBall(id);
        }
        if (shouldBounce)
        {
            int csl = cushionSounds != null ? cushionSounds.Length : 0;
            if (csl > 0)
            {
                float bounceVolume = NX * (tvx * sx) + NY * (tvy * 1f) + NZ * (tvz * sz);
                if (bounceVolume > 0.5f)
                {
                    float v = bounceVolume - 0.5f;
                    play(id, cushionSounds[UnityEngine.Random.Range(0, csl - 1)], v < 0F ? 0F : (v > 1F ? 1F : v));
                }
            }
        }
        return shouldBounce;
    }

    private bool _phy_ball_table_std(int id)
    {
        if (py[id] > k_RAIL_HEIGHT_UPPER)
        {
            inBounds[id] = 0;
        }
        bool shouldBounce = false;

        float NX = 0f, NY = 0f, NZ = 0f;
        float atvX, atvY, atvZ;
        float dot;

        tvx = vx[id]; tvy = vy[id]; tvz = vz[id];
        twx = wx[id]; twy = wy[id]; twz = wz[id];

        float sx = px[id] >= 0F ? 1F : -1F;
        float sz = pz[id] >= 0F ? 1F : -1F;
        float npX = px[id] * sx, npY = py[id] * 1f, npZ = pz[id] * sz;
        float prX = npX + k_BALL_RADIUS;
        float prZ = npZ + k_BALL_RADIUS;

        if (npX > vAx)
        {
            if (npX > npZ + k_MINOR_REGION_CONST)
            {
                if (npZ < vCz)
                {
                    if (npX > k_TABLE_WIDTH - k_BALL_RADIUS)
                    {
                        npX = k_TABLE_WIDTH - k_BALL_RADIUS;
                        NX = -1f; NY = 0f; NZ = 0f;
                        bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                        shouldBounce = true;
                    }
                }
                else
                {
                    atvX = npX - vCx; atvY = npY - npY; atvZ = npZ - vCz;

                    if (atvX * BYx + atvY * BYy + atvZ * BYz > 0.0f)
                    {
                        float mag = Mathf.Sqrt(atvX * atvX + atvY * atvY + atvZ * atvZ);
                        if (mag < r_k_CUSHION_RADIUS)
                        {
                            if (mag > 1E-05f)
                            {
                                NX = atvX / mag; NY = atvY / mag; NZ = atvZ / mag;
                            }
                            else
                            {
                                NX = 0f; NY = 0f; NZ = 0f;
                            }
                            float y = npY;
                            npX = vCx + NX * r_k_CUSHION_RADIUS; npZ = vCz + NZ * r_k_CUSHION_RADIUS;
                            npY = y;

                            bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                            shouldBounce = true;
                        }
                    }
                    else
                    {
                        atvX = npX - pQx; atvY = npY - pQy; atvZ = npZ - pQz;

                        if (CZNx * atvX + CZNy * atvY + CZNz * atvZ < k_BALL_RADIUS)
                        {
                            dot = atvX * CZx + atvY * CZy + atvZ * CZz;
                            float y = npY;
                            npX = (pQx + CZx * dot) + CZNx * k_BALL_RADIUS;
                            npZ = (pQz + CZz * dot) + CZNz * k_BALL_RADIUS;
                            npY = y;
                            NX = CZNx; NY = CZNy; NZ = CZNz;

                            bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                            shouldBounce = true;
                        }
                        if (furthest_vE)
                        {
                            if (pocketJaw(id, ref npX, ref npY, ref npZ, ref NX, ref NY, ref NZ, sx, sz, vE2x, vE2y, vE2z, k_INNER_RADIUS_CORNER_SQ2, k_INNER_RADIUS_CORNER2, false)) shouldBounce = true;
                        }
                        else
                        {
                            if (pocketJaw(id, ref npX, ref npY, ref npZ, ref NX, ref NY, ref NZ, sx, sz, vEx, vEy, vEz, k_INNER_RADIUS_CORNER_SQ, k_INNER_RADIUS_CORNER, false)) shouldBounce = true;
                        }
                    }
                }
            }
            else
            {
                if (npX < vBx)
                {
                    if (prZ > pNz)
                    {
                        atvX = npX - vAx; atvY = npY - vAy; atvZ = npZ - vAz;
                        float svX = tvx * sx, svZ = tvz * sz;
                        float vX = -svZ, vY = 0.0f, vZ = svX;

                        if (npZ > vAz)
                        {
                            if (vX * atvX + vY * atvY + vZ * atvZ > 0.0f)
                            {
                                atvX = npX - pLx; atvY = npY - pLy; atvZ = npZ - pLz;

                                dot = atvX * ADx + atvY * ADy + atvZ * ADz;
                                npX = pLx + ADx * dot; npY = pLy + ADy * dot; npZ = pLz + ADz * dot;
                                NX = ADNx; NY = ADNy; NZ = ADNz;
                                bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                                shouldBounce = true;
                            }
                            else
                            {
                                npZ = pNz - k_BALL_RADIUS;
                                NX = 0f; NY = 0f; NZ = -1f;
                                bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                                shouldBounce = true;
                            }
                        }
                        else
                        {
                            npZ = pNz - k_BALL_RADIUS;
                            NX = 0f; NY = 0f; NZ = -1f;

                            bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                            shouldBounce = true;
                        }
                    }
                }
                else
                {
                    atvX = npX - vBx; atvY = npY - npY; atvZ = npZ - vBz;

                    if (atvX * BYx + atvY * BYy + atvZ * BYz > 0.0f)
                    {
                        float mag = Mathf.Sqrt(atvX * atvX + atvY * atvY + atvZ * atvZ);
                        if (mag < r_k_CUSHION_RADIUS)
                        {
                            if (mag > 1E-05f)
                            {
                                NX = atvX / mag; NY = atvY / mag; NZ = atvZ / mag;
                            }
                            else
                            {
                                NX = 0f; NY = 0f; NZ = 0f;
                            }
                            float y = npY;
                            npX = vBx + NX * r_k_CUSHION_RADIUS; npZ = vBz + NZ * r_k_CUSHION_RADIUS;
                            npY = y;

                            bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                            shouldBounce = true;
                        }
                    }
                    else
                    {
                        atvX = npX - pPx; atvY = npY - pPy; atvZ = npZ - pPz;

                        if (BYNx * atvX + BYNy * atvY + BYNz * atvZ < k_BALL_RADIUS)
                        {
                            dot = atvX * BYx + atvY * BYy + atvZ * BYz;
                            float y = npY;
                            npX = (pPx + BYx * dot) + BYNx * k_BALL_RADIUS;
                            npZ = (pPz + BYz * dot) + BYNz * k_BALL_RADIUS;
                            npY = y;
                            NX = BYNx; NY = BYNy; NZ = BYNz;

                            bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                            shouldBounce = true;
                        }
                        if (furthest_vE)
                        {
                            if (pocketJaw(id, ref npX, ref npY, ref npZ, ref NX, ref NY, ref NZ, sx, sz, vE2x, vE2y, vE2z, k_INNER_RADIUS_CORNER_SQ2, k_INNER_RADIUS_CORNER2, false)) shouldBounce = true;
                        }
                        else
                        {
                            if (pocketJaw(id, ref npX, ref npY, ref npZ, ref NX, ref NY, ref NZ, sx, sz, vEx, vEy, vEz, k_INNER_RADIUS_CORNER_SQ, k_INNER_RADIUS_CORNER, false)) shouldBounce = true;
                        }
                    }
                }
            }
        }
        else
        {
            atvX = npX - vAx; atvY = npY - npY; atvZ = npZ - vAz;

            if (atvX * ADx + atvY * ADy + atvZ * ADz > 0.0f)
            {
                atvX = npX - vDx; atvY = npY - npY; atvZ = npZ - vDz;

                if (atvX * ADx + atvY * ADy + atvZ * ADz > 0.0f)
                {
                    if (npZ > pKz)
                    {
                        if (prX > pKx)
                        {
                            npX = pKx - k_BALL_RADIUS;
                            NX = 1f; NY = -0f; NZ = -0f;

                            bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                            shouldBounce = true;
                        }
                        if (furthest_vF)
                        {
                            if (pocketJaw(id, ref npX, ref npY, ref npZ, ref NX, ref NY, ref NZ, sx, sz, vF2x, vF2y, vF2z, k_INNER_RADIUS_SIDE_SQ2, k_INNER_RADIUS_SIDE2, true)) shouldBounce = true;
                        }
                        else
                        {
                            if (pocketJaw(id, ref npX, ref npY, ref npZ, ref NX, ref NY, ref NZ, sx, sz, vFx, vFy, vFz, k_INNER_RADIUS_SIDE_SQ, k_INNER_RADIUS_SIDE, true)) shouldBounce = true;
                        }
                    }
                    else
                    {
                        float mag = Mathf.Sqrt(atvX * atvX + atvY * atvY + atvZ * atvZ);
                        if (mag < r_k_CUSHION_RADIUS)
                        {
                            if (mag > 1E-05f)
                            {
                                NX = atvX / mag; NY = atvY / mag; NZ = atvZ / mag;
                            }
                            else
                            {
                                NX = 0f; NY = 0f; NZ = 0f;
                            }
                            float y = npY;
                            npX = vDx + NX * r_k_CUSHION_RADIUS; npZ = vDz + NZ * r_k_CUSHION_RADIUS;
                            npY = y;

                            bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                            shouldBounce = true;
                        }
                    }
                }
                else
                {
                    atvX = npX - pLx; atvY = npY - pLy; atvZ = npZ - pLz;

                    if (ADNx * atvX + ADNy * atvY + ADNz * atvZ < k_BALL_RADIUS)
                    {
                        dot = atvX * ADx + atvY * ADy + atvZ * ADz;
                        float y = npY;
                        npX = (pLx + ADx * dot) + ADNx * k_BALL_RADIUS;
                        npZ = (pLz + ADz * dot) + ADNz * k_BALL_RADIUS;
                        npY = y;
                        NX = ADNx; NY = ADNy; NZ = ADNz;

                        bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                        shouldBounce = true;
                    }
                }
            }
            else
            {
                float mag = Mathf.Sqrt(atvX * atvX + atvY * atvY + atvZ * atvZ);
                if (mag < r_k_CUSHION_RADIUS)
                {
                    if (mag > 1E-05f)
                    {
                        NX = atvX / mag; NY = atvY / mag; NZ = atvZ / mag;
                    }
                    else
                    {
                        NX = 0f; NY = 0f; NZ = 0f;
                    }
                    float y = npY;
                    npX = vAx + NX * r_k_CUSHION_RADIUS; npZ = vAz + NZ * r_k_CUSHION_RADIUS;
                    npY = y;

                    bounceCushion(id, NX * sx, NY * 1f, NZ * sz, false);
                    shouldBounce = true;
                }
            }
        }
        if (shouldBounce)
        {
            if (tvx * (NX * sx) + tvy * (NY * 1f) + tvz * (NZ * sz) < 0)
            {
                if (inBounds[id] != 0)
                {
                    inBounds[id] = 0;
                    transitioning[id] = 1;
                }
            }
            if (inBounds[id] != 0)
            {
                px[id] = npX * sx; py[id] = npY * 1f; pz[id] = npZ * sz;
                vx[id] = tvx; vy[id] = tvy; vz[id] = tvz;
                wx[id] = twx; wy[id] = twy; wz[id] = twz;
                table._TriggerBounceCushion(id);
                inBounds[id] = 1;
                transitioning[id] = 0;
            }
            else
            {
                shouldBounce = false;
                float posX = npX * sx, posY = npY * 1f, posZ = npZ * sz;

                float mdX = px[id] - posX, mdY = py[id] - posY, mdZ = pz[id] - posZ;
                float moveDistanceMag = Mathf.Sqrt(mdX * mdX + mdY * mdY + mdZ * mdZ);
                transitioning[id] = moveDistanceMag < k_BALL_RADIUS ? 1 : 0;
                float ndX, ndY, ndZ;
                if (moveDistanceMag > 1E-05f)
                {
                    ndX = mdX / moveDistanceMag; ndY = mdY / moveDistanceMag; ndZ = mdZ / moveDistanceMag;
                }
                else
                {
                    ndX = 0f; ndY = 0f; ndZ = 0f;
                }
                railX = posX + ndX * k_BALL_RADIUS;
                railY = posY + ndY * k_BALL_RADIUS;
                railZ = posZ + ndZ * k_BALL_RADIUS;
                railY = k_RAIL_HEIGHT_UPPER - k_BALL_RADIUS;
            }
        }
        else
        {
            transitioning[id] = 0;
            inBounds[id] = 1;
        }

        if (transitioning[id] != 0)
        {
            shouldBounce = transitionCollisionBall(id);
        }

        if (shouldBounce)
        {
            if (tvx * (NX * sx) + tvy * (NY * 1f) + tvz * (NZ * sz) < 0)
            {
                if (inBounds[id] != 0)
                {
                    inBounds[id] = 0;
                    transitioning[id] = 1;
                }
            }
            int csl = cushionSounds != null ? cushionSounds.Length : 0;
            if (csl > 0)
            {
                float bounceVolume = NX * (tvx * sx) + NY * (tvy * 1f) + NZ * (tvz * sz);
                if (bounceVolume > 0.5f)
                {
                    float v = bounceVolume - 0.5f;
                    play(id, cushionSounds[UnityEngine.Random.Range(0, csl - 1)], v < 0F ? 0F : (v > 1F ? 1F : v));
                }
            }
        }
        return shouldBounce;
    }

    private bool pocketJaw(int id, ref float npX, ref float npY, ref float npZ, ref float NX, ref float NY, ref float NZ, float sx, float sz,
        float cpX, float cpY, float cpZ, float radiussq, float radius, bool sidePocket)
    {
        float tpX = npX - cpX, tpY = cpY, tpZ = npZ - cpZ;
        float facing = sidePocket ? tpX * cpX + tpY * cpY + tpZ * cpZ : tpX * 1f + tpY * 0f + tpZ * 1f;
        if (facing > 0)
        {
            if (tpX * tpX + tpY * tpY + tpZ * tpZ + k_BALL_DSQR > radiussq)
            {
                float mag = Mathf.Sqrt(tpX * tpX + tpY * tpY + tpZ * tpZ);
                float pnX, pnY, pnZ;
                if (mag > 1E-05f)
                {
                    pnX = tpX / mag; pnY = tpY / mag; pnZ = tpZ / mag;
                }
                else
                {
                    pnX = 0f; pnY = 0f; pnZ = 0f;
                }
                float y = npY;
                float inset = radius - k_BALL_RADIUS;
                npX = cpX + pnX * inset; npY = cpY + pnY * inset; npZ = cpZ + pnZ * inset;
                npY = y;
                NX = -pnX; NY = -pnY; NZ = -pnZ;

                bounceCushion(id, NX * sx, NY * 1f, NZ * sz, true);
                return true;
            }
        }
        return false;
    }

    public void _InitConstants()
    {
        k_TABLE_WIDTH = table.k_TABLE_WIDTH;
        k_TABLE_HEIGHT = table.k_TABLE_HEIGHT;
        k_POCKET_WIDTH_CORNER = table.k_POCKET_WIDTH_CORNER;
        k_POCKET_HEIGHT_CORNER = table.k_POCKET_HEIGHT_CORNER;
        k_POCKET_RADIUS_SIDE = table.k_POCKET_RADIUS_SIDE;
        k_POCKET_DEPTH_SIDE = table.k_POCKET_DEPTH_SIDE;
        k_INNER_RADIUS_CORNER = table.k_INNER_RADIUS_CORNER;
        k_INNER_RADIUS_CORNER_SQ = k_INNER_RADIUS_CORNER * k_INNER_RADIUS_CORNER;
        k_INNER_RADIUS_SIDE = table.k_INNER_RADIUS_SIDE;
        k_INNER_RADIUS_SIDE_SQ = k_INNER_RADIUS_SIDE * k_INNER_RADIUS_SIDE;
        k_INNER_RADIUS_CORNER2 = table.k_INNER_RADIUS_CORNER2;
        k_INNER_RADIUS_CORNER_SQ2 = k_INNER_RADIUS_CORNER2 * k_INNER_RADIUS_CORNER2;
        k_INNER_RADIUS_SIDE2 = table.k_INNER_RADIUS_SIDE2;
        k_INNER_RADIUS_SIDE_SQ2 = k_INNER_RADIUS_SIDE2 * k_INNER_RADIUS_SIDE2;
        k_CUSHION_RADIUS = table.k_CUSHION_RADIUS;
        k_FACING_ANGLE_CORNER = table.k_FACING_ANGLE_CORNER;
        k_FACING_ANGLE_SIDE = table.k_FACING_ANGLE_SIDE;
        k_BALL_DIAMETRE = table.k_BALL_DIAMETRE;
        k_BALL_RADIUS = table.k_BALL_RADIUS;
        float epsilon = 0.000002f;
        k_BALL_DIAMETRESQ = k_BALL_DIAMETRE * k_BALL_DIAMETRE;
        k_BALL_DSQRPE = k_BALL_DIAMETRESQ - epsilon;
        k_BALL_DSQR = k_BALL_DIAMETRE * k_BALL_DIAMETRE;
        k_BALL_RSQR = k_BALL_RADIUS * k_BALL_RADIUS;
        k_BALL_MASS = table.k_BALL_MASS;

        Vector3 cornerPocket = table.k_vE;
        Vector3 cornerPocket2 = table.k_vE2;
        Vector3 sidePocket = table.k_vF;
        Vector3 sidePocket2 = table.k_vF2;
        vEx = cornerPocket.x; vEy = 0; vEz = cornerPocket.z;
        vE2x = cornerPocket2.x; vE2y = 0; vE2z = cornerPocket2.z;
        vFx = sidePocket.x; vFy = 0; vFz = sidePocket.z;
        vF2x = sidePocket2.x; vF2y = 0; vF2z = sidePocket2.z;
        float magE = Mathf.Sqrt(vEx * vEx + vEy * vEy + vEz * vEz);
        float magE2 = Mathf.Sqrt(vE2x * vE2x + vE2y * vE2y + vE2z * vE2z);
        float magF = Mathf.Sqrt(vFx * vFx + vFy * vFy + vFz * vFz);
        float magF2 = Mathf.Sqrt(vF2x * vF2x + vF2y * vF2y + vF2z * vF2z);
        furthest_vE = (magE + k_INNER_RADIUS_CORNER) > (magE2 + k_INNER_RADIUS_CORNER2);
        furthest_vF = (magF + k_INNER_RADIUS_SIDE) > (magF2 + k_INNER_RADIUS_SIDE2);
        closest_vE = (magE - k_INNER_RADIUS_CORNER) < (magE2 - k_INNER_RADIUS_CORNER2);
        closest_vF = (magF - k_INNER_RADIUS_SIDE) < (magF2 - k_INNER_RADIUS_SIDE2);

        k_RAIL_HEIGHT_UPPER = table.k_RAIL_HEIGHT_UPPER;
        k_RAIL_HEIGHT_LOWER_CACHED = table.k_RAIL_HEIGHT_LOWER;
        k_RAIL_DEPTH_WIDTH = table.k_RAIL_DEPTH_WIDTH;
        k_RAIL_DEPTH_HEIGHT = table.k_RAIL_DEPTH_HEIGHT;
        useRailLower = table.useRailLower;
        k_F_SLIDE = table.k_F_SLIDE;
        k_F_ROLL = table.k_F_ROLL;
        k_F_SPIN = table.k_F_SPIN;
        k_F_SPIN_RATE = table.k_F_SPIN_RATE;
        isDRate = table.isDRate;
        K_BOUNCE_FACTOR = table.K_BOUNCE_FACTOR;
        isHanModel = table.isHanModel;
        k_E_C = table.k_E_C;
        isDynamicRestitution = table.isDynamicRestitution;
        isCushionFrictionConstant = table.isCushionFrictionConstant;
        k_Cushion_MU = table.k_Cushion_MU;
        k_BALL_E = table.k_BALL_E;
        muFactor = table.muFactor;
        k_POCKET_RESTITUTION = table.k_POCKET_RESTITUTION;

        r_k_CUSHION_RADIUS = k_CUSHION_RADIUS + k_BALL_RADIUS;
        vertRadiusSQRPE = r_k_CUSHION_RADIUS * r_k_CUSHION_RADIUS - 0.000002f;
        vertRadius = Mathf.Sqrt(vertRadiusSQRPE);
        tenToMinus3 = Mathf.Pow(10, -3);
        k_BALL_RADIUS_SQRPE = k_BALL_RADIUS * k_BALL_RADIUS - 0.000002f;

        Collider[] collider = table.GetComponentsInChildren<Collider>();
        for (int i = 0; i < collider.Length; i++)
        {
            collider[i].enabled = true;
        }

        k_MINOR_REGION_CONST = k_TABLE_WIDTH - k_TABLE_HEIGHT;

        vAx = k_POCKET_RADIUS_SIDE;
        vAz = k_TABLE_HEIGHT + k_CUSHION_RADIUS;

        vBx = k_TABLE_WIDTH;
        vBz = k_TABLE_HEIGHT + k_CUSHION_RADIUS;

        vCx = k_TABLE_WIDTH + k_CUSHION_RADIUS;
        vCz = k_TABLE_HEIGHT;

        rotate(Quaternion.AngleAxis(-k_FACING_ANGLE_SIDE, Vector3.up), k_POCKET_DEPTH_SIDE, 0f, 0f);
        vDx = vAx + qrx; vDy = vAy + qry; vDz = vAz + qrz;

        rotate(Quaternion.AngleAxis(k_FACING_ANGLE_CORNER, Vector3.up), -.2f, 0f, 0f);
        float vYx = vBx + qrx, vYy = vBy + qry, vYz = vBz + qrz;

        rotate(Quaternion.AngleAxis(-k_FACING_ANGLE_CORNER, Vector3.up), 0f, 0f, -.2f);
        float vZx = vCx + qrx, vZy = vCy + qry, vZz = vCz + qrz;

        float adX = vDx - vAx, adY = vDy - vAy, adZ = vDz - vAz;
        float adMag = Mathf.Sqrt(adX * adX + adY * adY + adZ * adZ);
        if (adMag > 1E-05f) { ADx = adX / adMag; ADy = adY / adMag; ADz = adZ / adMag; } else { ADx = 0f; ADy = 0f; ADz = 0f; }
        ADNx = -ADz;
        ADNz = ADx;

        float byX = vBx - vYx, byY = vBy - vYy, byZ = vBz - vYz;
        float byMag = Mathf.Sqrt(byX * byX + byY * byY + byZ * byZ);
        if (byMag > 1E-05f) { BYx = byX / byMag; BYy = byY / byMag; BYz = byZ / byMag; } else { BYx = 0f; BYy = 0f; BYz = 0f; }
        BYNx = -BYz;
        BYNz = BYx;

        float czX = vCx - vZx, czY = vCy - vZy, czZ = vCz - vZz;
        float czMag = Mathf.Sqrt(czX * czX + czY * czY + czZ * czZ);
        if (czMag > 1E-05f) { CZx = czX / czMag; CZy = czY / czMag; CZz = czZ / czMag; } else { CZx = 0f; CZy = 0f; CZz = 0f; }
        CZNx = CZz;
        CZNz = -CZx;

        pNx = vAx; pNy = vAy; pNz = vAz;
        pNz -= k_CUSHION_RADIUS;

        pLx = vDx + ADNx * k_CUSHION_RADIUS; pLy = vDy + ADNy * k_CUSHION_RADIUS; pLz = vDz + ADNz * k_CUSHION_RADIUS;

        pKx = vDx; pKy = vDy; pKz = vDz;
        pKx -= k_CUSHION_RADIUS;

        float pOx = vBx, pOy = vBy, pOz = vBz;
        pOz -= k_CUSHION_RADIUS;
        pPx = vBx + BYNx * k_CUSHION_RADIUS; pPy = vBy + BYNy * k_CUSHION_RADIUS; pPz = vBz + BYNz * k_CUSHION_RADIUS;
        pQx = vCx + CZNx * k_CUSHION_RADIUS; pQy = vCy + CZNy * k_CUSHION_RADIUS; pQz = vCz + CZNz * k_CUSHION_RADIUS;

        pRx = vCx; pRy = vCy; pRz = vCz;
        pRx -= k_CUSHION_RADIUS;

        tableEdgeX = k_TABLE_WIDTH + k_RAIL_DEPTH_WIDTH + k_BALL_RADIUS;
        tableEdgeY = k_TABLE_HEIGHT + k_RAIL_DEPTH_HEIGHT + k_BALL_RADIUS;
        tableBoundsX = max3(
            k_TABLE_WIDTH + k_RAIL_DEPTH_WIDTH + k_BALL_RADIUS,
            furthest_vE ? (vE2x + k_INNER_RADIUS_CORNER2) : (vEx + k_INNER_RADIUS_CORNER),
            furthest_vF ? (vF2x + k_INNER_RADIUS_SIDE2) : (vFx + k_INNER_RADIUS_SIDE)
        );
        tableBoundsY = max3(
            k_TABLE_HEIGHT + k_RAIL_DEPTH_HEIGHT + k_BALL_RADIUS,
            furthest_vE ? (vE2z + k_INNER_RADIUS_CORNER2) : (vEz + k_INNER_RADIUS_CORNER),
            furthest_vF ? (vF2z + k_INNER_RADIUS_SIDE2) : (vFz + k_INNER_RADIUS_SIDE)
        );

        caromEdgeX = pRx - k_BALL_RADIUS;
        caromEdgeZ = pOz - k_BALL_RADIUS;

        float pMx = vAx + ADNx * k_CUSHION_RADIUS;

        float sideXdifA = vAx - pMx;
        float sideXdifD = vDx - pMx;
        float sideXdifN = pNx - pMx;
        float sideXdifL = pLx - pMx;
        float sideXdifK = pKx - pMx;
        pNx += k_POCKET_RADIUS_SIDE;
        pMx = pNx;
        vAx = pMx + sideXdifA;
        vDx = pMx + sideXdifD;
        pNx = pMx + sideXdifN;
        pLx = pMx + sideXdifL;
        pKx = pMx + sideXdifK;

        float widthXdifB = vBx - pPx;
        pOx -= k_POCKET_WIDTH_CORNER;
        pPx = pOx;
        vBx = pPx + widthXdifB;

        float heightZdifC = vCz - pQz;
        pRz -= k_POCKET_HEIGHT_CORNER;
        pQz = pRz;
        vCz = pQz + heightZdifC;

        vertX[0] = vAx; vertY[0] = vAy; vertZ[0] = vAz;
        vertX[1] = -vAx; vertY[1] = vAy; vertZ[1] = vAz;
        vertX[2] = vBx; vertY[2] = vBy; vertZ[2] = vBz;
        vertX[3] = vCx; vertY[3] = vCy; vertZ[3] = vCz;
        vertX[4] = vDx; vertY[4] = vDy; vertZ[4] = vDz;
    }

    private float max3(float a, float b, float c)
    {
        float m = a;
        if (b > m) m = b;
        if (c > m) m = c;
        return m;
    }

    private void rotate(Quaternion rotation, float ax, float ay, float az)
    {
        float qx = rotation.x, qy = rotation.y, qz = rotation.z, qw = rotation.w;
        float x2 = qx * 2F, y2 = qy * 2F, z2 = qz * 2F;
        float xx = qx * x2, yy = qy * y2, zz = qz * z2;
        float xy = qx * y2, xz = qx * z2, yz = qy * z2;
        float wx2 = qw * x2, wy2 = qw * y2, wz2 = qw * z2;
        qrx = (1F - (yy + zz)) * ax + (xy - wz2) * ay + (xz + wy2) * az;
        qry = (xy + wz2) * ax + (1F - (xx + zz)) * ay + (yz - wx2) * az;
        qrz = (xz - wy2) * ax + (yz + wx2) * ay + (1F - (xx + yy)) * az;
    }

    public void _ResetSimulationVariables()
    {
        jumpShotFlewOver = cueBallHasCollided = false;
        BasisVectorArrayShim.Split(table.ballsP, px, py, pz);
        for (int i = 0; i < ballCount; i++)
        {
            inBounds[i] = py[i] == 0 ? 1 : 0;
            inPocketBounds[i] = 0;
            transitioning[i] = 0;
        }

        if (useRailLower)
        {
            k_RAIL_HEIGHT_LOWER = k_RAIL_HEIGHT_LOWER_CACHED;
        }
        else
        {
            switch (table.gameModeLocal)
            {
                case 0:
                    k_RAIL_HEIGHT_LOWER = k_BALL_DIAMETRE * 0.635f;
                    break;
                case 1:
                    k_RAIL_HEIGHT_LOWER = k_BALL_DIAMETRE * 0.635f;
                    break;
                case 2:
                    k_RAIL_HEIGHT_LOWER = k_BALL_DIAMETRE * 0.6504065040650407f;
                    break;
                case 3:
                    k_RAIL_HEIGHT_LOWER = k_BALL_DIAMETRE * 0.6504065040650407f;
                    break;
                case 4:
                    k_RAIL_HEIGHT_LOWER = k_BALL_DIAMETRE * 0.7f;
                    break;
            }
        }
    }

    private bool isCueBallTouching()
    {
        uint skip;
        if (table.is8Ball || table.isSnooker6Red) skip = 0x1u;
        else if (table.is9Ball) skip = 0xFC01u;
        else skip = 0x1FFFu;
        Vector3[] positions = table.ballsP;
        return BasisSphereCastShim.FirstOverlap(positions[0], positions, skip, k_BALL_DSQR) >= 0;
    }

    private void tickCue()
    {
        GameObject cuetip = table.activeCue._GetCuetip();
        Transform tip = cuetip.transform;

        Vector3 lpos = table_Surface.InverseTransformPoint(tip.position);
        float l2x = lpos.x, l2y = lpos.y, l2z = lpos.z;

        if (table.canPlayLocal)
        {
            bool isContact = false;

            if (table.isReposition)
            {
                Transform marker = table.markerObj.transform;
                marker.position = balls[0].transform.position + new Vector3(0, k_BALL_RADIUS, 0);
                marker.localScale = Vector3.one * .3f;
                isContact = isCueBallTouching();
                if (markerMaterial == null) markerMaterial = table.markerObj.GetComponent<MeshRenderer>().material;
                markerMaterial.SetColor("_Color", isContact ? markerColorNo : markerColorYes);
            }

            Vector3 cueball = table.ballsP[0];
            float cbx = cueball.x, cby = cueball.y, cbz = cueball.z;

            if (table.canHitCueBall && !isContact)
            {
                float sweep_time_ball = (cbx - cueLlposX) * cueDirX + (cby - cueLlposY) * cueDirY + (cbz - cueLlposZ) * cueDirZ;
                float bx = cueLlposX - l2x, by = cueLlposY - l2y, bz = cueLlposZ - l2z;

                if (sweep_time_ball > 0.0f && sweep_time_ball < Mathf.Sqrt(bx * bx + by * by + bz * bz))
                {
                    l2x = cueLlposX + cueDirX * sweep_time_ball;
                    l2y = cueLlposY + cueDirY * sweep_time_ball;
                    l2z = cueLlposZ + cueDirZ * sweep_time_ball;
                }

                float tx = l2x - cbx, ty = l2y - cby, tz = l2z - cbz;
                if (tx * tx + ty * ty + tz * tz < k_BALL_RSQR)
                {
                    float fx = l2x - cueLlposX, fy = l2y - cueLlposY, fz = l2z - cueLlposZ;
                    float V0 = Mathf.Min(Mathf.Sqrt(fx * fx + fy * fy + fz * fz) / Time.fixedDeltaTime, 999.0f);
                    applyPhysics(V0);

                    table._TriggerCueBallHit();
                }
            }
            else
            {
                Vector3 forward = tip.forward;
                Vector3 vdir = space.InverseTransformVector(forward);
                cueDirX = vdir.x; cueDirY = vdir.y; cueDirZ = vdir.z;

                float[] r = ray;
                r[BasisSphereCastShim.RayOriginX] = l2x;
                r[BasisSphereCastShim.RayOriginY] = l2y;
                r[BasisSphereCastShim.RayOriginZ] = l2z;
                r[BasisSphereCastShim.RayDirectionX] = cueDirX;
                r[BasisSphereCastShim.RayDirectionY] = cueDirY;
                r[BasisSphereCastShim.RayDirectionZ] = cueDirZ;
                if (BasisSphereCastShim.RaySphere(r, cbx, cby, cbz, k_BALL_RSQR))
                {
                    cueHitX = r[BasisSphereCastShim.RayHitX]; cueHitY = r[BasisSphereCastShim.RayHitY]; cueHitZ = r[BasisSphereCastShim.RayHitZ];
                    if (!table.noGuidelineLocal)
                    {
                        table.guideline.SetActive(true);
                        table.devhit.SetActive(true);
                        table.guideline2.SetActive(table.isPracticeMode);
                    }
                    if (table.markerObj.activeSelf) { table.markerObj.SetActive(false); }

                    Vector3 q = table_Surface.InverseTransformDirection(forward);
                    Vector3 up = table_Surface.up;
                    solveCueContact(q.x, q.y, q.z, cbx, cby, cbz, up.x, up.y, up.z);
                    table.devhit.transform.localPosition = new Vector3(cueQX, cueQY, cueQZ);

                    float a = cueA, b = cueB, c = cueC, cosTheta = cueCos, sinTheta = cueSin;

                    float V0 = 5;
                    float k_CUE_MASS = 0.5f;
                    float F = 2 * k_BALL_MASS * V0 / (1 + k_BALL_MASS / k_CUE_MASS + 5 / (2 * k_BALL_RADIUS) * ((a * a) + (b * b) * (cosTheta * cosTheta) + (c * c) * (sinTheta * sinTheta) - 2 * b * c * cosTheta * sinTheta));

                    float vy0 = -F / k_BALL_MASS * cosTheta;
                    float vz0 = -F / k_BALL_MASS * sinTheta;

                    float m_e = 0.02f;

                    float aOverR = a / k_BALL_RADIUS;
                    float alpha = -Mathf.Atan(
                       (5f / 2f * a / k_BALL_RADIUS * Mathf.Sqrt(1f - (aOverR * aOverR))) /
                       (1 + k_BALL_MASS / m_e + 5f / 2f * (1f - (aOverR * aOverR)))
                    ) * 180 / Mathf.PI;

                    rotate(Quaternion.FromToRotation(Vector3.back, new Vector3(cueJX, cueJY, cueJZ)), -0f, vz0, -vy0);
                    rotate(Quaternion.AngleAxis(alpha, up), qrx, qry, qrz);

                    cue_fdir = Mathf.Atan2(qrz, qrx);

                    Transform guide = table.guideline.transform;
                    guide.localPosition = cueball;
                    guide.localEulerAngles = new Vector3(0.0f, -cue_fdir * Mathf.Rad2Deg, 0.0f);
                    Transform guide2 = table.guideline2.transform;
                    guide2.localPosition = cueball;
                    guide2.rotation = Quaternion.Euler(new Vector3(0.0f, tip.eulerAngles.y - 90, 0.0f));
                }
                else
                {
                    if (!table.markerObj.activeSelf && table.isReposition) { table.markerObj.SetActive(true); }
                    table.devhit.SetActive(false);
                    table.guideline.SetActive(false);
                    table.guideline2.SetActive(false);
                }
            }
        }

        cueLlposX = l2x; cueLlposY = l2y; cueLlposZ = l2z;
    }

    private void solveCueContact(float qx, float qy, float qz, float ox, float oy, float oz, float ux, float uy, float uz)
    {
        float upSq = ux * ux + uy * uy + uz * uz;
        if (upSq < float.Epsilon)
        {
            cueJX = -qx; cueJY = -qy; cueJZ = -qz;
        }
        else
        {
            float along = (qx * ux + qy * uy + qz * uz) / upSq;
            cueJX = -(qx - ux * along); cueJY = -(qy - uy * along); cueJZ = -(qz - uz * along);
        }

        float ix = cueJY * uz - cueJZ * uy;
        float iy = cueJZ * ux - cueJX * uz;
        float iz = cueJX * uy - cueJY * ux;
        float iMag = Mathf.Sqrt(ix * ix + iy * iy + iz * iz);
        float nx, ny, nz;
        if (iMag > 1E-05f)
        {
            nx = ix / iMag; ny = iy / iMag; nz = iz / iMag;
        }
        else
        {
            nx = 0f; ny = 0f; nz = 0f;
        }
        float planeDistance = -(nx * ox + ny * oy + nz * oz);

        float Qx = cueHitX, Qy = cueHitY, Qz = cueHitZ;
        float rx = Qx - ox, ry = Qy - oy, rz = Qz - oz;
        float qSq = qx * qx + qy * qy + qz * qz;
        float fx, fy, fz;
        if (qSq < float.Epsilon)
        {
            fx = rx; fy = ry; fz = rz;
        }
        else
        {
            float along = (rx * qx + ry * qy + rz * qz) / qSq;
            fx = rx - qx * along; fy = ry - qy * along; fz = rz - qz * along;
        }
        float fMag = Mathf.Sqrt(fx * fx + fy * fy + fz * fz);
        if (fMag / k_BALL_RADIUS > CueMaxHitRadius)
        {
            float tx, ty, tz;
            if (fMag > 1E-05f)
            {
                tx = fx / fMag; ty = fy / fMag; tz = fz / fMag;
            }
            else
            {
                tx = 0f; ty = 0f; tz = 0f;
            }
            float sx = (ox + tx * k_BALL_RADIUS * CueMaxHitRadius) - qx * k_BALL_DIAMETRE;
            float sy = (oy + ty * k_BALL_RADIUS * CueMaxHitRadius) - qy * k_BALL_DIAMETRE;
            float sz = (oz + tz * k_BALL_RADIUS * CueMaxHitRadius) - qz * k_BALL_DIAMETRE;
            float[] r = ray;
            r[BasisSphereCastShim.RayOriginX] = sx;
            r[BasisSphereCastShim.RayOriginY] = sy;
            r[BasisSphereCastShim.RayOriginZ] = sz;
            r[BasisSphereCastShim.RayDirectionX] = qx;
            r[BasisSphereCastShim.RayDirectionY] = qy;
            r[BasisSphereCastShim.RayDirectionZ] = qz;
            if (BasisSphereCastShim.RaySphere(r, ox, oy, oz, k_BALL_RADIUS_SQRPE))
            {
                cueHitX = r[BasisSphereCastShim.RayHitX]; cueHitY = r[BasisSphereCastShim.RayHitY]; cueHitZ = r[BasisSphereCastShim.RayHitZ];
            }
            Qx = cueHitX; Qy = cueHitY; Qz = cueHitZ;
        }
        cueQX = Qx; cueQY = Qy; cueQZ = Qz;

        cueA = (nx * Qx + ny * Qy + nz * Qz) + planeDistance;
        cueB = Qy - oy;
        cueC = Mathf.Sqrt(k_BALL_RADIUS * k_BALL_RADIUS - cueA * cueA - cueB * cueB);

        float adj = Mathf.Sqrt(qx * qx + qz * qz);
        float opp = qy;
        cueTheta = -Mathf.Atan(opp / adj);

        cueCos = Mathf.Cos(cueTheta);
        cueSin = Mathf.Sin(cueTheta);
    }

    public void _ApplyPhysics()
    {
        applyPhysics(inV0);
    }

    private void applyPhysics(float V0)
    {
        GameObject cuetip = table.activeCue._GetCuetip();

        Vector3 q = table_Surface.InverseTransformDirection(cuetip.transform.forward);
        Vector3 o = table.ballsP[0];
        Vector3 up = table_Surface.up;
        solveCueContact(q.x, q.y, q.z, o.x, o.y, o.z, up.x, up.y, up.z);
        float a = cueA, b = cueB, c = cueC, theta = cueTheta, cosTheta = cueCos, sinTheta = cueSin;

        float k_CUE_MASS = 0.5f;
        float F = 2 * k_BALL_MASS * V0 / (1 + k_BALL_MASS / k_CUE_MASS + 5 / (2 * k_BALL_RADIUS) * ((a * a) + (b * b) * (cosTheta * cosTheta) + (c * c) * (sinTheta * sinTheta) - 2 * b * c * cosTheta * sinTheta));
        table._LogWarn("cue ball was hit at (" + a.ToString("F2") + "," + b.ToString("F2") + "," + c.ToString("F2") + ") with angle " + theta * Mathf.Rad2Deg + " and initial velocity " + V0.ToString("F2") + "m/s");

        float I = 2f / 5f * k_BALL_MASS * (k_BALL_RADIUS * k_BALL_RADIUS);
        float velY = -F / k_BALL_MASS * cosTheta;
        float velZ = -F / k_BALL_MASS * sinTheta;
        float invI = 1 / I;
        float spinX = (-c * F * sinTheta + b * F * cosTheta) * invI;
        float spinY = (a * F * sinTheta) * invI;
        float spinZ = (-a * F * cosTheta) * invI;

        spinX = -spinX;
        table._LogWarn("initial cue ball velocities are v=" + new Vector3(0, velY, velZ) + ", w=" + new Vector3(spinX, spinY, spinZ));

        float m_e = 0.02f;

        float aOverR = a / k_BALL_RADIUS;
        float alpha = -Mathf.Atan(
            (5f / 2f * a / k_BALL_RADIUS * Mathf.Sqrt(1f - (aOverR * aOverR))) /
            (1 + k_BALL_MASS / m_e + 5f / 2f * (1f - (aOverR * aOverR)))
        ) * 180 / Mathf.PI;

        float rvy = velZ;
        if (rvy > 0)
        {
            rvy = 0;
            table._Log("prevented scooping");
        }

        Quaternion r = Quaternion.FromToRotation(Vector3.back, new Vector3(cueJX, cueJY, cueJZ));
        rotate(r, spinX, -spinZ, spinY);
        Vector3 w = new Vector3(qrx, qry, qrz);
        rotate(r, -0f, rvy, -velY);
        rotate(Quaternion.AngleAxis(alpha, up), qrx, qry, qrz);

        table.ballsV[0] = new Vector3(qrx, qry, qrz);
        table.ballsW[0] = w;
    }
}
