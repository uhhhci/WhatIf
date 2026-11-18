// Planar shadow: a 3D model's copy flattened onto the floor along the room's main light direction
// (Magic Leap light estimation), drawn as black with alpha - on the Magic Leap 2 the alpha darkens the
// real floor through segmented dimming. Independent of Unity's lights: the objects keep their standard
// lighting, only this shadow follows the room's light. A stencil keeps overlapping parts (and the
// shadows of several objects) from darkening twice. Real walls (depth occluders) hide shadows behind
// them. Set by PlanarShadows: _WhatIfShadowDir (direction the light travels), _WhatIfFloorY,
// _WhatIfShadowStrength. In Resources so it is always part of a build.
Shader "WhatIf/PlanarShadow"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-40" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "PlanarShadow"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            Stencil
            {
                Ref 64
                ReadMask 64
                WriteMask 64
                Comp NotEqual
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 _WhatIfShadowDir;      // xyz: direction the light travels (y < 0)
            float _WhatIfFloorY;
            float _WhatIfShadowStrength;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float height : TEXCOORD0;   // how far above the floor the original point was
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 w = TransformObjectToWorld(input.positionOS.xyz);
                float3 d = normalize(_WhatIfShadowDir.xyz);
                d.y = min(d.y, -0.2);                      // never along the floor
                float h = w.y - _WhatIfFloorY;
                float3 p = w + d * (max(h, 0.0) / -d.y);   // along the light down to the floor
                p.y = _WhatIfFloorY + 0.006;               // just above it
                o.height = h;
                o.positionCS = TransformWorldToHClip(p);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                clip(i.height + 0.02);                     // parts below the floor cast nothing
                return half4(0, 0, 0, _WhatIfShadowStrength);
            }
            ENDHLSL
        }
    }
}
