// One configurable surface look for every effect the planner describes: paint, water, lava, ice,
// fog, a starry ceiling ... Built from blocks instead of hard-coded effects:
//   colours (up to 3) mixed by a pattern (noise, cells/cracks, waves, stripes, speckles) at a scale,
//   movement (flowing in a direction, rippling, bubbling), glow (only where the pattern is bright),
//   reflection towards grazing angles, and edges where the surface meets real objects (from the
//   scene depth: foam, glow or frost). Alpha = coverage: with ML2 segmented dimming it decides how
//   much of the real surface is hidden. In Resources so it is always part of a build.
Shader "WhatIf/Surface"
{
    Properties
    {
        _Color1 ("Colour 1", Color) = (0.2, 0.4, 0.9, 1)
        _Color2 ("Colour 2", Color) = (0.1, 0.2, 0.6, 1)
        _Color3 ("Colour 3", Color) = (0.6, 0.8, 1.0, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.6
        _Pattern ("Pattern (0 none 1 noise 2 cells 3 waves 4 stripes 5 speckle)", Float) = 1
        _Scale ("Pattern size (m)", Float) = 0.5
        _Motion ("Motion (0 still 1 flow 2 ripple 3 bubble)", Float) = 0
        _Speed ("Speed", Float) = 0.2
        _FlowDir ("Flow direction (xz)", Vector) = (1, 0, 0, 0)
        _Glow ("Glow", Range(0, 1)) = 0
        _GlowColor ("Glow colour", Color) = (1, 0.5, 0.1, 1)
        _Reflect ("Reflection", Range(0, 1)) = 0
        _Edge ("Edge (0 none 1 foam 2 glow 3 frost)", Float) = 0
        _EdgeColor ("Edge colour", Color) = (1, 1, 1, 1)
        _EdgeWidth ("Edge width (m)", Float) = 0.06
        // Coats of one kind of surface (several detected floor planes) draw each pixel once:
        // overlapping planes made darker seams and broke the pattern at every plane's edge.
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp ("Stencil comparison", Float) = 8
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Surface"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Stencil { Ref 2 Comp [_StencilComp] Pass Replace }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color1, _Color2, _Color3, _GlowColor, _EdgeColor;
                float _Opacity, _Pattern, _Scale, _Motion, _Speed, _Glow, _Reflect, _Edge, _EdgeWidth;
                float4 _FlowDir;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float hash21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }

            float valueNoise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3 - 2 * f);
                return lerp(lerp(hash21(i), hash21(i + float2(1, 0)), u.x), lerp(hash21(i + float2(0, 1)), hash21(i + 1), u.x), u.y);
            }

            float fbm(float2 p) { return 0.5 * valueNoise(p) + 0.3 * valueNoise(p * 2.1 + 7.3) + 0.2 * valueNoise(p * 4.3 + 1.7); }

            // distance to the nearest cell border: small values = cracks between cells
            float cells(float2 p, float t)
            {
                float2 i = floor(p), f = frac(p);
                float d1 = 8, d2 = 8;
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    float2 g = float2(x, y);
                    float2 o = float2(hash21(i + g), hash21(i + g + 17.1));
                    o = 0.5 + 0.4 * sin(t + 6.2831 * o);
                    float d = length(g + o - f);
                    if (d < d1) { d2 = d1; d1 = d; } else if (d < d2) d2 = d;
                }
                return d2 - d1;
            }

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 ws = TransformObjectToWorld(input.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(input.normalOS);
                if (_Motion > 1.5 && _Motion < 2.5) // ripple: small waves on horizontal layers
                    ws += n * 0.01 * sin(ws.x * 3 + _Time.y * 2 * (_Speed + 0.1)) * cos(ws.z * 2.3 + _Time.y * 1.7 * (_Speed + 0.1));
                o.positionWS = ws;
                o.normalWS = n;
                o.positionCS = TransformWorldToHClip(ws);
                o.screenPos = ComputeScreenPos(o.positionCS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // pattern coordinates in the surface's own plane (world units / pattern size)
                float3 n = normalize(i.normalWS);
                float3 a = abs(n);
                float2 uv = a.y > 0.5 ? i.positionWS.xz : (a.x > a.z ? i.positionWS.zy : i.positionWS.xy);
                uv /= max(0.02, _Scale);
                float t = _Time.y * _Speed;
                if (_Motion > 0.5 && _Motion < 1.5) uv -= normalize(_FlowDir.xy + 1e-4) * t;      // flow
                float bubble = (_Motion > 2.5) ? 0.5 + 0.5 * sin(t * 6 + valueNoise(uv * 3) * 12) : 0;

                float v = 0.5;
                if (_Pattern > 0.5 && _Pattern < 1.5) v = fbm(uv + t * 0.3);
                else if (_Pattern > 1.5 && _Pattern < 2.5) v = 1 - saturate(cells(uv, t) * 6);     // 1 = crack
                else if (_Pattern > 2.5 && _Pattern < 3.5) v = 0.5 + 0.5 * sin(uv.x * 6.2831 + fbm(uv) * 3 + t * 3);
                else if (_Pattern > 3.5 && _Pattern < 4.5) v = step(0.5, frac(uv.x));
                else if (_Pattern > 4.5) v = step(0.93, hash21(floor(uv * 8))) * (0.6 + 0.4 * sin(t * 4 + hash21(floor(uv * 8)) * 30));
                v = saturate(v + bubble * 0.25);

                half3 col = v < 0.5 ? lerp(_Color2.rgb, _Color1.rgb, v * 2) : lerp(_Color1.rgb, _Color3.rgb, v * 2 - 1);
                // glow where the pattern is bright (lava cracks, stars)
                col += _GlowColor.rgb * _Glow * smoothstep(0.55, 1.0, v) * 1.5;
                // fake reflection towards grazing angles: lighter, like the sky
                float3 view = normalize(GetCameraPositionWS() - i.positionWS);
                float fres = pow(1 - saturate(abs(dot(view, n))), 3) * _Reflect;
                col = lerp(col, half3(0.85, 0.9, 1.0), fres);
                float alpha = saturate(_Opacity + fres * 0.3 + _Glow * smoothstep(0.55, 1.0, v) * 0.3);

                // edges where the surface meets real objects or walls (scene depth from occluders)
                if (_Edge > 0.5)
                {
                    float2 suv = i.screenPos.xy / i.screenPos.w;
                    float scene = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                    float self = LinearEyeDepth(i.positionCS.z, _ZBufferParams);
                    float e = 1 - saturate((scene - self) / max(0.005, _EdgeWidth));
                    e *= step(0.001, scene - self + 0.5);
                    if (_Edge < 1.5) e *= 0.6 + 0.4 * valueNoise(uv * 8 + t * 2);                 // foam
                    col = lerp(col, _EdgeColor.rgb * (_Edge > 1.5 && _Edge < 2.5 ? 1.6 : 1.0), e);
                    alpha = max(alpha, e * 0.9);
                }
                return half4(col, alpha);
            }
            ENDHLSL
        }
    }
}
