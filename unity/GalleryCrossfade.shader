// เชดเดอร์สำหรับจอสไลด์โชว์
// - เฟดข้ามระหว่างสองรูปด้วย _Blend (0 = TexA, 1 = TexB)
// - จัดรูปให้เข้ากับจอได้ 3 แบบผ่าน _FitMode โดยไม่ต้องแตะ transform
// - คำนวณสัดส่วนจอเองจาก Scale ของออบเจกต์ หรือรับค่าตรง ๆ จากสคริปต์
Shader "Paradise/GalleryCrossFade"
{
    Properties
    {
        _TexA   ("Texture A", 2D) = "black" {}
        _TexB   ("Texture B", 2D) = "black" {}
        _Blend  ("Blend A to B", Range(0, 1)) = 0

        // Contain= เห็นรูปครบทั้งใบ มีขอบดำ  <- ค่าเริ่มต้น
        // Cover  = เต็มจอ ไม่มีขอบดำ แต่ส่วนที่ล้นโดนตัด
        // Stretch= ยืดจนเต็ม รูปเบี้ยว
        [Enum(Contain,0,Cover,1,Stretch,2)]
        _FitMode ("Fit Mode", Float) = 0

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
            float _FitMode;
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
                // Stretch: ยืดตรง ๆ ไม่ต้องคิดอะไร
                if (_FitMode > 1.5) return tex2D(tex, uv).rgb;

                float texAspect = texel.z / max(texel.w, 1.0);
                surf = max(surf, 0.0001);

                float2 p = uv - 0.5;

                if (_FitMode < 0.5)
                {
                    // Contain: ย่อรูปให้อยู่ในจอทั้งใบ ที่เหลือเป็นขอบดำ
                    if (texAspect > surf) p.y *= texAspect / surf;
                    else                  p.x *= surf / texAspect;
                    p += 0.5;

                    float inside = step(0.0, p.x) * step(p.x, 1.0)
                                 * step(0.0, p.y) * step(p.y, 1.0);
                    return tex2D(tex, saturate(p)).rgb * inside;
                }

                // Cover: ขยายรูปจนคลุมทั้งจอ ส่วนที่ล้นออกไปโดนตัด ไม่มีขอบดำ
                if (texAspect > surf) p.x *= surf / texAspect;   // รูปแบนกว่าจอ -> ตัดซ้าย/ขวา
                else                  p.y *= texAspect / surf;   // รูปสูงกว่าจอ -> ตัดบน/ล่าง
                p += 0.5;

                return tex2D(tex, saturate(p)).rgb;
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
