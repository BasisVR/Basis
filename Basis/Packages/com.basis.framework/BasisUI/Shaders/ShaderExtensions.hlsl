#ifndef SHADER_THING_INCLUDED
#define SHADER_THING_INCLUDED


// From: https://www.shadertoy.com/view/ttcyRS, credits to Inigo Quilez
void OklabLerp_float(float3 colA, float3 colB, float h, out float3 Out)
{
    // https://bottosson.github.io/posts/oklab
    const float3x3 kCONEtoLMS = float3x3(
         0.4121656120,  0.5362752080,  0.0514575653,
         0.2118591070,  0.6807189584,  0.1074065790,
         0.0883097947,  0.2818474174,  0.6302613616);
    const float3x3 kLMStoCONE = float3x3(
         4.0767245293, -3.3072168827,  0.2307590544,
        -1.2681437731,  2.6093323231, -0.3411344290,
        -0.0041119885, -0.7034763098,  1.7068625689);

    // rgb to cone (arg of pow can't be negative)
    float3 lmsA = pow(mul(kCONEtoLMS, colA), (1.0 / 3.0).xxx);
    float3 lmsB = pow(mul(kCONEtoLMS, colB), (1.0 / 3.0).xxx);
    // lerp
    float3 lms = lerp(lmsA, lmsB, h);
    // gain in the middle (no oaklab anymore, but looks better?)
    // lms *= 1.0+0.2*h*(1.0-h);
    // cone to rgb
    Out = mul(kLMStoCONE, lms * lms * lms);
}

void Circle_float(float2 UV, float2 Position, float Radius, out float Circle)
{
    float sdf = distance(Position, UV) - Radius;
    Circle = 1 - saturate(sdf / fwidth(sdf));
}

void GradientCircle_float(float2 UV, float2 Position, float Radius, float Radians, float4 Color1, float4 Color2, out float4 Out)
{
    float circle;
    Circle_float(UV, Position, Radius, circle);

    float2 gradientDir = float2(cos(Radians),-sin(Radians));
    float gradientVal = saturate(dot(UV - Position, gradientDir) / Radius / 2 + 0.5);

    float4 gradient;
    OklabLerp_float(Color1, Color2, gradientVal, gradient.rgb);
    gradient.a = lerp(Color1.a, Color2.a, gradientVal);

    Out = gradient * circle;
}

void CompositePremultiplied_float(float4 background, float4 foreground, out float4 Out)
{
    float4 bg = background;
    float4 fg = foreground;
    Out = float4(bg.rgb * (1 - fg.a) + fg.rgb, max(bg.a, fg.a));
}

#endif
