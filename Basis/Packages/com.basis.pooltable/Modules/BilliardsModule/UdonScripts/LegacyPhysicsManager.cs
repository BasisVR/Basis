
using Basis;
using System;

using UnityEngine;




public class LegacyPhysicsManager : MonoBehaviour
{
    public string PHYSICSNAME = "<color=#4287F5>Legacy</color>";
#if HT_QUEST
   private const float k_MAX_DELTA = 0.05f; // max time to process per frame on quest (~4)
#else
    private const float k_MAX_DELTA = 0.1f; // max time to process per frame on pc (~8)
#endif
    private const float k_FIXED_TIME_STEP = 0.0125f; // time step in seconds per iteration
    private const float k_BALL_DSQRPE = 0.003598f;            // ball diameter squared plus epsilon
    private const float k_BALL_DIAMETREPE = 0.06001f;                // width of ball
    private const float k_BALL_DIAMETREPESQ = 0.0036012001f;                // width of ball
    private const float k_BALL_DIAMETRE = 0.06f;                // width of ball
    private const float k_BALL_RADIUS = 0.03f;
    private const float k_BALL_1OR = 33.3333333333f;       // 1 over ball radius
    private const float k_GRAVITY = 9.80665f;             // Earths gravitational acceleration
    private const float k_BALL_DSQR = 0.0036f;              // ball diameter squared
    private const float k_BALL_MASS = 0.16f;                // Weight of ball in kg
    private const float k_BALL_RSQR = 0.0009f;              // ball radius squared
    const float k_F_SLIDE = 0.2f;                 // Friction coefficient of sliding
    const float k_F_ROLL = 0.01f;                // Friction coefficient of rolling

    private Color markerColorYes = new Color(0.0f, 1.0f, 0.0f, 1.0f);
    private Color markerColorNo = new Color(1.0f, 0.0f, 0.0f, 1.0f);

    private Vector3 k_CONTACT_POINT = new Vector3(0.0f, -0.03f, 0.0f);

    [SerializeField] AudioClip[] hitSounds;
    [SerializeField] public Transform transform_Surface;

    private BilliardsModule table;

    private float accumulatedTime;
    private float lastTimestamp;

    private GameObject[] balls;
    private Vector3[] balls_P;
    private Vector3[] balls_V;
    private Vector3[] balls_W;
    private float k_INNER_RADIUS;
    private float k_INNER_RADIUS_SQ;
    private Vector3 k_vE;
    private Vector3 k_vF;

    float k_TABLE_WIDTH;
    float k_TABLE_HEIGHT;
    float k_POCKET_WIDTH_CORNER;
    float k_POCKET_HEIGHT_CORNER;
    float k_CUSHION_RADIUS;

    [NonSerialized] public BilliardsModule table_;
    public void _Init()
    {
        table = table_;

        _InitConstants();

        // copy some pointers
        balls = table.balls;
        balls_P = table.ballsP;
        balls_V = table.ballsV;
        balls_W = table.ballsW;
    }

    public void _FixedTick()
    {
        float now = Time.timeSinceLevelLoad;
        float delta = now - lastTimestamp;
        lastTimestamp = now;

        if (table.gameLive)
        {
            tickCue();
        }

        if (!table.isLocalSimulationRunning) return;

        float newAccumulatedTime = Mathf.Clamp(accumulatedTime + Time.fixedDeltaTime, 0, k_MAX_DELTA);
        while (newAccumulatedTime >= k_FIXED_TIME_STEP)
        {
            table._BeginPerf(table.PERF_PHYSICS_MAIN);
            tickOnce();
            table._EndPerf(table.PERF_PHYSICS_MAIN);
            newAccumulatedTime -= k_FIXED_TIME_STEP;
        }

        accumulatedTime = newAccumulatedTime;
    }

    private void tickCue()
    {
        GameObject cuetip = table.activeCue._GetCuetip();

        cue_lpos = transform_Surface.InverseTransformPoint(cuetip.transform.position);
        Vector3 lpos2 = cue_lpos;

        // if shot is prepared for next hit
        if (table.canPlayLocal)
        {
            bool isContact = false;

            if (table.isReposition)
            {
                table.markerObj.transform.position = balls[0].transform.position;
                isContact = isCueBallTouching();
                if (isContact)
                {
                    table.markerObj.GetComponent<MeshRenderer>().material.SetColor("_Color", markerColorNo);
                }
                else
                {
                    table.markerObj.GetComponent<MeshRenderer>().material.SetColor("_Color", markerColorYes);
                }
            }

            Vector3 cueball_pos = balls_P[0];

            if (table.canHitCueBall && !isContact)
            {
                float sweep_time_ball = Vector3.Dot(cueball_pos - cue_llpos, cue_vdir);

                // Check for potential skips due to low frame rate
                if (sweep_time_ball > 0.0f && sweep_time_ball < (cue_llpos - lpos2).magnitude)
                {
                    lpos2 = cue_llpos + cue_vdir * sweep_time_ball;
                }

                // Hit condition is when cuetip is gone inside ball
                if ((lpos2 - cueball_pos).sqrMagnitude < k_BALL_RSQR)
                {
                    Vector3 horizontal_force = lpos2 - cue_llpos;

                    horizontal_force.y = 0.0f;

                    // Compute velocity delta
                    float vel = (horizontal_force.magnitude / Time.fixedDeltaTime) * 1.5f;
                    multiplier = -50.0f;
                    applyPhysics(vel);

                    table._TriggerCueBallHit();
                }
            }
            else
            {
                cue_vdir = this.transform.InverseTransformVector(cuetip.transform.forward);//new Vector2( cuetip.transform.forward.z, -cuetip.transform.forward.x ).normalized;

                // Get where the cue will strike the ball
                if (_phy_ray_sphere(lpos2, cue_vdir, cueball_pos))
                {
                    if (!table.noGuidelineLocal)
                    {
                        table.guideline.SetActive(true);
                        table.devhit.SetActive(true);
                        if (table.isPracticeMode)
                            table.guideline2.SetActive(true);
                        else
                            table.guideline2.SetActive(false);
                    }
                    if (table.markerObj.activeSelf) { table.markerObj.SetActive(false); }
                    table.devhit.transform.localPosition = RaySphere_output;

                    cue_shotdir = cue_vdir;
                    cue_shotdir.y = 0.0f;

                    Vector3 scuffdir = (cueball_pos - RaySphere_output);
                    scuffdir.y = 0.0f;
                    cue_shotdir += scuffdir.normalized * 0.17f;

                    cue_fdir = Mathf.Atan2(cue_shotdir.z, cue_shotdir.x);

                    // Update the prediction line direction
                    table.guideline.transform.localPosition = balls_P[0];
                    table.guideline.transform.localEulerAngles = new Vector3(0.0f, -cue_fdir * Mathf.Rad2Deg, 0.0f);
                    table.guideline2.transform.localPosition = balls_P[0];
                    table.guideline2.transform.rotation = Quaternion.Euler(new Vector3(0.0f, cuetip.transform.eulerAngles.y - 90, 0.0f));
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

        cue_llpos = lpos2;
    }

    // Run one physics iteration for all balls
    private void tickOnce()
    {
        bool ballsMoving = false;

        uint sn_pocketed = table.ballsPocketedLocal;

        // Cue angular velocity
        table._BeginPerf(table.PERF_PHYSICS_BALL);
        bool[] moved = new bool[balls.Length];

        if ((sn_pocketed & 0x1U) == 0)
        {
            if (balls_P[0].y < 0)
            {
                balls_P[0].y = 0;
                balls_P[0].y = -balls_P[0].y * 0.5f; // bounce with restitution
            }

            // Apply movement
            Vector3 deltaPos = calculateDeltaPosition(sn_pocketed);
            balls_P[0] += deltaPos;
            moved[0] = deltaPos != Vector3.zero;

            ballsMoving |= stepOneBall(0, balls, balls_P, balls_V, balls_W, sn_pocketed, moved);
        }

        // Run main simulation / inter-ball collision

        uint ball_bit = 0x1u;
        for (int i = 1; i < 16; i++)
        {
            ball_bit <<= 1;

            if ((ball_bit & sn_pocketed) == 0U)
            {
                balls_V[i].y = 0;
                balls_P[i].y = 0;

                Vector3 deltaPos = balls_V[i] * k_FIXED_TIME_STEP;
                balls_P[i] += deltaPos;
                moved[i] = deltaPos != Vector3.zero;

                ballsMoving |= stepOneBall(i, balls, balls_P, balls_V, balls_W, sn_pocketed, moved);
            }
        }
        table._EndPerf(table.PERF_PHYSICS_BALL);

        // Check if simulation has settled
        if (!ballsMoving)
        {
            table._TriggerSimulationEnded(false);
            return;
        }

        bool canCueBallBounceOffCushion = balls_P[0].y < k_BALL_RADIUS;

        table._BeginPerf(table.PERF_PHYSICS_CUSHION);
        if (table.is4Ball)
        {
            if (canCueBallBounceOffCushion && moved[0]) _phy_ball_table_carom(0);
            if (moved[13]) _phy_ball_table_carom(13);
            if (moved[14]) _phy_ball_table_carom(14);
            if (moved[15]) _phy_ball_table_carom(15);
        }
        else
        {
            ball_bit = 0x1U;
            // Run edge collision
            for (int i = 0; i < 16; i++)
            {
                if (moved[i] && (ball_bit & sn_pocketed) == 0U && (i != 0 || canCueBallBounceOffCushion))
                {
                    _phy_ball_table_std(i);
                }

                ball_bit <<= 1;
            }
        }
        table._EndPerf(table.PERF_PHYSICS_CUSHION);

        bool outOfBounds = false;
        if ((sn_pocketed & 0x01u) == 0x00u)
        {
            if (Mathf.Abs(balls_P[0].x) > table.k_TABLE_WIDTH + 0.1 || Mathf.Abs(balls_P[0].z) > table.k_TABLE_HEIGHT + 0.1)
            {
                table._TriggerPocketBall(0, true);
                table._Log("out of bounds! " + balls_P[0].ToString());
                outOfBounds = true;
            }
        }

        if (table.is4Ball) return;

        ball_bit = 0x1U;

        // Run triggers
        table._BeginPerf(table.PERF_PHYSICS_POCKET);
        for (int i = 0; i < 16; i++)
        {
            if (moved[i] && (ball_bit & sn_pocketed) == 0U && (i != 0 || !outOfBounds))
            {
                if (i != 0 || canCueBallBounceOffCushion)
                {
                    _phy_ball_pockets(i);
                }
            }

            ball_bit <<= 1;
        }
        table._EndPerf(table.PERF_PHYSICS_POCKET);
    }

    private Vector3 calculateDeltaPosition(uint sn_pocketed)
    {
        // Get what will be the next position
        Vector3 originalDelta = balls_V[0] * k_FIXED_TIME_STEP;

        Vector3 norm = balls_V[0].normalized;

        Vector3 h;
        float lf, s, nmag;

        // Closest found values
        float minlf = float.MaxValue;
        int minid = 0;
        float mins = 0;

        // Loop balls look for collisions
        uint ball_bit = 0x1U;

        // Loop balls look for collisions
        for (int i = 1; i < 16; i++)
        {
            ball_bit <<= 1;

            if ((ball_bit & sn_pocketed) != 0U)
                continue;

            h = balls_P[i] - balls_P[0];
            lf = Vector3.Dot(norm, h);
            s = k_BALL_DSQRPE - Vector3.Dot(h, h) + lf * lf;

            if (s < 0.0f)
                continue;

            if (lf < minlf)
            {
                minlf = lf;
                minid = i;
                mins = s;
            }
        }

        if (minid > 0)
        {
            nmag = minlf - Mathf.Sqrt(mins);

            // Assign new position if got appropriate magnitude
            if (nmag * nmag < originalDelta.sqrMagnitude)
            {
                return norm * nmag;
            }
        }

        return originalDelta;
    }

    // Advance simulation 1 step for ball id
    private bool stepOneBall(int id, GameObject[] balls, Vector3[] balls_P, Vector3[] balls_V, Vector3[] balls_W, uint sn_pocketed, bool[] moved)
    {
        GameObject g_ball_current = balls[id];

        bool isBallMoving = false;

        // no point updating velocity if ball isn't moving
        if (balls_V[id] != Vector3.zero || balls_W[id] != Vector3.zero)
        {
            isBallMoving = updateVelocity(id, g_ball_current, balls_V, balls_W);
        }

        moved[id] |= isBallMoving;

        // check for collisions. a non-moving ball might be collided by a moving one
        uint ball_bit = 0x1U << id;
        for (int i = id + 1; i < 16; i++)
        {
            ball_bit <<= 1;

            if ((ball_bit & sn_pocketed) != 0U)
                continue;

            Vector3 delta = balls_P[i] - balls_P[id];
            float dist = delta.sqrMagnitude;

            if (dist < k_BALL_DIAMETREPESQ)
            {
                dist = Mathf.Sqrt(dist);
                Vector3 normal = delta / dist;

                // static resolution
                Vector3 res = (k_BALL_DIAMETRE - dist) * normal;
                balls_P[i] += res;
                balls_P[id] -= res;
                moved[i] = true;
                moved[id] = true;

                Vector3 velocityDelta = balls_V[id] - balls_V[i];

                float dot = Vector3.Dot(velocityDelta, normal);

                // Dynamic resolution (Cr is assumed to be (1)+1.0)

                Vector3 reflection = normal * dot;
                balls_V[id] -= reflection;
                balls_V[i] += reflection;

                // Prevent sound spam if it happens
                if (balls_V[id].sqrMagnitude > 0 && balls_V[i].sqrMagnitude > 0)
                {
                    g_ball_current.GetComponent<AudioSource>().PlayOneShot(hitSounds[id % 3], Mathf.Clamp01(reflection.magnitude));
                }

                table._TriggerCollision(id, i);
            }
        }

        return isBallMoving;
    }

    private bool updateVelocity(int id, GameObject ball, Vector3[] balls_V, Vector3[] balls_W)
    {
        bool ballMoving = false;

        // Since v1.5.0
        Vector3 V = balls_V[id];
        Vector3 W = balls_W[id];
        Vector3 cv;

        // Equations derived from: http://citeseerx.ist.psu.edu/viewdoc/download?doi=10.1.1.89.4627&rep=rep1&type=pdf
        // 
        // R: Contact location with ball and floor aka: (0,-r,0)
        // µₛ: Slipping friction coefficient
        // µᵣ: Rolling friction coefficient
        // i: Up vector aka: (0,1,0)
        // g: Planet Earth's gravitation acceleration ( 9.80665 )
        // 
        // Relative contact velocity (marlow):
        //   c = v + R✕ω
        //
        // Ball is classified as 'rolling' or 'slipping'. Rolling is when the relative velocity is none and the ball is
        // said to be in pure rolling motion
        //
        // When ball is classified as rolling:
        //   Δv = -µᵣ∙g∙Δt∙(v/|v|)
        //
        // Angular momentum can therefore be derived as:
        //   ωₓ = -vᵤ/R
        //   ωᵧ =  0
        //   ωᵤ =  vₓ/R
        //
        // In the slipping state:
        //   Δω = ((-5∙µₛ∙g)/(2/R))∙Δt∙i✕(c/|c|)
        //   Δv = -µₛ∙g∙Δt(c/|c|)

        // Relative contact velocity of ball and table
        cv = V + Vector3.Cross(k_CONTACT_POINT, W);
        float cvMagnitude = cv.magnitude;

        // Rolling is achieved when cv's length is approaching 0
        // The epsilon is quite high here because of the fairly large timestep we are working with
        if (cvMagnitude <= 0.1f)
        {
            //V += -k_F_ROLL * k_GRAVITY * k_FIXED_TIME_STEP * V.normalized;
            // (baked):
            V += -0.00122583125f * V.normalized;

            // Calculate rolling angular velocity
            W.x = -V.z * k_BALL_1OR;

            if (0.3f > Mathf.Abs(W.y))
            {
                W.y = 0.0f;
            }
            else
            {
                W.y -= Mathf.Sign(W.y) * 0.3f;
            }

            W.z = V.x * k_BALL_1OR;

            // Stopping scenario
            if (V.sqrMagnitude < 0.0001f && W.sqrMagnitude < 0.04f)
            {
                W = Vector3.zero;
                V = Vector3.zero;
            }
            else
            {
                ballMoving = true;
            }
        }
        else // Slipping
        {
            Vector3 nv = cv / cvMagnitude;

            // Angular slipping friction
            //W += ((-5.0f * k_F_SLIDE * k_GRAVITY)/(2.0f * 0.03f)) * k_FIXED_TIME_STEP * Vector3.Cross( Vector3.up, nv );
            // (baked):
            W += -2.04305208f * Vector3.Cross(Vector3.up, nv);

            //V += -k_F_SLIDE * k_GRAVITY * k_FIXED_TIME_STEP * nv;
            // (baked):
            V += -0.024516625f * nv;

            ballMoving = true;
        }

        balls_W[id] = W;
        balls_V[id] = V;

        ball.transform.Rotate(this.transform.TransformDirection(W.normalized), W.magnitude * k_FIXED_TIME_STEP * -Mathf.Rad2Deg, Space.World);
        // g_ball_current.GetComponent<AudioSource>().Play();

        return ballMoving;
    }

    // Cue input tracking

    Vector3 cue_lpos;
    Vector3 cue_llpos;
    public Vector3 cue_vdir;
    Vector3 cue_shotdir;
    float cue_fdir;

#if HT_QUEST
#else
    [HideInInspector]
    public Vector3 dkTargetPos;            // Target for desktop aiming
#endif


    [NonSerialized] public bool outIsTouching;
    public void _IsCueBallTouching()
    {
        outIsTouching = isCueBallTouching();
    }

    private bool isCueBallTouching()
    {
        if (table.is8Ball) // 8 ball
        {
            // Check all
            for (int i = 1; i < 16; i++)
            {
                if ((balls_P[0] - balls_P[i]).sqrMagnitude < k_BALL_DSQR)
                {
                    return true;
                }
            }
        }
        else if (table.is9Ball) // 9
        {
            // Only check to 9 ball
            for (int i = 1; i <= 9; i++)
            {
                if ((balls_P[0] - balls_P[i]).sqrMagnitude < k_BALL_DSQR)
                {
                    return true;
                }
            }
        }
        else // 4
        {
            if ((balls_P[0] - balls_P[13]).sqrMagnitude < k_BALL_DSQR)
            {
                return true;
            }
            if ((balls_P[0] - balls_P[14]).sqrMagnitude < k_BALL_DSQR)
            {
                return true;
            }
            if ((balls_P[0] - balls_P[15]).sqrMagnitude < k_BALL_DSQR)
            {
                return true;
            }
        }

        return false;
    }

    const float k_SINA = 0.28078832987f;
    const float k_SINA2 = 0.07884208619f;
    const float k_COSA = 0.95976971915f;
    const float k_COSA2 = 0.92115791379f;
    const float k_EP1 = 1.79f;
    const float k_A = 21.875f;
    const float k_B = 6.25f;
    const float k_F = 1.72909790282f;

    // Apply cushion bounce
    void _phy_bounce_cushion(int id, Vector3 N, Vector3[] balls_V, Vector3[] balls_W)
    {
        // Mathematical expressions derived from: https://billiards.colostate.edu/physics_articles/Mathavan_IMechE_2010.pdf
        //
        // (Note): subscript gamma, u, are used in replacement of Y and Z in these expressions because
        // unicode does not have them.
        //
        // f = 2/7
        // f₁ = 5/7
        // 
        // Velocity delta:
        //   Δvₓ = −vₓ∙( f∙sin²θ + (1+e)∙cos²θ ) − Rωᵤ∙sinθ
        //   Δvᵧ = 0
        //   Δvᵤ = f₁∙vᵤ + fR( ωₓ∙sinθ - ωᵧ∙cosθ ) - vᵤ
        //
        // Aux:
        //   Sₓ = vₓ∙sinθ - vᵧ∙cosθ+ωᵤ
        //   Sᵧ = 0
        //   Sᵤ = -vᵤ - ωᵧ∙cosθ + ωₓ∙cosθ
        //   
        //   k = (5∙Sᵤ) / ( 2∙mRA )
        //   c = vₓ∙cosθ - vᵧ∙cosθ
        //
        // Angular delta:
        //   ωₓ = k∙sinθ
        //   ωᵧ = k∙cosθ
        //   ωᵤ = (5/(2m))∙(-Sₓ / A + ((sinθ∙c∙(e+1)) / B)∙(cosθ - sinθ))
        //
        // These expressions are in the reference frame of the cushion, so V and ω inputs need to be rotated

        // Reject bounce if velocity is going the same way as normal
        // this state means we tunneled, but it happens only on the corner
        // vertexes
        Vector3 source_v = balls_V[id];
        if (Vector3.Dot(source_v, N) > 0.0f)
        {
            return;
        }

        // Rotate V, W to be in the reference frame of cushion
        Quaternion rq = Quaternion.AngleAxis(Mathf.Atan2(-N.z, -N.x) * Mathf.Rad2Deg, Vector3.up);
        Quaternion rb = Quaternion.Inverse(rq);
        Vector3 V = rq * source_v;
        Vector3 W = rq * balls_W[id];

        Vector3 V1;
        Vector3 W1;
        float k, c, s_x, s_z;

        //V1.x = -V.x * ((2.0f/7.0f) * k_SINA2 + k_EP1 * k_COSA2) - (2.0f/7.0f) * k_BALL_PL_X * W.z * k_SINA;
        //V1.z = (5.0f/7.0f)*V.z + (2.0f/7.0f) * k_BALL_PL_X * (W.x * k_SINA - W.y * k_COSA) - V.z;
        //V1.y = 0.0f; 
        // (baked):
        V1.x = -V.x * k_F - 0.00240675711f * W.z;
        V1.z = 0.71428571428f * V.z + 0.00857142857f * (W.x * k_SINA - W.y * k_COSA) - V.z;
        V1.y = 0.0f;

        // s_x = V.x * k_SINA - V.y * k_COSA + W.z;
        // (baked): y component not used:
        s_x = V.x * k_SINA + W.z;
        s_z = -V.z - W.y * k_COSA + W.x * k_SINA;

        // k = (5.0f * s_z) / ( 2 * k_BALL_MASS * k_A ); 
        // (baked):
        k = s_z * 0.71428571428f;

        // c = V.x * k_COSA - V.y * k_COSA;
        // (baked): y component not used
        c = V.x * k_COSA;

        W1.x = k * k_SINA;

        //W1.z = (5.0f / (2.0f * k_BALL_MASS)) * (-s_x / k_A + ((k_SINA * c * k_EP1) / k_B) * (k_COSA - k_SINA));
        // (baked):
        W1.z = 15.625f * (-s_x * 0.04571428571f + c * 0.0546021744f);
        W1.y = k * k_COSA;

        // Unrotate result
        balls_V[id] += rb * V1;
        balls_W[id] += rb * W1;
    }


    private float k_MINOR_REGION_CONST;

    Vector3 k_vA = new Vector3();
    Vector3 k_vB = new Vector3();
    Vector3 k_vC = new Vector3();
    Vector3 k_vD = new Vector3();

    Vector3 k_vX = new Vector3();
    Vector3 k_vY = new Vector3();
    Vector3 k_vZ = new Vector3();
    Vector3 k_vW = new Vector3();

    Vector3 k_pK = new Vector3();
    Vector3 k_pL = new Vector3();
    Vector3 k_pM = new Vector3();
    Vector3 k_pN = new Vector3();
    public Vector3 k_pO = new Vector3();
    Vector3 k_pP = new Vector3();
    Vector3 k_pQ = new Vector3();
    public Vector3 k_pR = new Vector3();
    Vector3 k_pT = new Vector3();
    Vector3 k_pS = new Vector3();
    Vector3 k_pU = new Vector3();
    Vector3 k_pV = new Vector3();

    Vector3 k_vA_vD = new Vector3();
    Vector3 k_vA_vD_normal = new Vector3();

    Vector3 k_vB_vY = new Vector3();
    Vector3 k_vB_vY_normal = new Vector3();

    Vector3 k_vC_vZ_normal = new Vector3();

    Vector3 k_vA_vB_normal = new Vector3(0.0f, 0.0f, -1.0f);
    Vector3 k_vC_vW_normal = new Vector3(-1.0f, 0.0f, 0.0f);

    Vector3 _sign_pos = new Vector3(0.0f, 1.0f, 0.0f);

    public void _InitConstants()
    {
        k_TABLE_WIDTH = table.k_TABLE_WIDTH;
        k_TABLE_HEIGHT = table.k_TABLE_HEIGHT;
        k_POCKET_WIDTH_CORNER = table.k_POCKET_WIDTH_CORNER;
        k_POCKET_HEIGHT_CORNER = table.k_POCKET_HEIGHT_CORNER;
        k_CUSHION_RADIUS = table.k_CUSHION_RADIUS;

        Collider[] collider = table.GetComponentsInChildren<Collider>();
        for (int i = 0; i < collider.Length; i++)
        {
            collider[i].enabled = true;
        }

        // Handy values
        k_MINOR_REGION_CONST = table.k_TABLE_WIDTH - table.k_TABLE_HEIGHT;

        // Major source vertices
        k_vA.x = table.k_POCKET_RADIUS_SIDE;
        k_vA.z = table.k_TABLE_HEIGHT;

        k_vB.x = table.k_TABLE_WIDTH - table.k_POCKET_WIDTH_CORNER;
        k_vB.z = table.k_TABLE_HEIGHT;

        k_vC.x = table.k_TABLE_WIDTH;
        k_vC.z = table.k_TABLE_HEIGHT - table.k_POCKET_HEIGHT_CORNER;

        k_vD.x = k_vA.x - 0.016f;
        k_vD.z = k_vA.z + 0.060f;

        // Aux points
        k_vX = k_vD + Vector3.forward;
        k_vW = k_vC;
        k_vW.z = 0.0f;

        k_vY = k_vB;
        k_vY.x += 1.0f;
        k_vY.z += 1.0f;

        k_vZ = k_vC;
        k_vZ.x += 1.0f;
        k_vZ.z += 1.0f;

        // Normals
        k_vA_vD = k_vD - k_vA;
        k_vA_vD = k_vA_vD.normalized;
        k_vA_vD_normal.x = -k_vA_vD.z;
        k_vA_vD_normal.z = k_vA_vD.x;

        k_vB_vY = k_vB - k_vY;
        k_vB_vY = k_vB_vY.normalized;
        k_vB_vY_normal.x = -k_vB_vY.z;
        k_vB_vY_normal.z = k_vB_vY.x;

        k_vC_vZ_normal = -k_vB_vY_normal;

        // Minkowski difference
        k_pN = k_vA;
        k_pN.z -= table.k_CUSHION_RADIUS;

        k_pM = k_vA + k_vA_vD_normal * table.k_CUSHION_RADIUS;
        k_pL = k_vD + k_vA_vD_normal * table.k_CUSHION_RADIUS;

        k_pK = k_vD;
        k_pK.x -= table.k_CUSHION_RADIUS;

        k_pO = k_vB;
        k_pO.z -= table.k_CUSHION_RADIUS;
        k_pP = k_vB + k_vB_vY_normal * table.k_CUSHION_RADIUS;
        k_pQ = k_vC + k_vC_vZ_normal * table.k_CUSHION_RADIUS;

        k_pR = k_vC;
        k_pR.x -= table.k_CUSHION_RADIUS;

        k_pT = k_vX;
        k_pT.x -= table.k_CUSHION_RADIUS;

        k_pS = k_vW;
        k_pS.x -= table.k_CUSHION_RADIUS;

        k_pU = k_vY + k_vB_vY_normal * table.k_CUSHION_RADIUS;
        k_pV = k_vZ + k_vC_vZ_normal * table.k_CUSHION_RADIUS;

        k_pS = k_vW;
        k_pS.x -= table.k_CUSHION_RADIUS;

        // others
        k_INNER_RADIUS = table.k_INNER_RADIUS_CORNER;
        k_INNER_RADIUS_SQ = k_INNER_RADIUS * k_INNER_RADIUS;
        k_vE = table.k_vE;
        k_vF = table.k_vF;
    }

    // Check pocket condition
    void _phy_ball_pockets(int id)
    {
        Vector3 A = balls_P[id];
        Vector3 absA = new Vector3(Mathf.Abs(A.x), A.y, Mathf.Abs(A.z));

        if ((absA - k_vE).sqrMagnitude < k_INNER_RADIUS_SQ)
        {
            table._TriggerPocketBall(id, false);
            return;
        }

        if ((absA - k_vF).sqrMagnitude < k_INNER_RADIUS_SQ)
        {
            table._TriggerPocketBall(id, false);
            return;
        }

        if (absA.z > k_vF.z)
        {
            table._TriggerPocketBall(id, true);
            return;
        }

        if (absA.z > -absA.x + k_vE.x + k_vE.z)
        {
            table._TriggerPocketBall(id, true);
            return;
        }
    }

    // Pocketless table
    void _phy_ball_table_carom(int id)
    {
        float zz, zx;
        Vector3 A = balls_P[id];

        // Setup major regions
        zx = Mathf.Sign(A.x);
        zz = Mathf.Sign(A.z);

        if (A.x * zx > k_pR.x)
        {
            balls_P[id].x = k_pR.x * zx;
            _phy_bounce_cushion(id, Vector3.left * zx, balls_V, balls_W);
        }

        if (A.z * zz > k_pO.z)
        {
            balls_P[id].z = k_pO.z * zz;
            _phy_bounce_cushion(id, Vector3.back * zz, balls_V, balls_W);
        }
    }

    void _phy_ball_table_std(int id)
    {
        Vector3 A, N, _V, V, a_to_v;
        float dot;

        A = balls_P[id];

        _sign_pos.x = Mathf.Sign(A.x);
        _sign_pos.z = Mathf.Sign(A.z);

        A = Vector3.Scale(A, _sign_pos);

#if HT8B_DRAW_REGIONS
   Debug.DrawLine( k_vA, k_vB, Color.white );
   Debug.DrawLine( k_vD, k_vA, Color.white );
   Debug.DrawLine( k_vB, k_vY, Color.white );
   Debug.DrawLine( k_vD, k_vX, Color.white );
   Debug.DrawLine( k_vC, k_vW, Color.white );
   Debug.DrawLine( k_vC, k_vZ, Color.white );

   r_k_CUSHION_RADIUS = k_CUSHION_RADIUS-k_BALL_RADIUS;

   _phy_table_init();

   Debug.DrawLine( k_pT, k_pK, Color.yellow );
   Debug.DrawLine( k_pK, k_pL, Color.yellow );
   Debug.DrawLine( k_pL, k_pM, Color.yellow );
   Debug.DrawLine( k_pM, k_pN, Color.yellow );
   Debug.DrawLine( k_pN, k_pO, Color.yellow );
   Debug.DrawLine( k_pO, k_pP, Color.yellow );
   Debug.DrawLine( k_pP, k_pU, Color.yellow );

   Debug.DrawLine( k_pV, k_pQ, Color.yellow );
   Debug.DrawLine( k_pQ, k_pR, Color.yellow );
   Debug.DrawLine( k_pR, k_pS, Color.yellow );

   r_k_CUSHION_RADIUS = k_CUSHION_RADIUS;
   _phy_table_init();
#endif

        if (A.x > k_vA.x) // Major Regions
        {
            if (A.x > A.z + k_MINOR_REGION_CONST) // Minor B
            {
                if (A.z < k_TABLE_HEIGHT - k_POCKET_HEIGHT_CORNER)
                {
                    // Region H
#if HT8B_DRAW_REGIONS
            Debug.DrawLine( new Vector3( 0.0f, 0.0f, 0.0f ), new Vector3( k_TABLE_WIDTH, 0.0f, 0.0f ), Color.red );
            Debug.DrawLine( k_vC, k_vC + k_vC_vW_normal, Color.red );
#endif
                    if (A.x > k_TABLE_WIDTH - k_CUSHION_RADIUS)
                    {
                        // Static resolution
                        A.x = k_TABLE_WIDTH - k_CUSHION_RADIUS;

                        // Dynamic
                        _phy_bounce_cushion(id, Vector3.Scale(k_vC_vW_normal, _sign_pos), balls_V, balls_W);
                    }
                }
                else
                {
                    a_to_v = A - k_vC;

                    if (Vector3.Dot(a_to_v, k_vB_vY) > 0.0f)
                    {
                        // Region I ( VORONI )
#if HT8B_DRAW_REGIONS
               Debug.DrawLine( k_vC, k_pR, Color.green );
               Debug.DrawLine( k_vC, k_pQ, Color.green );
#endif
                        if (a_to_v.magnitude < k_CUSHION_RADIUS)
                        {
                            // Static resolution
                            N = a_to_v.normalized;
                            A = k_vC + N * k_CUSHION_RADIUS;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos), balls_V, balls_W);
                        }
                    }
                    else
                    {
                        // Region J
#if HT8B_DRAW_REGIONS
               Debug.DrawLine( k_vC, k_vB, Color.red );
               Debug.DrawLine( k_pQ, k_pV, Color.blue );
#endif
                        a_to_v = A - k_pQ;

                        if (Vector3.Dot(k_vC_vZ_normal, a_to_v) < 0.0f)
                        {
                            // Static resolution
                            dot = Vector3.Dot(a_to_v, k_vB_vY);
                            A = k_pQ + dot * k_vB_vY;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(k_vC_vZ_normal, _sign_pos), balls_V, balls_W);
                        }
                    }
                }
            }
            else // Minor A
            {
                if (A.x < k_vB.x)
                {
                    // Region A
#if HT8B_DRAW_REGIONS
            Debug.DrawLine( k_vA, k_vA + k_vA_vB_normal, Color.red );
            Debug.DrawLine( k_vB, k_vB + k_vA_vB_normal, Color.red );
#endif
                    if (A.z > k_pN.z)
                    {
                        // Velocity based A->C delegation ( scuffed CCD )
                        a_to_v = A - k_vA;
                        _V = Vector3.Scale(balls_V[id], _sign_pos);
                        V.x = -_V.z;
                        V.y = 0.0f;
                        V.z = _V.x;

                        if (A.z > k_vA.z)
                        {
                            if (Vector3.Dot(V, a_to_v) > 0.0f)
                            {
                                // Region C ( Delegated )
                                a_to_v = A - k_pL;

                                // Static resolution
                                dot = Vector3.Dot(a_to_v, k_vA_vD);
                                A = k_pL + dot * k_vA_vD;

                                // Dynamic
                                _phy_bounce_cushion(id, Vector3.Scale(k_vA_vD_normal, _sign_pos), balls_V, balls_W);
                            }
                            else
                            {
                                // Static resolution
                                A.z = k_pN.z;

                                // Dynamic
                                _phy_bounce_cushion(id, Vector3.Scale(k_vA_vB_normal, _sign_pos), balls_V, balls_W);
                            }
                        }
                        else
                        {
                            // Static resolution
                            A.z = k_pN.z;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(k_vA_vB_normal, _sign_pos), balls_V, balls_W);
                        }
                    }
                }
                else
                {
                    a_to_v = A - k_vB;

                    if (Vector3.Dot(a_to_v, k_vB_vY) > 0.0f)
                    {
                        // Region F ( VERONI )
#if HT8B_DRAW_REGIONS
               Debug.DrawLine( k_vB, k_pO, Color.green );
               Debug.DrawLine( k_vB, k_pP, Color.green );
#endif
                        if (a_to_v.magnitude < k_CUSHION_RADIUS)
                        {
                            // Static resolution
                            N = a_to_v.normalized;
                            A = k_vB + N * k_CUSHION_RADIUS;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos), balls_V, balls_W);
                        }
                    }
                    else
                    {
                        // Region G
#if HT8B_DRAW_REGIONS
               Debug.DrawLine( k_vB, k_vC, Color.red );
               Debug.DrawLine( k_pP, k_pU, Color.blue );
#endif
                        a_to_v = A - k_pP;

                        if (Vector3.Dot(k_vB_vY_normal, a_to_v) < 0.0f)
                        {
                            // Static resolution
                            dot = Vector3.Dot(a_to_v, k_vB_vY);
                            A = k_pP + dot * k_vB_vY;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(k_vB_vY_normal, _sign_pos), balls_V, balls_W);
                        }
                    }
                }
            }
        }
        else
        {
            a_to_v = A - k_vA;

            if (Vector3.Dot(a_to_v, k_vA_vD) > 0.0f)
            {
                a_to_v = A - k_vD;

                if (Vector3.Dot(a_to_v, k_vA_vD) > 0.0f)
                {
                    if (A.z > k_pK.z)
                    {
                        // Region E
#if HT8B_DRAW_REGIONS
               Debug.DrawLine( k_vD, k_vD + k_vC_vW_normal, Color.red );
#endif
                        if (A.x > k_pK.x)
                        {
                            // Static resolution
                            A.x = k_pK.x;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(k_vC_vW_normal, _sign_pos), balls_V, balls_W);
                        }
                    }
                    else
                    {
                        // Region D ( VORONI )
#if HT8B_DRAW_REGIONS
               Debug.DrawLine( k_vD, k_vD + k_vC_vW_normal, Color.green );
               Debug.DrawLine( k_vD, k_vD + k_vA_vD_normal, Color.green );
#endif
                        if (a_to_v.magnitude < k_CUSHION_RADIUS)
                        {
                            // Static resolution
                            N = a_to_v.normalized;
                            A = k_vD + N * k_CUSHION_RADIUS;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos), balls_V, balls_W);
                        }
                    }
                }
                else
                {
                    // Region C
#if HT8B_DRAW_REGIONS
            Debug.DrawLine( k_vA, k_vA + k_vA_vD_normal, Color.red );
            Debug.DrawLine( k_vD, k_vD + k_vA_vD_normal, Color.red );
            Debug.DrawLine( k_pL, k_pM, Color.blue );
#endif
                    a_to_v = A - k_pL;

                    if (Vector3.Dot(k_vA_vD_normal, a_to_v) < 0.0f)
                    {
                        // Static resolution
                        dot = Vector3.Dot(a_to_v, k_vA_vD);
                        A = k_pL + dot * k_vA_vD;

                        // Dynamic
                        _phy_bounce_cushion(id, Vector3.Scale(k_vA_vD_normal, _sign_pos), balls_V, balls_W);
                    }
                }
            }
            else
            {
                // Region B ( VORONI )
#if HT8B_DRAW_REGIONS
         Debug.DrawLine( k_vA, k_vA + k_vA_vB_normal, Color.green );
         Debug.DrawLine( k_vA, k_vA + k_vA_vD_normal, Color.green );
#endif
                if (a_to_v.magnitude < k_CUSHION_RADIUS)
                {
                    // Static resolution
                    N = a_to_v.normalized;
                    A = k_vA + N * k_CUSHION_RADIUS;

                    // Dynamic
                    _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos), balls_V, balls_W);
                }
            }
        }

        balls_P[id] = Vector3.Scale(A, _sign_pos);
    }

    public Vector3 RaySphere_output;
    bool _phy_ray_sphere(Vector3 start, Vector3 dir, Vector3 sphere)
    {
        Vector3 nrm = dir.normalized;
        Vector3 h = sphere - start;
        float lf = Vector3.Dot(nrm, h);
        float s = k_BALL_RSQR - Vector3.Dot(h, h) + lf * lf;

        if (s < 0.0f) return false;

        s = Mathf.Sqrt(s);

        if (lf < s)
        {
            if (lf + s >= 0)
            {
                s = -s;
            }
            else
            {
                return false;
            }
        }

        RaySphere_output = start + nrm * (lf - s);
        return true;
    }

    public float multiplier;

    public float inV0;
    public void _ApplyPhysics()
    {
        applyPhysics(inV0);
    }

    private void applyPhysics(float V0)
    {
        Vector3 cueball_pos = balls_P[0];
        Vector3 r = (RaySphere_output - cueball_pos) * k_BALL_1OR;

        // Clamp velocity input to 16 m/s ( very strong break speed )
        balls_V[0] = cue_shotdir * Mathf.Min(V0, 16.0f) * Vector3.Dot(r, -cue_shotdir);

        // Angular velocity: L=r(normalized)×p 
        Vector3 p = cue_vdir * V0;
        balls_W[0] = Vector3.Cross(r, p) * -25.0f;
    }
}
