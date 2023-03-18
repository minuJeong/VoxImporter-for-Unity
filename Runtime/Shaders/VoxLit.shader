Shader "Lit/VoxLit"
{
	HLSLINCLUDE
	#pragma target 4.0

	#pragma shader_feature DYNAMICLIGHTMAP_ON
	#pragma shader_feature LIGHTMAP_ON
	#pragma shader_feature DEBUG_DISPLAY

	#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Macros.hlsl"
	#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"
	#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
	#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceData.hlsl"
	#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/UnityGBuffer.hlsl"

	Texture3D<half4> _VoxelTex;
	SamplerState sampler_VoxelTex;

	Texture2D<half4> _GradientTex;
	SamplerState sampler_GradientTex;

	cbuffer UnityPerMaterial
	{
		uint _MaxStepsExp;
		float _Alpha;
		float _GradientDecay;
	}

	struct Attributes
	{
		float4 position: POSITION;
		float4 normal : NORMAL;
	};

	struct Varyings
	{
		half4 positionCS: SV_Position;
		half4 normalWS : Normal;
		half4 tangentWS : Texcoord5;
		half4 positionWS : Texcoord0;
		float3 positionOS : Texcoord6;
		float4 ray : Texcoord7;
		half4 fogFactorAndVertexLight : Texcoord2;
		DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 3);
#ifdef DYNAMICLIGHTMAP_ON
		float2  dynamicLightmapUV : Texcoord4;
#endif
	};

	Varyings Vert(Attributes input)
	{
		VertexPositionInputs positionInputs = GetVertexPositionInputs(input.position.xyz);
		VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normal.xyz);
		const half3 vertexLight = VertexLighting(positionInputs.positionWS, normalInputs.normalWS);
		const half fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
		const float3 deltaCamera = positionInputs.positionWS.xyz - _WorldSpaceCameraPos.xyz;
		
		Varyings output = (Varyings)0;
		output.positionCS = positionInputs.positionCS;
		output.positionOS.xyz = input.position.xyz;
		output.ray.xyz = TransformWorldToObject(deltaCamera);
		output.positionWS.xyz = positionInputs.positionWS.xyz;
		output.normalWS.xyz = normalInputs.normalWS.xyz;
		output.tangentWS.xyz = normalInputs.tangentWS.xyz;
		output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
		output.ray.w = length(deltaCamera);
		return output;
	}

	inline float Hash(float2 uv)
	{
	    return frac(sin(dot(uv, float2(12.9898, 78.233)))*43758.5453);
	}

	half4 MarchVoxel(Varyings input)
	{
		const half maxSteps = pow(2.0h, _MaxStepsExp);
		const float step = rcp(maxSteps * 0.5h);
		
		const float3 origin = input.positionOS;
		const float3 ray = normalize(input.ray.xyz);

		half4 color = half4(0.0h, 0.0h, 0.0h, 0.0h);
		float travel = 0.0f;
		float decay = 1.0f;
		[loop] for (half i = 0.0h; i < maxSteps; ++i)
		{
			const float3 pos = origin + ray * travel;
			travel += step;
			
			const float3 absPos = abs(pos);
			if (max(absPos.x, max(absPos.y, absPos.z)) > 0.5f) break;
			
			half4 voxelTex = _VoxelTex.SampleGrad(
				sampler_VoxelTex, pos + 0.5h, ddx(pos), ddy(pos)
			);
			if (voxelTex.w <= 0.0h) continue;

			voxelTex.w *= _Alpha;
			color.xyz += (1.0h - color.w) * voxelTex.xyz * voxelTex.w;
			color.w += (1.0h - color.w) * voxelTex.w;
			decay *= _GradientDecay;

			if (color.w >= 1.0h) break;
		}

		half4 gradientTex = _GradientTex.Sample(sampler_GradientTex, half2(decay * 0.5h, 0.5h));
		color.xyz *= gradientTex.xyz * gradientTex.w;
		return color;
	}

	half4 ForwardFrag(Varyings input): SV_Target
	{
		clip(0.5h - smoothstep(2.1h, 2.0h, input.ray.w - Hash(GetNormalizedScreenSpaceUV(input.positionCS))));
		return MarchVoxel(input);
	}
	ENDHLSL
	
	Properties
	{
		_VoxelTex ("Voxel", 3D) = "black" {}
		_MaxStepsExp ("Max Steps", Range(5, 10)) = 5
		_GradientTex ("Gradient", 2D) = "white" {}
		_GradientDecay ("Gradient Decay", Range(0.0, 1.0)) = 0.99
		_Alpha ("Alpha", Range(0.0, 1.0)) = 1.0
	}
	SubShader
	{
		Tags
		{
			"RenderType" = "Transparent"
			"Queue" = "Transparent"
			"RenderPipeline" = "UniversalPipeline"
		}
		LOD 100
		ZWrite Off
		Blend SrcAlpha OneMinusSrcAlpha

		Pass
		{
			Name "Forward Lit"
			Tags
			{
				"LightMode" = "UniversalForwardOnly"
			}
			
			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment ForwardFrag
			ENDHLSL
		}
	}
}