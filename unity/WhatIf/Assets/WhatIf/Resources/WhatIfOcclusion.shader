// Invisible occluder for the real room: writes depth, no colour. Drawn before everything else, so
// virtual content behind real walls and furniture is hidden; with segmented dimming the untouched
// pixels stay see-through (alpha 0). Pushed back a few cm away from the viewer, so mesh noise
// doesn't cut into paint, shadows or objects standing right on a surface.
// In Resources so it is always part of a build.
Shader "WhatIf/Occlusion"
{
    Properties
    {
        _PushBack ("Push back from the viewer (m)", Float) = 0.03
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry-10" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        CBUFFER_START(UnityPerMaterial)
            float _PushBack;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings vert(Attributes input)
        {
            Varyings o;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float3 world = TransformObjectToWorld(input.positionOS.xyz);
            world += normalize(world - GetCameraPositionWS()) * _PushBack;
            o.positionCS = TransformWorldToHClip(world);
            return o;
        }

        half4 frag(Varyings i) : SV_Target { return 0; }
        ENDHLSL

        Pass
        {
            Name "Occlusion"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }
}
