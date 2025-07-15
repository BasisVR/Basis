// #define HT8B_DRAW_REGIONS
using System;
using UnityEngine;


public class StandardPhysicsManager : MonoBehaviour
{
    public string PHYSICSNAME = "<color=#678AC2>Standard</color>";
#if HT_QUEST
   private  float k_MAX_DELTA =  k_FIXED_TIME_STEP * 2; // Private Const Float 0.05f max time to process per frame on quest (~4)
#else
    private float k_MAX_DELTA = k_FIXED_TIME_STEP * 6; // Private Cont Float 0.1f max time to process per frame on pc (~8)
#endif
    private const float k_FIXED_TIME_STEP = 1 / 80f;      // time step in seconds per iteration
    private float k_BALL_DSQRPE = 0.003598f;            // ball diameter squared plus epsilon // this is actually minus epsilon?
    private float k_BALL_DIAMETRESQ = 0.0036f;          // width of ball
    private float k_BALL_DIAMETRE = 0.06f;              // width of ball
    private float k_BALL_RADIUS = 0.03f;
    private float k_BALL_1OR = 33.3333333333f;          // 1 over ball radius
    private const float k_GRAVITY = 9.80665f;           // Earths gravitational acceleration
    private float k_BALL_DSQR = 0.0036f;                // ball diameter squared
    private float k_BALL_MASS = 0.16f;                  // Weight of ball in kg
    private float k_BALL_RSQR = 0.0009f;                // ball radius squared
    const float k_F_SLIDE = 0.2f;                       // Friction coefficient of sliding (Ball-Table).
    const float k_F_ROLL = 0.01f;                       // Friction coefficient of rolling (Ball-table)
    const float k_F_SLIDE_C = 0.14f;                    //Friction coefficient of sliding Cushion (Ball-Cushion)
    const float K_BOUNCE_FACTOR = 0.5f;                 // COR Ball-Slate

    private Color markerColorYes = new Color(0.0f, 1.0f, 0.0f, 1.0f);
    private Color markerColorNo = new Color(1.0f, 0.0f, 0.0f, 1.0f);

    private Vector3 k_CONTACT_POINT = new Vector3(0.0f, -0.03f, 0.0f);

    [SerializeField] AudioClip[] hitSounds;
    Transform transform_Surface;

    private AudioSource audioSource;
    private float initialVolume;    // Ball-Slate
    private float initialPitch;

    private BilliardsModule table;

    private float accumulatedTime;
    private float lastTimestamp;
    float pocketedTime = 0;

    private GameObject[] balls;
    private Vector3[] balls_P; // Displacement Vector
    private Vector3[] balls_V; // Velocity Vector
    private Vector3[] balls_W; // Angular Velocity Vector
    private float k_INNER_RADIUS_CORNER;
    private float k_INNER_RADIUS_CORNER2;
    private float k_INNER_RADIUS_CORNER_SQ;
    private float k_INNER_RADIUS_CORNER_SQ2;
    private float k_INNER_RADIUS_SIDE;
    private float k_INNER_RADIUS_SIDE2;
    private float k_INNER_RADIUS_SIDE_SQ;
    private float k_INNER_RADIUS_SIDE_SQ2;

    float k_TABLE_WIDTH;
    float k_TABLE_HEIGHT;
    float k_POCKET_WIDTH_CORNER;
    float k_POCKET_HEIGHT_CORNER;
    float k_POCKET_RADIUS_SIDE;
    float k_POCKET_DEPTH_SIDE;
    float k_CUSHION_RADIUS;
    float k_RAIL_DEPTH_WIDTH;
    float k_RAIL_DEPTH_HEIGHT;
    private Vector3 k_vE;
    private Vector3 k_vF;
    private Vector3 k_vE2;
    private Vector3 k_vF2;
    bool furthest_vE;
    bool furthest_vF;
    bool closest_vE;
    bool closest_vF;
    [Tooltip("Clamp the cue-ball collision point to center + Radius*this (Limits max applyable spin, as miss-cue isn't possible)")]
    public float CueMaxHitRadius = 0.9f;

    private bool jumpShotFlewOver, cueBallHasCollided;

    private void Start()
    {
        //Temporary Organization variables that will need to be stored and saved ONCE down to Cushion Physics, using Han 2005 Model.
        //here just for reference

        /*
       float rvw                            // the ball state, "r" is Ball Displacement (ball_P) / "v" is velocity (ball_V) / "w" is angular velocity (ball_W)
       float R = k_BALL_RADIUS;
       float m = k_BALL_MASS;
       float h = 7f * k_BALL_RADIUS / 5f;   // the height of the cushion
       float s =                            // the motion state of the ball.
       float mu =                           // The coefficient of friction, If ball motion state is sliding, assume coefficient of sliding friction. If rolling, assume coefficient of rolling friction. If spinning, assume coefficient of spinning friction
       float u_s = k_F_SLIDE;
       float u_sp                           // Spiining Coefficient of Friction
       float u_r = k_F_ROLL;                // rolling coefficicient of firction.
       float e_c = (1 + e);                 // Cushion Coefficient of restitution.
       float f_c = k_F_SLIDE_C;             // cushion coefficient of friction
       float g = k_GRAVITY;                 // Acceleration due to gravity felt by a ball.
       */


        //audioSource = GetComponent<AudioSource>();
        //audioSource.volume = initialVolume;


    }
    [NonSerialized] public BilliardsModule table_;
    public void _Init()
    {
        table = table_;
        transform_Surface = table.tableSurface;

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
                table.markerObj.transform.position = balls[0].transform.position + new Vector3(0, k_BALL_RADIUS, 0);  // Ensures the Market stays above the ball no matter the size or Scale
                table.markerObj.transform.localScale = Vector3.one * .3f;
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

                    float V0 = Mathf.Min(horizontal_force.magnitude / Time.fixedDeltaTime, 999.0f);
                    applyPhysics(V0);

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
                    Vector3 q = transform_Surface.InverseTransformDirection(cuetip.transform.forward);  // direction of cue in surface space
                    Vector3 o = balls_P[0];

                    Vector3 j = -Vector3.ProjectOnPlane(q, transform_Surface.up); // project cue direction onto table surface, gives us j
                    Vector3 k = transform_Surface.up;
                    Vector3 i = Vector3.Cross(j, k);

                    Plane jkPlane = new Plane(i, o);

                    Vector3 Q = RaySphere_output;
                    // Clamp the increase in spin from hitting the ball further from the center by moving the hit point towards the center
                    Vector3 Qflat = Vector3.ProjectOnPlane(Q - o, q);
                    float distFromCenter = Qflat.magnitude / k_BALL_RADIUS;
                    if (distFromCenter > CueMaxHitRadius)
                    {
                        _phy_ray_sphere((o + Qflat.normalized * k_BALL_RADIUS * CueMaxHitRadius) - q * k_BALL_DIAMETRE, q, o);
                        Q = RaySphere_output;
                    }
                    table.devhit.transform.localPosition = Q;

                    float a = jkPlane.GetDistanceToPoint(Q);
                    float b = Q.y - o.y;
                    float c = Mathf.Sqrt(Mathf.Pow(k_BALL_RADIUS, 2) - Mathf.Pow(a, 2) - Mathf.Pow(b, 2));

                    float adj = Mathf.Sqrt(Mathf.Pow(q.x, 2) + Mathf.Pow(q.z, 2));
                    float opp = q.y;
                    float theta = -Mathf.Atan(opp / adj);

                    float cosTheta = Mathf.Cos(theta);
                    float sinTheta = Mathf.Sin(theta);

                    float V0 = 1f; // probably fine, right?
                    float k_CUE_MASS = 0.5f; // kg
                    float F = 2 * k_BALL_MASS * V0 / (1 + k_BALL_MASS / k_CUE_MASS + 5 / (2 * k_BALL_RADIUS) * (Mathf.Pow(a, 2) + Mathf.Pow(b, 2) * Mathf.Pow(cosTheta, 2) + Mathf.Pow(c, 2) * Mathf.Pow(sinTheta, 2) - 2 * b * c * cosTheta * sinTheta)); // F = Magnitude


                    float I = 2f / 5f * k_BALL_MASS * Mathf.Pow(k_BALL_RADIUS, 2);
                    Vector3 v = new Vector3(0, -F / k_BALL_MASS * cosTheta, -F / k_BALL_MASS * sinTheta);
                    Vector3 w = 1 / I * new Vector3(-c * F * sinTheta + b * F * cosTheta, a * F * sinTheta, -a * F * cosTheta);

                    // the paper is inconsistent here. either w.x is inverted (i.e. the i axis points right instead of left) or b is inverted (which means F is wrong too)
                    // for my sanity I'm going to assume the former
                    w.x = -w.x;

                    float m_e = 0.02f; // float m_e = Mathf.Sqrt(k_CUE_MASS) <- Consider this change when playing with Cue Mass to fix Guideline prediction of Squirt, needs a small revision.

                    // https://billiards.colostate.edu/physics_articles/Alciatore_pool_physics_article.pdf
                    float alpha = -Mathf.Atan(
                       (5f / 2f * a / k_BALL_RADIUS * Mathf.Sqrt(1f - Mathf.Pow(a / k_BALL_RADIUS, 2))) /
                       (1 + k_BALL_MASS / m_e + 5f / 2f * (1f - Mathf.Pow(a / k_BALL_RADIUS, 2)))
                    ) * 180 / Mathf.PI;

                    // rewrite to the axis we expect
                    v = new Vector3(-v.x, v.z, -v.y);

                    // translate
                    Quaternion r = Quaternion.FromToRotation(Vector3.back, j);
                    v = r * v;
                    w = r * w;

                    // apply squirt
                    Vector3 before = v;
                    v = Quaternion.AngleAxis(alpha, transform_Surface.up) * v;
                    Vector3 after = v;

                    cue_shotdir = v;

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

            ballsMoving |= stepOneBall(0, sn_pocketed, moved);
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

                ballsMoving |= stepOneBall(i, sn_pocketed, moved);
            }
        }
        table._EndPerf(table.PERF_PHYSICS_BALL);

        // Check if simulation has settled
        if (!ballsMoving)
        {
            if (Time.time - pocketedTime > 1f)
            {
                table._TriggerSimulationEnded(false);
                return;
            }
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
                pocketedTime = Time.time;
                table._Log("out of bounds! " + balls_P[0].ToString());
                outOfBounds = true;
            }
        }

        if (table.is4Ball) return;

        if (table.isSnooker6Red)
        {
            if (!cueBallHasCollided && balls_P[0].y > 0)
            {
                ball_bit = 0x1U;
                Vector2 cueBallPos = new Vector2(balls_P[0].x, balls_P[0].z);
                bool flewOverThisFrame = false;
                for (int i = 1; i < 16; i++)
                {
                    ball_bit <<= 1;
                    if ((ball_bit & sn_pocketed) > 0U) continue; //skip checking pocketed balls
                    Vector2 compareBallPos = new Vector2(balls_P[i].x, balls_P[i].z);
                    if (Vector2.Distance(cueBallPos, compareBallPos) < k_BALL_DIAMETRE)
                    {
                        jumpShotFlewOver = true;
                        flewOverThisFrame = true;
                    }
                }
                if (jumpShotFlewOver && !flewOverThisFrame)
                {
                    table_._TriggerJumpShotFoul();
                    jumpShotFlewOver = false;//prevent this from being called again
                }
            }
        }

        ball_bit = 0x1U;

        // Run triggers
        table._BeginPerf(table.PERF_PHYSICS_POCKET);
        if (!table.is4Ball)
        {
            for (int i = 0; i < 16; i++)
            {
                if (moved[i] && (ball_bit & sn_pocketed) == 0U && (i != 0 || !outOfBounds))
                {
                    if (i != 0 || canCueBallBounceOffCushion)
                    {
                        _phy_ball_pockets(i, balls_P);
                    }
                }

                ball_bit <<= 1;
            }
        }
        table._EndPerf(table.PERF_PHYSICS_POCKET);
    }

    // ( Since v0.2.0a ) Check if we can predict a collision before move update happens to improve accuracy
    // This function predicts if the cue ball is about to hit another ball, and if it is, it teleports it
    // to the surface of that ball, instead of letting it clip into that ball
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

        for (int i = 1; i < 16; i++)
        {
            ball_bit <<= 1;

            if ((ball_bit & sn_pocketed) != 0U)
                continue;

            h = balls_P[i] - balls_P[0];
            lf = Vector3.Dot(norm, h);
            if (lf < 0f) continue; // discard balls that are behind the movement direction

            s = k_BALL_DSQRPE - Vector3.Dot(h, h) + lf * lf; // I assume this checks if predicted new position is inside another ball
                                                             // why dot product with the same values????
            if (s < 0.0f) // and skips if it isn't
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
    private bool stepOneBall(int id, uint sn_pocketed, bool[] moved)
    {
        GameObject g_ball_current = balls[id];

        bool isBallMoving = false;

        // no point updating velocity if ball isn't moving
        if (balls_V[id] != Vector3.zero || balls_W[id] != Vector3.zero)
        {
            isBallMoving = updateVelocity(id, g_ball_current);
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

            if (dist < k_BALL_DIAMETRESQ)
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

                Vector3 cueBallVelPrev = balls_V[0];
                Vector3 reflection = normal * dot;
                balls_V[id] -= reflection;
                balls_V[i] += reflection;

                // Prevent sound spam if it happens
                if (balls_V[id].sqrMagnitude > 0 && balls_V[i].sqrMagnitude > 0)
                {
                    g_ball_current.GetComponent<AudioSource>().PlayOneShot(hitSounds[id % 3], Mathf.Clamp01(dot));
                }
                if (table_.isSnooker6Red)
                {
                    if (!cueBallHasCollided && id == 0 && balls_P[0].y > 0)
                    {
                        // In snooker it's a foul if the cue ball jumps over the object ball even if it hits it in the process
                        // check if cue ball is moving faster in the direction of the movement of the object ball to determine if it's going to go over it.
                        // there may be unknown problems with this implementation.
                        Vector3 ballid = balls_V[id]; ballid.y = 0;
                        Vector3 balli = balls_V[i]; balli.y = 0;
                        ballid *= ballid.magnitude / balli.magnitude;
                        balli = balli.normalized;
                        float velDot = Vector3.Dot(ballid, balli);

                        // detect if ball landed on top of the far side of the ball, which means by definition you went over it (this case is not covered by the velDot check)
                        Vector3 flattenedCueBallVelPrev = cueBallVelPrev;
                        flattenedCueBallVelPrev.y = 0;
                        bool dotBehind = Vector3.Dot(flattenedCueBallVelPrev, delta) < 0;

                        if (velDot > 1 || dotBehind)
                        {
                            table_._TriggerJumpShotFoul();
                        }
                        cueBallHasCollided = true;
                    }
                }
                table._TriggerCollision(id, i);
            }
        }

        return isBallMoving;
    }

    private bool updateVelocity(int id, GameObject ball)
    {
        bool ballMoving = false;

        // Since v1.5.0
        Vector3 V = balls_V[id];
        Vector3 VwithoutY = new Vector3(V.x, 0, V.z);
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

        if (balls_P[id].y < 0.001)
        {
            // Relative contact velocity of ball and table
            cv = VwithoutY + Vector3.Cross(k_CONTACT_POINT, W);
            float cvMagnitude = cv.magnitude;

            // Rolling is achieved when cv's length is approaching 0
            // The epsilon is quite high here because of the fairly large timestep we are working with
            if (cvMagnitude <= 0.1f)
            {
                //V += -k_F_ROLL * k_GRAVITY * k_FIXED_TIME_STEP * V.normalized;
                // (baked):
                V += -0.00122583125f * VwithoutY.normalized;

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
                if (V.sqrMagnitude < 0.0001f && W.magnitude < 0.04f)
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
        }
        else
        {
            ballMoving = true;
        }

        if (balls_P[id].y > 0) // small epsilon to apply gravity
            V.y -= k_GRAVITY * k_FIXED_TIME_STEP;
        else
            V.y = 0;

        balls_W[id] = W;
        balls_V[id] = V;

        ball.transform.Rotate(this.transform.TransformDirection(W.normalized), W.magnitude * k_FIXED_TIME_STEP * -Mathf.Rad2Deg, Space.World);

        return ballMoving;
    }

    public void _ResetSimulationVariables()
    {
        jumpShotFlewOver = cueBallHasCollided = false;
    }

    private void ModifyAudioProperties(int id, GameObject Ball)
    {
        Vector3 V = balls_V[id];

        // Decrease volume and pitch with each bounce
        audioSource.volume = initialVolume * Mathf.Pow(K_BOUNCE_FACTOR, V.y);
        audioSource.pitch = initialPitch * Mathf.Pow(K_BOUNCE_FACTOR, V.y);
    } // We could play a sound of slate for Imersion. (1-1)

    private void ResetAudioProperties()
    {
        // Reset volume and pitch when ball comes to rest
        audioSource.volume = initialVolume;
        audioSource.pitch = initialPitch;
    } // We could play a sound of slate for Imersion. (2-2)

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
        if (table.is8Ball || table.isSnooker6Red)
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
    void _phy_bounce_cushion(int id, Vector3 N)
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

        table._TriggerBounceCushion(id);
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

    Vector3 k_vC_vZ = new Vector3();
    Vector3 k_vB_vY = new Vector3();
    Vector3 k_vB_vY_normal = new Vector3();

    Vector3 k_vC_vZ_normal = new Vector3();

    Vector3 k_vA_vB_normal = new Vector3(0.0f, 0.0f, -1.0f);
    Vector3 k_vC_vW_normal = new Vector3(-1.0f, 0.0f, 0.0f);

    Vector3 _sign_pos = new Vector3(0.0f, 1.0f, 0.0f);
    float caromEdgeX, caromEdgeZ;
    Vector2 tableEdge; // outer edges of the table's rail
    Vector2 tableBounds; // distances at which ball falls off table, bigger than tableEdge if the pocket sticks out from the table, otherwise the same
    float k_FACING_ANGLE_CORNER = 135f;
    float k_FACING_ANGLE_SIDE = 14.93142f;

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
        float epsilon = 0.000002f; // ??
        k_BALL_DIAMETRESQ = k_BALL_DIAMETRE * k_BALL_DIAMETRE;
        k_BALL_DSQRPE = k_BALL_DIAMETRESQ - epsilon;
        k_BALL_1OR = 1 / k_BALL_RADIUS;
        k_BALL_DSQR = k_BALL_DIAMETRE * k_BALL_DIAMETRE;
        k_BALL_RSQR = k_BALL_RADIUS * k_BALL_RADIUS;
        k_BALL_MASS = table.k_BALL_MASS;
        k_CONTACT_POINT = new Vector3(0.0f, -k_BALL_RADIUS, 0.0f);
        k_vE = table.k_vE; //cornerPocket
        k_vF = table.k_vF; //sidePocket
        k_vE2 = table.k_vE2; //cornerPocket2
        k_vF2 = table.k_vF2; //sidePocket2
        k_vE.y = k_vF.y = k_vE2.y = k_vF2.y = 0;
        //work out which pocket point's edge is most distant from center
        furthest_vE = (k_vE + k_vE.normalized * k_INNER_RADIUS_CORNER).magnitude > (k_vE2 + k_vE2.normalized * k_INNER_RADIUS_CORNER2).magnitude;
        furthest_vF = (k_vF + k_vF.normalized * k_INNER_RADIUS_SIDE).magnitude > (k_vF2 + k_vF2.normalized * k_INNER_RADIUS_SIDE2).magnitude;
        //work out which pocket point's edge is closest to center
        closest_vE = (k_vE - k_vE.normalized * k_INNER_RADIUS_CORNER).magnitude < (k_vE2 - k_vE2.normalized * k_INNER_RADIUS_CORNER2).magnitude;
        closest_vF = (k_vF - k_vF.normalized * k_INNER_RADIUS_SIDE).magnitude < (k_vF2 - k_vF2.normalized * k_INNER_RADIUS_SIDE2).magnitude;

        Collider[] collider = table.GetComponentsInChildren<Collider>();
        for (int i = 0; i < collider.Length; i++)
        {
            collider[i].enabled = true;
        }

        // Handy values
        k_MINOR_REGION_CONST = k_TABLE_WIDTH - k_TABLE_HEIGHT;

        // Major source vertices
        k_vA.x = k_POCKET_RADIUS_SIDE;
        k_vA.z = k_TABLE_HEIGHT + k_CUSHION_RADIUS;

        k_vB.x = k_TABLE_WIDTH;
        k_vB.z = k_TABLE_HEIGHT + k_CUSHION_RADIUS;

        k_vC.x = k_TABLE_WIDTH + k_CUSHION_RADIUS;
        k_vC.z = k_TABLE_HEIGHT;

        k_vD = k_vA;
        Vector3 Rotationk_vD = new Vector3(k_POCKET_DEPTH_SIDE, 0, 0);
        Rotationk_vD = Quaternion.AngleAxis(-k_FACING_ANGLE_SIDE, Vector3.up) * Rotationk_vD;
        k_vD += Rotationk_vD;

        // Aux points
        k_vX = k_vD + Vector3.forward;
        k_vW = k_vC;
        k_vW.z = 0.0f;

        k_vY = k_vB;
        Vector3 Rotationk_vY = new Vector3(-.2f, 0, 0);
        Rotationk_vY = Quaternion.AngleAxis(k_FACING_ANGLE_CORNER, Vector3.up) * Rotationk_vY;
        k_vY += Rotationk_vY;

        k_vZ = k_vC;
        Vector3 Rotationk_vZ = new Vector3(0, 0, -.2f);
        Rotationk_vZ = Quaternion.AngleAxis(-k_FACING_ANGLE_CORNER, Vector3.up) * Rotationk_vZ;
        k_vZ += Rotationk_vZ;

        // Normals
        k_vA_vD = k_vD - k_vA;
        k_vA_vD = k_vA_vD.normalized;
        k_vA_vD_normal.x = -k_vA_vD.z;
        k_vA_vD_normal.z = k_vA_vD.x;

        k_vB_vY = k_vB - k_vY;
        k_vB_vY = k_vB_vY.normalized;
        k_vB_vY_normal.x = -k_vB_vY.z;
        k_vB_vY_normal.z = k_vB_vY.x;

        //set up angle properly instead of just mirroring, required for facing angle
        k_vC_vZ = k_vC - k_vZ;
        k_vC_vZ = k_vC_vZ.normalized;
        k_vC_vZ_normal.x = k_vC_vZ.z;
        k_vC_vZ_normal.z = -k_vC_vZ.x;

        // Minkowski difference
        k_pN = k_vA;
        k_pN.z -= k_CUSHION_RADIUS;

        k_pL = k_vD + k_vA_vD_normal * k_CUSHION_RADIUS;

        k_pK = k_vD;
        k_pK.x -= k_CUSHION_RADIUS;

        k_pO = k_vB;
        k_pO.z -= k_CUSHION_RADIUS; // only used in carom, but also used to draw point in HT8B_DRAW_REGIONS, carom requires it to be r_k_cushion_radius;
        k_pP = k_vB + k_vB_vY_normal * k_CUSHION_RADIUS;
        k_pQ = k_vC + k_vC_vZ_normal * k_CUSHION_RADIUS;

        k_pR = k_vC;
        k_pR.x -= k_CUSHION_RADIUS; // only used in carom, but also used to draw point in HT8B_DRAW_REGIONS, carom requires it to be r_k_cushion_radius;

        k_RAIL_DEPTH_WIDTH = table.k_RAIL_DEPTH_WIDTH;
        k_RAIL_DEPTH_HEIGHT = table.k_RAIL_DEPTH_HEIGHT;

        tableEdge.x = k_TABLE_WIDTH + k_RAIL_DEPTH_WIDTH + k_BALL_RADIUS;
        tableEdge.y = k_TABLE_HEIGHT + k_RAIL_DEPTH_HEIGHT + k_BALL_RADIUS;
        tableBounds.x = Mathf.Max(
            k_TABLE_WIDTH + k_RAIL_DEPTH_WIDTH + k_BALL_RADIUS,
            furthest_vE ? (k_vE2.x + k_INNER_RADIUS_CORNER2) : (k_vE.x + k_INNER_RADIUS_CORNER),
            furthest_vF ? (k_vF2.x + k_INNER_RADIUS_SIDE2) : (k_vF.x + k_INNER_RADIUS_SIDE)
        );
        tableBounds.y = Mathf.Max(
            k_TABLE_HEIGHT + k_RAIL_DEPTH_HEIGHT + k_BALL_RADIUS,
            furthest_vE ? (k_vE2.z + k_INNER_RADIUS_CORNER2) : (k_vE.z + k_INNER_RADIUS_CORNER),
            furthest_vF ? (k_vF2.z + k_INNER_RADIUS_SIDE2) : (k_vF.z + k_INNER_RADIUS_SIDE)
        );

        caromEdgeX = k_pR.x - k_BALL_RADIUS;
        caromEdgeZ = k_pO.z - k_BALL_RADIUS;

        // move points to enable pocket radius tweaking and adjusting cushion radius without moving cushions
        // also makes table width and height actual equal the playable space on the table
        // k_pM is only used for drawing lines, and this
        k_pM = k_vA + k_vA_vD_normal * k_CUSHION_RADIUS;

        float sideXdifA = k_vA.x - k_pM.x;
        float sideXdifD = k_vD.x - k_pM.x;
        float sideXdifN = k_pN.x - k_pM.x;
        float sideXdifT = k_pT.x - k_pM.x;
        float sideXdifL = k_pL.x - k_pM.x;
        float sideXdifK = k_pK.x - k_pM.x;
        float sideXdifX = k_vX.x - k_pM.x;
        k_pN.x += k_POCKET_RADIUS_SIDE;
        k_pM.x = k_pN.x;
        k_pN.x = k_pM.x + sideXdifN;
        k_pT.x = k_pM.x + sideXdifT;
        k_pL.x = k_pM.x + sideXdifL;
        k_pK.x = k_pM.x + sideXdifK;
        k_vA.x = k_pM.x + sideXdifA;
        k_vD.x = k_pM.x + sideXdifD;
        k_vX.x = k_pM.x + sideXdifX;

        float widthXdifB = k_vB.x - k_pP.x;
        float widthXdifR = k_pO.x - k_pP.x;
        float widthXdifY = k_vY.x - k_pP.x;
        // float widthXdifV = k_pU.x - k_pP.x;
        k_pO.x -= k_POCKET_WIDTH_CORNER;
        k_pP.x = k_pO.x;
        k_pO.x = k_pP.x + widthXdifR;
        k_vB.x = k_pP.x + widthXdifB;
        k_vY.x = k_pP.x + widthXdifY;
        // k_pU.x = k_pP.x + widthXdifV;

        float heightZdifC = k_vC.z - k_pQ.z;
        float heightZdifZ = k_vZ.z - k_pQ.z;
        // float heightZdifR = k_pR.z - k_pQ.z;
        // float heightZdifV = k_pV.z - k_pQ.z;
        k_pR.z -= k_POCKET_HEIGHT_CORNER;
        k_pQ.z = k_pR.z;
        // k_pR.z = k_pQ.z + heightZdifR;
        k_vC.z = k_pQ.z + heightZdifC;
        k_vZ.z = k_pQ.z + heightZdifZ;
        // k_pV.z = k_pQ.z + heightZdifV;

#if HT8B_DRAW_REGIONS
        // for drawing lines only
        k_pT = k_vX;
        k_pT.x -= k_CUSHION_RADIUS;

        k_pS = k_vW;
        k_pS.x -= k_CUSHION_RADIUS;

        k_pU = k_vY + k_vB_vY_normal * k_CUSHION_RADIUS;
        k_pV = k_vZ + k_vC_vZ_normal * k_CUSHION_RADIUS;
#endif
    }

    // Check pocket condition
    void _phy_ball_pockets(int id, Vector3[] balls_P)
    {
        Vector3 A = balls_P[id];
        Vector3 absA = new Vector3(Mathf.Abs(A.x), 0, Mathf.Abs(A.z));

        if ((absA - k_vE).sqrMagnitude < k_INNER_RADIUS_CORNER_SQ && (absA - k_vE2).sqrMagnitude < k_INNER_RADIUS_CORNER_SQ2)
        {
            table._TriggerPocketBall(id, false);
            pocketedTime = Time.time;
            return;
        }

        if ((absA - k_vF).sqrMagnitude < k_INNER_RADIUS_SIDE_SQ && (absA - k_vF2).sqrMagnitude < k_INNER_RADIUS_SIDE_SQ2)
        {
            table._TriggerPocketBall(id, false);
            pocketedTime = Time.time;
            return;
        }

        if (absA.z > tableEdge.y)
        {
            if (absA.z > tableBounds.y || (A.y < 0))
            {
                table._TriggerPocketBall(id, true);
                pocketedTime = Time.time;
                return;
            }
        }

        if (absA.x > tableEdge.x)
        {
            if (absA.x > tableBounds.x || (A.y < 0))
            {
                table._TriggerPocketBall(id, true);
                pocketedTime = Time.time;
                return;
            }
        }
    }

    // Pocketless table
    void _phy_ball_table_carom(int id)
    {
        Vector3 A = balls_P[id];
        _sign_pos.x = Mathf.Sign(A.x);
        _sign_pos.z = Mathf.Sign(A.z);
        A = Vector3.Scale(A, _sign_pos);

        // Setup major regions

        if (A.x > caromEdgeX)
        {
            A.x = caromEdgeX;
            _phy_bounce_cushion(id, Vector3.left * _sign_pos.x);
        }

        if (A.z > caromEdgeZ)
        {
            A.z = caromEdgeZ;
            _phy_bounce_cushion(id, Vector3.back * _sign_pos.z);
        }
        balls_P[id] = Vector3.Scale(A, _sign_pos);
    }

    void _phy_ball_table_std(int id)
    {
        Vector3 N, _V, V, a_to_v;
        float dot;

        Vector3 newPos = balls_P[id];
        Vector3 newVel = balls_V[id];
        Vector3 newAngVel = balls_W[id];

        _sign_pos.x = Mathf.Sign(newPos.x);
        _sign_pos.z = Mathf.Sign(newPos.z);
        newPos = Vector3.Scale(newPos, _sign_pos);
        Vector3 newPosPR = newPos;
        newPosPR.x += k_BALL_RADIUS;
        newPosPR.z += k_BALL_RADIUS;

        float r_k_CUSHION_RADIUS = k_CUSHION_RADIUS + k_BALL_RADIUS;

#if HT8B_DRAW_REGIONS
        Debug.DrawLine(k_vA, k_vB, Color.white);
        Debug.DrawLine(k_vD, k_vA, Color.white);
        Debug.DrawLine(k_vB, k_vY, Color.white);
        Debug.DrawLine(k_vD, k_vX, Color.white);
        Debug.DrawLine(k_vC, k_vW, Color.white);
        Debug.DrawLine(k_vC, k_vZ, Color.white);

        //    r_k_CUSHION_RADIUS = k_CUSHION_RADIUS-k_BALL_RADIUS;

        //    _phy_table_init();

        Debug.DrawLine(k_pT, k_pK, Color.yellow);
        Debug.DrawLine(k_pK, k_pL, Color.yellow);
        Debug.DrawLine(k_pL, k_pM, Color.yellow);
        Debug.DrawLine(k_pM, k_pN, Color.yellow);
        Debug.DrawLine(k_pN, k_pO, Color.yellow);
        Debug.DrawLine(k_pO, k_pP, Color.yellow);
        Debug.DrawLine(k_pP, k_pU, Color.yellow);

        Debug.DrawLine(k_pV, k_pQ, Color.yellow);
        Debug.DrawLine(k_pQ, k_pR, Color.yellow);
        Debug.DrawLine(k_pR, k_pS, Color.yellow);

        //    r_k_CUSHION_RADIUS = k_CUSHION_RADIUS;
        //    _phy_table_init();
#endif

        if (newPos.x > k_vA.x) // Major Regions
        {
            if (newPos.x > newPos.z + k_MINOR_REGION_CONST) // Minor B
            {
                if (newPos.z < k_vC.z)
                {
                    // Region H
#if HT8B_DRAW_REGIONS
                    Debug.DrawLine(new Vector3(0.0f, 0.0f, 0.0f), new Vector3(k_TABLE_WIDTH, 0.0f, 0.0f), Color.red);
                    Debug.DrawLine(k_vC, k_vC + k_vC_vW_normal, Color.red);
#endif
                    if (newPos.x > k_TABLE_WIDTH - k_BALL_RADIUS)
                    {
                        // Static resolution
                        newPos.x = k_TABLE_WIDTH - k_BALL_RADIUS;
                        // Dynamic
                        _phy_bounce_cushion(id, Vector3.Scale(k_vC_vW_normal, _sign_pos));
#if HT8B_DRAW_REGIONS
                        if (id == 0) Debug.Log("Region H");
#endif
                    }
                }
                else
                {
                    a_to_v = newPos - k_vC;

                    if (Vector3.Dot(a_to_v, k_vB_vY) > 0.0f)
                    {
                        // Region I ( VORONI ) (NEAR CORNER POCKET)
#if HT8B_DRAW_REGIONS
                        Debug.DrawLine(k_vC, k_pR, Color.green);
                        Debug.DrawLine(k_vC, k_pQ, Color.green);
#endif
                        if (a_to_v.magnitude < r_k_CUSHION_RADIUS)
                        {
                            // Static resolution
                            N = a_to_v.normalized;
                            newPos = k_vC + N * r_k_CUSHION_RADIUS;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos));
#if HT8B_DRAW_REGIONS
                            if (id == 0) Debug.Log("Region I ( VORONI ) (NEAR CORNER POCKET)");
#endif
                        }
                    }
                    else
                    {
                        // Region J (Inside Corner Pocket)
#if HT8B_DRAW_REGIONS
                        Debug.DrawLine(k_vC, k_vB, Color.red);
                        Debug.DrawLine(k_pQ, k_pV, Color.blue);
#endif
                        a_to_v = newPos - k_pQ;

                        if (Vector3.Dot(k_vC_vZ_normal, a_to_v) < k_BALL_RADIUS)
                        {
                            // Static resolution
                            dot = Vector3.Dot(a_to_v, k_vC_vZ);
                            newPos = k_pQ + dot * k_vC_vZ + k_vC_vZ_normal * k_BALL_RADIUS;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(k_vC_vZ_normal, _sign_pos));
#if HT8B_DRAW_REGIONS
                            if (id == 0) Debug.Log("Region J (Inside Corner Pocket)");
#endif
                        }
                        /* if (!                        {
                            Vector3 toPocketEdge = newPos - k_vE;
                            if (Vector3.Dot(toPocketEdge, k_vE) > 0)
                            {
                                // actually above the corner pocket itself, collision for the back of it if you jump over it
                                // TODO: sqrmag inner radius
                                if (toPocketEdge.sqrMagnitude + k_BALL_DSQR > k_INNER_RADIUS_CORNER_SQ)
                                {
                                    Vector3 pocketNormal = toPocketEdge.normalized;
                                    // Static resolution
                                    newPos = k_vE + pocketNormal * k_INNER_RADIUS_CORNER - pocketNormal * k_BALL_RADIUS;

                                    // Dynamic
                                    _phy_bounce_cushion(id, Vector3.Scale(-pocketNormal, _sign_pos));
                                                                    }
                            }
                        } */
                    }
                }
            }
            else // Minor A
            {
                if (newPos.x < k_vB.x)
                {
                    // Region A
#if HT8B_DRAW_REGIONS
                    Debug.DrawLine(k_vA, k_vA + k_vA_vB_normal, Color.red);
                    Debug.DrawLine(k_vB, k_vB + k_vA_vB_normal, Color.red);
#endif
                    if (newPosPR.z > k_pN.z)
                    {
                        // Velocity based A->C delegation ( scuffed CCD )
                        a_to_v = newPos - k_vA;
                        _V = Vector3.Scale(newVel, _sign_pos);
                        V.x = -_V.z;
                        V.y = 0.0f;
                        V.z = _V.x;

                        if (newPos.z > k_vA.z)
                        {
                            if (Vector3.Dot(V, a_to_v) > 0.0f)
                            {
                                Debug.DrawRay(balls[0].transform.parent.TransformPoint(Vector3.Scale(k_vA, _sign_pos)), V * .3f, Color.red, 3f);
                                Debug.DrawRay(balls[0].transform.parent.TransformPoint(Vector3.Scale(k_vA, _sign_pos)), a_to_v * .3f, Color.white, 3f);
                                // Region C ( Delegated )
                                a_to_v = newPos - k_pL;

                                // Static resolution
                                dot = Vector3.Dot(a_to_v, k_vA_vD);
                                newPos = k_pL + dot * k_vA_vD + k_vA_vD_normal * k_BALL_RADIUS;

                                // Dynamic
                                _phy_bounce_cushion(id, Vector3.Scale(k_vA_vD_normal, _sign_pos));
#if HT8B_DRAW_REGIONS
                                if (id == 0) Debug.Log("Region C ( Delegated )");
#endif
                            }
                            else
                            {
                                // Static resolution
                                newPos.z = k_pN.z - k_BALL_RADIUS;

                                // Dynamic
                                _phy_bounce_cushion(id, Vector3.Scale(k_vA_vB_normal, _sign_pos));
#if HT8B_DRAW_REGIONS
                                if (id == 0) Debug.Log("Region A II");
#endif
                            }
                        }
                        else
                        {
                            {
                                // Static resolution
                                newPos.z = k_pN.z - k_BALL_RADIUS;

                                // Dynamic
                                _phy_bounce_cushion(id, Vector3.Scale(k_vA_vB_normal, _sign_pos));
#if HT8B_DRAW_REGIONS
                                if (id == 0) Debug.Log("Region A");
#endif
                            }
                        }
                    }
                }
                else
                {
                    a_to_v = newPos - k_vB;

                    if (Vector3.Dot(a_to_v, k_vB_vY) > 0.0f)
                    {
                        // Region F ( VERONI ) (NEAR CORNER POCKET)
#if HT8B_DRAW_REGIONS
                        Debug.DrawLine(k_vB, k_pO, Color.green);
                        Debug.DrawLine(k_vB, k_pP, Color.green);
#endif
                        if (a_to_v.magnitude < r_k_CUSHION_RADIUS)
                        {
                            // Static resolution
                            N = a_to_v.normalized;
                            newPos = k_vB + N * r_k_CUSHION_RADIUS;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos));
#if HT8B_DRAW_REGIONS
                            if (id == 0) Debug.Log("Region F ( VERONI ) (NEAR CORNER POCKET)");
#endif
                        }
                    }
                    else
                    {
                        // Region G (Inside Corner Pocket)
#if HT8B_DRAW_REGIONS
                        Debug.DrawLine(k_vB, k_vC, Color.red);
                        Debug.DrawLine(k_pP, k_pU, Color.blue);
#endif
                        a_to_v = newPos - k_pP;

                        if (Vector3.Dot(k_vB_vY_normal, a_to_v) < k_BALL_RADIUS)
                        {
                            // Static resolution
                            dot = Vector3.Dot(a_to_v, k_vB_vY);
                            newPos = k_pP + dot * k_vB_vY + k_vB_vY_normal * k_BALL_RADIUS;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(k_vB_vY_normal, _sign_pos));
#if HT8B_DRAW_REGIONS
                            if (id == 0) Debug.Log("Region G (Inside Corner Pocket)");
#endif
                        }
                        /* if (!                        {
                            Vector3 toPocketEdge = newPos - k_vE;
                            if (Vector3.Dot(toPocketEdge, k_vE) > 0)
                            {
                                // actually above the corner pocket itself, collision for the back of it if you jump over it
                                // TODO: sqrmag inner radius
                                if (toPocketEdge.sqrMagnitude + k_BALL_DSQR > k_INNER_RADIUS_CORNER_SQ)
                                {
                                    Vector3 pocketNormal = toPocketEdge.normalized;
                                    // Static resolution
                                    newPos = k_vE + pocketNormal * k_INNER_RADIUS_CORNER - pocketNormal * k_BALL_RADIUS;

                                    // Dynamic
                                    _phy_bounce_cushion(id, Vector3.Scale(-pocketNormal, _sign_pos));
                                                                    }
                            }
                        } */
                    }
                }
            }
        }
        else
        {
            a_to_v = newPos - k_vA;

            if (Vector3.Dot(a_to_v, k_vA_vD) > 0.0f)
            {
                a_to_v = newPos - k_vD;

                if (Vector3.Dot(a_to_v, k_vA_vD) > 0.0f)
                {
                    if (newPos.z > k_pK.z)
                    {
                        // Region E
#if HT8B_DRAW_REGIONS
                        Debug.DrawLine(k_vD, k_vD + k_vC_vW_normal, Color.red);
#endif
                        if (newPos.x > k_pK.x)
                        {
                            // Static resolution
                            newPos.x = k_pK.x;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(k_vC_vW_normal, _sign_pos));
#if HT8B_DRAW_REGIONS
                            if (id == 0) Debug.Log("Region E");
#endif
                        }
                    }
                    else
                    {
                        // Region D ( VORONI )
#if HT8B_DRAW_REGIONS
                        Debug.DrawLine(k_vD, k_vD + k_vC_vW_normal, Color.green);
                        Debug.DrawLine(k_vD, k_vD + k_vA_vD_normal, Color.green);
#endif
                        if (a_to_v.magnitude < r_k_CUSHION_RADIUS)
                        {
                            // Static resolution
                            N = a_to_v.normalized;
                            newPos = k_vD + N * r_k_CUSHION_RADIUS;

                            // Dynamic
                            _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos));
#if HT8B_DRAW_REGIONS
                            if (id == 0) Debug.Log("Region D ( VORONI )");
#endif
                        }
                    }
                }
                else
                {
                    // Region C
#if HT8B_DRAW_REGIONS
                    Debug.DrawLine(k_vA, k_vA + k_vA_vD_normal, Color.red);
                    Debug.DrawLine(k_vD, k_vD + k_vA_vD_normal, Color.red);
                    Debug.DrawLine(k_pL, k_pM, Color.blue);
#endif
                    a_to_v = newPos - k_pL;

                    if (Vector3.Dot(k_vA_vD_normal, a_to_v) < k_BALL_RADIUS)
                    {
                        // Static resolution
                        dot = Vector3.Dot(a_to_v, k_vA_vD);
                        newPos = k_pL + dot * k_vA_vD + k_vA_vD_normal * k_BALL_RADIUS;

                        // Dynamic
                        _phy_bounce_cushion(id, Vector3.Scale(k_vA_vD_normal, _sign_pos));
#if HT8B_DRAW_REGIONS
                        if (id == 0) Debug.Log("Region C");
#endif
                    }
                }
            }
            else
            {
                // Region B ( VORONI )
#if HT8B_DRAW_REGIONS
                Debug.DrawLine(k_vA, k_vA + k_vA_vB_normal, Color.green);
                Debug.DrawLine(k_vA, k_vA + k_vA_vD_normal, Color.green);
#endif
                if (a_to_v.magnitude < r_k_CUSHION_RADIUS)
                {
                    // Static resolution
                    N = a_to_v.normalized;
                    newPos = k_vA + N * r_k_CUSHION_RADIUS;

                    // Dynamic
                    _phy_bounce_cushion(id, Vector3.Scale(N, _sign_pos));
#if HT8B_DRAW_REGIONS
                    if (id == 0) Debug.Log("Region B ( VORONI )");
#endif
                }
            }
        }
        balls_P[id] = Vector3.Scale(newPos, _sign_pos);
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

    public float inV0;
    public void _ApplyPhysics()
    {
        applyPhysics(inV0);
    }

    private void applyPhysics(float V0)
    {
        GameObject cuetip = table.activeCue._GetCuetip();

        Vector3 q = transform_Surface.InverseTransformDirection(cuetip.transform.forward);// direction of cue in surface space
        Vector3 o = balls_P[0];


        Vector3 j = -Vector3.ProjectOnPlane(q, transform_Surface.up); // project cue direction onto table surface, gives us j
        Vector3 k = transform_Surface.up;
        Vector3 i = Vector3.Cross(j, k);

        Plane jkPlane = new Plane(i, o);

        Vector3 Q = RaySphere_output;
        // Clamp the increase in spin from hitting the ball further from the center by moving the hit point towards the center
        Vector3 Qflat = Vector3.ProjectOnPlane(Q - o, q);
        float distFromCenter = Qflat.magnitude / k_BALL_RADIUS;
        if (distFromCenter > CueMaxHitRadius)
        {
            _phy_ray_sphere((o + Qflat.normalized * k_BALL_RADIUS * CueMaxHitRadius) - q * k_BALL_DIAMETRE, q, o);
            Q = RaySphere_output;
        }

        float a = jkPlane.GetDistanceToPoint(Q);
        float b = Q.y - o.y;
        float c = Mathf.Sqrt(Mathf.Pow(k_BALL_RADIUS, 2) - Mathf.Pow(a, 2) - Mathf.Pow(b, 2));

        float adj = Mathf.Sqrt(Mathf.Pow(q.x, 2) + Mathf.Pow(q.z, 2));
        float opp = q.y;
        float theta = -Mathf.Atan(opp / adj);

        float cosTheta = Mathf.Cos(theta);
        float sinTheta = Mathf.Sin(theta);

        float k_CUE_MASS = 0.5f; // kg
        float F = 2 * k_BALL_MASS * V0 / (1 + k_BALL_MASS / k_CUE_MASS + 5 / (2 * k_BALL_RADIUS) * (Mathf.Pow(a, 2) + Mathf.Pow(b, 2) * Mathf.Pow(cosTheta, 2) + Mathf.Pow(c, 2) * Mathf.Pow(sinTheta, 2) - 2 * b * c * cosTheta * sinTheta));
        table._LogWarn("cue ball was hit at (" + a.ToString("F2") + "," + b.ToString("F2") + "," + c.ToString("F2") + ") with angle " + theta * Mathf.Rad2Deg + " and initial velocity " + V0.ToString("F2") + "m/s");

        float I = 2f / 5f * k_BALL_MASS * Mathf.Pow(k_BALL_RADIUS, 2);
        Vector3 v = new Vector3(0, -F / k_BALL_MASS * cosTheta, -F / k_BALL_MASS * sinTheta);
        Vector3 w = 1 / I * new Vector3(-c * F * sinTheta + b * F * cosTheta, a * F * sinTheta, -a * F * cosTheta);

        // the paper is inconsistent here. either w.x is inverted (i.e. the i axis points right instead of left) or b is inverted (which means F is wrong too)
        // for my sanity I'm going to assume the former
        w.x = -w.x;
        table._LogWarn("initial cue ball velocities are v=" + v + ", w=" + w);

        float m_e = 0.02f;

        // https://billiards.colostate.edu/physics_articles/Alciatore_pool_physics_article.pdf
        float alpha = -Mathf.Atan(
           (5f / 2f * a / k_BALL_RADIUS * Mathf.Sqrt(1f - Mathf.Pow(a / k_BALL_RADIUS, 2))) /
           (1 + k_BALL_MASS / m_e + 5f / 2f * (1f - Mathf.Pow(a / k_BALL_RADIUS, 2)))
        ) * 180 / Mathf.PI;

        // rewrite to the axis we expect
        v = new Vector3(-v.x, v.z, -v.y);
        w = new Vector3(w.x, -w.z, w.y);

        Vector3 preJumpV = v;
        if (v.y > 0) //0f
        {
            // no scooping
            v.y = 0;
            table._Log("prevented scooping");
        }
        else if (v.y < 0)
        {   /*
            // the ball must not be under the cue after one time step
            float k_MIN_HORIZONTAL_VEL = (k_BALL_RADIUS - c) / k_FIXED_TIME_STEP;
            if (v.z < k_MIN_HORIZONTAL_VEL)
            {
                // not enough strength to be a jump shot
                v.y = 0;
                table._Log("not enough strength for jump shot (" + k_MIN_HORIZONTAL_VEL + " vs " + v.z + ")");
            }
            */

            {
                // dampen y velocity because the table will eat a lot of energy (we're driving the ball straight into it)
                v.y = -v.y * K_BOUNCE_FACTOR; //0.35f
                if (v.y < k_GRAVITY * k_FIXED_TIME_STEP)
                {
                    v.y = 0f;
                }
                table._Log("dampening to " + v.y);
            }
        }

        // translate
        Quaternion r = Quaternion.FromToRotation(Vector3.back, j);
        v = r * v;
        w = r * w;

        // apply squirt
        v = Quaternion.AngleAxis(alpha, transform_Surface.up) * v;

        // done
        balls_V[0] = v;
        balls_W[0] = w;
    }
}
