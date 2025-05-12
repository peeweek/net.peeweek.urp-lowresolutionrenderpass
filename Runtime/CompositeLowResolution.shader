Shader "LowResolution/CompositeLowResolution"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"}

        Pass
        {
            Name "CompositeLowResolution"
            Blend One OneMinusSrcAlpha
            ZTest Always
            ZWrite Off
            Cull Off
            
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Fragment
            #pragma shader_feature_local BLUR_OUTPUT

            // Core.hlsl for XR dependencies
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Debug/DebuggingFullscreen.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            SAMPLER(sampler_BlitTexture);

            half4 Fragment(Varyings input) : SV_Target
            {
                float2 uv = input.texcoord;

                uint width;
                uint height;
                uint levels;
                _BlitTexture.GetDimensions(0, width, height, levels);

                float2 dduv = 1.0/ float2(width,height);


                float4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv);
#if BLUR_OUTPUT
                col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + dduv * float2(0,1));
                col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + dduv * float2(1,1));
                col += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_BlitTexture, uv + dduv * float2(1,0));
                col *= 0.25;
#endif
                col.a = saturate(col.a);
                return (col);
            }
            ENDHLSL
        }
    }
}
