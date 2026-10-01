// เชดเดอร์สำหรับจอสไลด์โชว์
// - เฟดข้ามระหว่างสองรูปด้วย _Blend (0 = TexA, 1 = TexB)
// - ย่อรูปให้พอดีจอแบบไม่ยืด (contain fit) ส่วนที่เหลือเป็นสีดำ
//   จึงผสมรูปแนวตั้งกับแนวนอนในชุดเดียวกันได้
Shader "INS/GalleryCrossfade"
{
    Properties
    {
        _TexA   ("Texture A", 2D) = "black" {}
        _TexB   ("Texture B", 2D) = "black" {}
        _Blend  ("Blend A to B", Range(0, 1)) = 0
        _SurfaceAspect ("Surface Aspect (กว้าง/สูง ของจอ)", Float) = 1.7778
        _Brightness    ("Brightness", Range(0, 2)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float2 uv : TEXCOORD0; float4 pos : SV_POSITION; };

            sampler2D _TexA; float4 _TexA_TexelSize;
            sampler2D _TexB; float4 _TexB_TexelSize;
            float _Blend;
            float _SurfaceAspect;
            float _Brightness;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            // texel.zw = (กว้าง, สูง) ของเท็กซ์เจอร์ Unity ใส่ให้อัตโนมัติ
            float3 SampleFit(sampler2D tex, float4 texel, float2 uv)
            {
                float texAspect = texel.z / max(texel.w, 1.0);
                float surf      = max(_SurfaceAspect, 0.0001);

                float2 p = uv - 0.5;
                if (texAspect > surf) p.y *= texAspect / surf;   // รูปแบนกว่าจอ -> คาดดำบน/ล่าง
                else                  p.x *= surf / texAspect;   // รูปสูงกว่าจอ -> คาดดำซ้าย/ขวา
                p += 0.5;

                float inside = step(0.0, p.x) * step(p.x, 1.0)
                             * step(0.0, p.y) * step(p.y, 1.0);

                return tex2D(tex, saturate(p)).rgb * inside;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 a = SampleFit(_TexA, _TexA_TexelSize, i.uv);
                float3 b = SampleFit(_TexB, _TexB_TexelSize, i.uv);
                float3 c = lerp(a, b, saturate(_Blend)) * _Brightness;
                return fixed4(c, 1.0);
            }
            ENDCG
        }
    }

    FallBack "Unlit/Texture"
}
