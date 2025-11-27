Shader "metaphira/Physics"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
	}
		SubShader
	{
		Tags { "RenderType" = "Opaque" }
		LOD 100

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			// make fog work
			#pragma multi_compile_fog

			#include "UnityCG.cginc"

			#define MAX_BALLS 22

			int _SimulationId;
			int _NBallPositions;
			float _BallsP[MAX_BALLS * 3];
			float _BallsV[MAX_BALLS * 3];
			float _BallsW[MAX_BALLS * 3];

			typedef struct
			{
				int id;

				int num_balls;
				int pocketed;
				int steps;
				float3 balls_p[MAX_BALLS];
				float3 balls_v[MAX_BALLS];
				float3 balls_w[MAX_BALLS];
			} phys_state_t;

			static phys_state_t phys_state;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
            };

			Texture2D<float4> _MainTex;
            float4 _MainTex_ST;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }

			#define FLT_MAX					3.402823466e+38 // Maximum representable floating-point number
			#define FIXED_TIME_STEP			0.0125f
			#define BALL_RADIUS				0.03f
			#define BALL_RADIUS_RECIP		(1 / BALL_RADIUS)
			#define BALL_DIAMETER			(BALL_RADIUS * 2)
			#define BALL_DIAMETER_SQ		(BALL_DIAMETER * BALL_DIAMETER)
			#define EPSILON					2.0e-6
			#define BALL_DIAMETER_SQ_SEP	(BALL_DIAMETER_SQ - EPSILON)
			#define CONTACT_POINT			float3(0.0f, -BALL_RADIUS, 0.0f)
			#define GRAVITY					9.80665f
			#define STEPS_PER_FRAME			10

			float3 calculate_delta_position()
			{
				float3 originalDelta = phys_state.balls_v[0] * FIXED_TIME_STEP;
				float3 norm = normalize(phys_state.balls_v[0]);

				float3 h;
				float lf, s, nmag;

				// closest found values
				float minlf = FLT_MAX;
				int minid = 0;
				float mins = 0;

				// loop balls to look for collisions
				for (uint i = 1; i < 16; i++)
				{
					if (((1 << i) & phys_state.pocketed) != 0) continue;

					h = phys_state.balls_p[i] - phys_state.balls_p[0];
					lf = dot(norm, h);
					if (lf < 0) continue;

					s = BALL_DIAMETER_SQ_SEP - dot(h, h) + lf * lf;

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
					nmag = minlf - sqrt(mins);

					// Assign new position if got appropriate magnitude
					if (nmag * nmag < dot(originalDelta, originalDelta))
					{
						return norm * nmag;
					}
				}

				return originalDelta;
			}


			/*private Vector3 calculateDeltaPosition(uint sn_pocketed)
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
					if (lf < 0f) continue;

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
			}*/

			bool update_velocity(uint ball)
			{
				bool ballMoving = false;

				// Since v1.5.0
				float3 V = phys_state.balls_v[ball];
				float3 VwithoutY = float3(V.x, 0, V.z);
				float3 W = phys_state.balls_w[ball];
				float3 cv;

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

				if (phys_state.balls_p[ball].y < 0.001)
				{
					// Relative contact velocity of ball and table
					cv = VwithoutY + cross(CONTACT_POINT, W);
					float cvMagnitude = length(cv);

					// Rolling is achieved when cv's length is approaching 0
					// The epsilon is quite high here because of the fairly large timestep we are working with
					if (cvMagnitude <= 0.1f)
					{
						//V += -k_F_ROLL * k_GRAVITY * k_FIXED_TIME_STEP * V.normalized;
						// (baked):
						V += -0.00122583125f * normalize(VwithoutY);

						// Calculate rolling angular velocity
						W.x = -V.z * BALL_RADIUS_RECIP;

						if (0.3f > abs(W.y))
						{
							W.y = 0.0f;
						}
						else
						{
							W.y -= sign(W.y) * 0.3f;
						}

						W.z = V.x * BALL_RADIUS_RECIP;

						// Stopping scenario
						if (dot(V, V) < 0.0001f && length(W) < 0.04f)
						{
							W = float3(0, 0, 0);
							V = float3(0, 0, 0);
						}
						else
						{
							ballMoving = true;
						}
					}
					else // Slipping
					{
						float3 nv = cv / cvMagnitude;

						// Angular slipping friction
						//W += ((-5.0f * k_F_SLIDE * k_GRAVITY)/(2.0f * 0.03f)) * k_FIXED_TIME_STEP * Vector3.Cross( Vector3.up, nv );
						// (baked):
						W += -2.04305208f * cross(float3(0, 1, 0), nv);

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

				if (phys_state.balls_p[ball].y > 0) // small epsilon to apply gravity
					V.y -= GRAVITY * FIXED_TIME_STEP;
				else
					V.y = 0;


				float3 balls_w[MAX_BALLS] = phys_state.balls_w;
				balls_w[ball] = W;
				phys_state.balls_w = balls_w;

				float3 balls_v[MAX_BALLS] = phys_state.balls_v;
				balls_v[ball] = V;
				phys_state.balls_v = balls_v;

				// ball.transform.Rotate(this.transform.TransformDirection(W.normalized), W.magnitude * k_FIXED_TIME_STEP * -Mathf.Rad2Deg, Space.World);

				return ballMoving;

			}

			/*private bool updateVelocity(int id, GameObject ball)
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
			}*/

			void handle_collision(uint ball, uint other_ball, float3 delta, float dist)
			{
				float3 balls_p[MAX_BALLS] = phys_state.balls_p;
				float3 balls_v[MAX_BALLS] = phys_state.balls_v;

				dist = sqrt(dist);
				float3 normal = delta / dist;

				// static resolution
				float3 res = (BALL_DIAMETER - dist) * normal;
				balls_p[other_ball] += res;
				balls_p[ball] -= res;

				float3 velocityDelta = phys_state.balls_v[other_ball] - phys_state.balls_v[ball];

				float dotRes = dot(velocityDelta, normal);

				// Dynamic resolution (Cr is assumed to be (1)+1.0)

				float3 reflection = normal * dotRes;
				balls_v[other_ball] -= reflection;
				balls_v[ball] += reflection;

				phys_state.balls_p = balls_p;
				phys_state.balls_v = balls_v;
			}

			bool step_one_ball(uint ball)
			{
				bool is_ball_moving = update_velocity(ball);

				for (int other_ball = ball + 1; other_ball < 2; other_ball++)
				{
					if (other_ball >= phys_state.num_balls) break; 

					if ((1 << other_ball) & phys_state.pocketed != 0) continue;

					float3 delta = phys_state.balls_p[other_ball] - phys_state.balls_p[ball];
					float dist = dot(delta, delta);

					if (dist > BALL_DIAMETER_SQ) continue;

					handle_collision(ball, other_ball, delta, dist);

					// Prevent sound spam if it happens
					/*if (balls_V[id].sqrMagnitude > 0 && balls_V[i].sqrMagnitude > 0)
					{
						g_ball_current.GetComponent<AudioSource>().PlayOneShot(hitSounds[id % 3], Mathf.Clamp01(reflection.magnitude));
					}

					table._TriggerCollision(id, i);*/
				}

				return is_ball_moving;
			}


			/*// Advance simulation 1 step for ball id
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
			}*/

			void decode()
			{
				phys_state.id = _MainTex[float2(255, 255)].x;
				phys_state.steps = _MainTex[float2(255, 255)].z;
				phys_state.num_balls = _MainTex[float2(255, 255)].a;

				if (phys_state.id != _SimulationId)
				{
					for (int i = 0; i < MAX_BALLS; i++)
					{
						phys_state.balls_p[i] = float3(_BallsP[i * 3], _BallsP[i * 3 + 1], _BallsP[i * 3 + 2]);
						phys_state.balls_v[i] = float3(_BallsV[i * 3], _BallsV[i * 3 + 1], _BallsV[i * 3 + 2]);
						phys_state.balls_w[i] = float3(_BallsW[i * 3], _BallsW[i * 3 + 1], _BallsW[i * 3 + 2]);
					}
					// phys_state.ballPositions = _BallsP;
					phys_state.steps = 0;
					phys_state.num_balls = _NBallPositions;
					phys_state.id = _SimulationId;
				}
				else
				{
					for (int i = 0; i < MAX_BALLS; i++)
					{
						phys_state.balls_p[i] = _MainTex[float2((uint) i, 0)];
						phys_state.balls_v[i] = _MainTex[float2((uint) i, 1)];
						phys_state.balls_w[i] = _MainTex[float2((uint) i, 2)];
					}
				}
			}

			float4 encode(uint2 pos)
			{
				switch (pos.y)
				{
				case 0: // ball positions
					if (pos.x < MAX_BALLS)
					{
						return float4(phys_state.balls_p[pos.x], phys_state.steps);
					}
					break;
				case 1: // ball velocities
					if (pos.x < MAX_BALLS)
					{
						return float4(phys_state.balls_v[pos.x], phys_state.steps);
					}
					break;
				case 2: // ball angular velocities
					if (pos.x < MAX_BALLS)
					{
						return float4(phys_state.balls_w[pos.x], phys_state.steps);
					}
					break;
				case 255: // metadata
					if (pos.x == 255)
					{
						return float4(phys_state.id, phys_state.pocketed, phys_state.steps, phys_state.num_balls);
					}
					break;
				}

				return float4(0, 0, 0, 0);
			}

			static uint2 s_dim;

			float4 frag (v2f i) : SV_Target
            {
				_MainTex.GetDimensions(s_dim.x, s_dim.y);
				uint2 pos = i.uv.xy * s_dim;

				decode();

				phys_state.steps++;

				for (uint i = 0; i < MAX_BALLS; i++)
				{
					if (i >= phys_state.num_balls) break;

					if ((1 << i) & phys_state.pocketed != 0) continue;

					if (i == 0)
					{
						if (phys_state.balls_p[0].y < 0)
						{
							phys_state.balls_p[0].y = 0;
							phys_state.balls_p[0].y = -phys_state.balls_p[0].y * 0.5f; // bounce with restitution
						}

						float3 deltaPos = calculate_delta_position();
						phys_state.balls_p[0] += deltaPos;
					}
					else
					{
						phys_state.balls_v[i].y = 0;
						phys_state.balls_p[i].y = 0;

						float3 deltaPos = phys_state.balls_v[i] * FIXED_TIME_STEP;
						phys_state.balls_p[i] += deltaPos;
					}
					 
					step_one_ball(i);
				}

				return encode(pos);
            }
            ENDCG
        }
    }
}
