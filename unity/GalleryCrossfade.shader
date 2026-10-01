// เชดเดอร์สำหรับจอสไลด์โชว์
// - เฟดข้ามระหว่างสองรูปด้วย _Blend (0 = TexA, 1 = TexB)
// - ย่อรูปให้พอดีจอแบบไม่ยืด (contain fit) ส่วนที่เหลือเป็นสีดำ
//   จึงผสมรูปแนวตั้งกับแนวนอนในชุดเดียวกันได้
// - คำนวณสัดส่วนจอเองจาก Scale ของออบเจกต์ ไม่ต้องกรอกมือ
Shader "Paradise/GalleryCrossFade"
{
    Properties
    {
        _TexA   ("Texture A", 2D) = "black" {}
        _TexB   ("Texture B", 2D) = "black" {}
        _Blend  ("Blend A to B", Range(0, 1)) = 0

        [Enum(AutoQuad,0,AutoPlane,1,Manual,2)]
        _AspectMode ("Aspect Mode", Float) = 0

        _SurfaceAspect ("Surface Aspect (ใช้เมื่อเลือก Manual)", Float) = 1.7778
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

            struct v2f
            {
                float2 uv   : TEXCOORD0;
                float  surf : TEXCOORD1;   // สัดส่วนจอ กว้าง/สูง
                float4 pos  : SV_POSITION;
            };

            sampler2D _TexA; float4 _TexA_TexelSize;
            sampler2D _TexB; float4 _TexB_TexelSize;
            float _Blend;
            float _AspectMode;
            float _SurfaceAspect;
            float _Brightness;

            // ความยาวแกนของออบเจกต์ในพิกัดโลก = สเกลจริงหลังคิด parent ทั้งสาย
            float3 ObjAxis(int i)
            {
                return float3(unity_ObjectToWorld[0][i],
                              unity_ObjectToWorld[1][i],
                              unity_ObjectToWorld[2][i]);
            }

            float SurfaceAspect()
            {
                if (_AspectMode > 1.5) return _SurfaceAspect;   // Manual

                float sx = length(ObjAxis(0));
                float sy = length(ObjAxis(1));
                float sz = length(ObjAxis(2));

                // Quad กางอยู่บนระนาบ XY ส่วน Plane กางอยู่บนระนาบ XZ
                float denom = (_AspectMode > 0.5) ? sz : sy;
                return sx / max(denom, 1e-5);
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                o.uv   = v.uv;
                o.surf = SurfaceAspect();
                return o;
            }

            // texel.zw = (กว้าง, สูง) ของเท็กซ์เจอร์ Unity ใส่ให้อัตโนมัติ
            float3 SampleFit(sampler2D tex, float4 texel, float2 uv, float surf)
            {
                float texAspect = texel.z / max(texel.w, 1.0);
                surf = max(surf, 0.0001);

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
                float3 a = SampleFit(_TexA, _TexA_TexelSize, i.uv, i.surf);
                float3 b = SampleFit(_TexB, _TexB_TexelSize, i.uv, i.surf);
                float3 c = lerp(a, b, saturate(_Blend)) * _Brightness;
                return fixed4(c, 1.0);
            }
            ENDCG
        }
    }

    FallBack "Unlit/Texture"
}
