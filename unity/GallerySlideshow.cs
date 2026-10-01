using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.Image;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// สไลด์โชว์ที่ดึงรูปจาก GitHub Pages มาแสดงบนจอหลายจอพร้อมกัน
/// อ่าน config.json เพื่อเอา count / duration / loop แล้วค่อยไล่โหลดรูปทีละใบ
///
/// ใช้ script ตัวเดียวคุมได้หลายจอ — ลากจอทั้งหมดใส่ช่อง Screens
/// รูปโหลดชุดเดียวแล้วส่งให้ทุกจอผ่าน MaterialPropertyBlock
/// แต่ละจอจึงมีสัดส่วนของตัวเองได้ ทั้งที่ใช้ material ใบเดียวกัน
///
/// สคริปต์ไม่แตะ transform ของจอเลย การจัดรูปเป็นหน้าที่ของ Fit Mode บน material
/// หมายเหตุ: แต่ละคนในเวิลด์โหลดเองแยกกัน ภาพที่เห็นจึงอาจไม่ตรงกัน
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GallerySlideshow : UdonSharpBehaviour
{
    [Header("URL — ก๊อปจากหน้า gallery/index.html")]
    [Tooltip(".../gallery/config.json")]
    [SerializeField] private VRCUrl configUrl;

    [Tooltip(".../gallery/images/0.png, 1.png, ... เรียงตามลำดับ ช่องที่ไม่ใช้เว้นว่างได้")]
    [SerializeField] private VRCUrl[] imageUrls;

    [Header("จอแสดงผล")]
    [Tooltip("ลากจอทุกจอมาใส่ได้เลย ทุกจอจะฉายภาพเดียวกันพร้อมกัน\n" +
             "เว้นว่างไว้ = ใช้ Mesh Renderer ของตัวเอง")]
    [SerializeField] private MeshRenderer[] screens;

    [Tooltip("ระยะเวลาเฟดข้ามภาพ (วินาที)")]
    [SerializeField] private float fadeTime = 1.5f;

    [Tooltip("วัดสัดส่วนของแต่ละจอแล้วส่งให้เชดเดอร์ (แนะนำเปิด)\n" +
             "ปิดถ้าอยากตั้ง Aspect Mode / Surface Aspect เองบน material")]
    [SerializeField] private bool driveShaderAspect = true;

    [Header("ค่าสำรอง — ใช้เมื่ออ่าน config.json ไม่ได้")]
    [SerializeField] private float fallbackDuration = 10f;

    // ต้องเก็บ downloader ไว้เป็น field ไม่งั้นโดน garbage collector เก็บกลางคัน
    private VRCImageDownloader _downloader;

    private Texture2D[] _textures;
    private float[]     _aspects;   // สัดส่วนของรูปแต่ละใบ กันพึ่ง _TexelSize
    private int   _count;           // จำนวนรูปที่ config.json บอก
    private int   _fetched;         // ยิง request ไปแล้วกี่ใบ
    private int   _ready;           // โหลดสำเร็จแล้วกี่ใบ
    private float _duration;
    private bool  _loop = true;

    private int   _cur = -1;        // index ที่กำลังแสดง
    private int   _next;
    private float _timer;
    private bool  _fading;
    private bool  _finished;

    private MaterialPropertyBlock _mpb;
    private float[] _screenAspect;  // สัดส่วนของแต่ละจอ วัดครั้งเดียวตอน Start

    // สถานะที่จะส่งให้ทุกจอ
    private Texture2D _texA, _texB;
    private float     _aspA, _aspB, _blend;

    void Start()
    {
        _duration   = fallbackDuration;
        _textures   = new Texture2D[imageUrls.Length];
        _aspects    = new float[imageUrls.Length];
        _downloader = new VRCImageDownloader();
        _mpb        = new MaterialPropertyBlock();

        SetupScreens();
        PushToScreens();

        if (HasUrl(configUrl))
        {
            VRCStringDownloader.LoadUrl(configUrl, (IUdonEventReceiver)this);
        }
        else
        {
            // ไม่ได้ใส่ config ก็โหลดทุกช่องที่กรอกไว้
            _count = imageUrls.Length;
            FetchNext();
        }
    }

    void OnDestroy()
    {
        if (_downloader != null) _downloader.Dispose();
    }

    // ---------- จอ ----------

    private void SetupScreens()
    {
        // ไม่ได้ลากจอมาใส่ ก็ใช้ renderer ของตัวเอง
        if (screens == null || screens.Length == 0)
        {
            MeshRenderer self = GetComponent<MeshRenderer>();
            if (self != null) screens = new MeshRenderer[] { self };
            else              screens = new MeshRenderer[0];
        }

        _screenAspect = new float[screens.Length];
        for (int i = 0; i < screens.Length; i++)
        {
            _screenAspect[i] = (screens[i] != null) ? MeasureAspect(screens[i].transform) : 1f;
        }
    }

    /// วัดสัดส่วนจริงของจอจากขนาดเมช x สเกลจริง
    /// ดีกว่าให้เชดเดอร์เดาเอง เพราะเมชไม่จำเป็นต้องเป็น Quad 1x1
    private float MeasureAspect(Transform t)
    {
        Vector2 mesh = Vector2.one;
        bool    isXZ = false;

        MeshFilter mf = t.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            Vector3 b = mf.sharedMesh.bounds.size;

            // เมชแบนบน XZ (เช่น Plane) จะมีความหนาแกน Y เกือบศูนย์
            if (b.y < 0.0001f && b.z > 0.0001f) { isXZ = true; mesh = new Vector2(b.x, b.z); }
            else                                {              mesh = new Vector2(b.x, b.y); }

            if (mesh.x < 0.00001f) mesh.x = 1f;
            if (mesh.y < 0.00001f) mesh.y = 1f;
        }

        Vector3 sc = t.lossyScale;
        float w = sc.x * mesh.x;
        float h = (isXZ ? sc.z : sc.y) * mesh.y;

        return (h > 0.0001f) ? (w / h) : 1f;
    }

    /// ส่งภาพและค่าต่าง ๆ ให้ทุกจอ ใช้ MaterialPropertyBlock จึงไม่ไปแก้ material asset
    private void PushToScreens()
    {
        if (screens == null) return;

        for (int i = 0; i < screens.Length; i++)
        {
            MeshRenderer r = screens[i];
            if (r == null) continue;

            r.GetPropertyBlock(_mpb);

            if (_texA != null) _mpb.SetTexture("_TexA", _texA);
            if (_texB != null) _mpb.SetTexture("_TexB", _texB);

            _mpb.SetFloat("_TexAAspect", _aspA);
            _mpb.SetFloat("_TexBAspect", _aspB);
            _mpb.SetFloat("_Blend", _blend);

            if (driveShaderAspect)
            {
                _mpb.SetFloat("_AspectMode", 2f);   // Manual
                _mpb.SetFloat("_SurfaceAspect", _screenAspect[i]);
            }

            r.SetPropertyBlock(_mpb);
        }
    }

    // ---------- config.json ----------

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        ParseConfig(result.Result);
        FetchNext();
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        Debug.LogWarning("[Gallery] โหลด config.json ไม่ได้: " + result.Error + " — ใช้ค่าสำรองแทน");
        _count = imageUrls.Length;
        FetchNext();
    }

    private void ParseConfig(string raw)
    {
        _count = imageUrls.Length;

        DataToken json;
        if (!VRCJson.TryDeserializeFromJson(raw, out json) || json.TokenType != TokenType.DataDictionary)
        {
            Debug.LogWarning("[Gallery] config.json อ่านไม่ออก — ใช้ค่าสำรองแทน");
            return;
        }

        DataDictionary cfg = json.DataDictionary;
        DataToken t;

        if (cfg.TryGetValue("count", out t))
        {
            _count = Mathf.Clamp((int)AsNumber(t, imageUrls.Length), 0, imageUrls.Length);
        }

        if (cfg.TryGetValue("duration", out t))
        {
            float d = (float)AsNumber(t, fallbackDuration);
            if (d > 0.1f) _duration = d;
        }

        if (cfg.TryGetValue("loop", out t) && t.TokenType == TokenType.Boolean)
        {
            _loop = t.Boolean;
        }
    }

    /// VRCJson คืนตัวเลขมาเป็น Double เป็นหลัก แต่เผื่อชนิดอื่นไว้ด้วย
    private double AsNumber(DataToken t, double fallback)
    {
        if (t.TokenType == TokenType.Double) return t.Double;
        if (t.TokenType == TokenType.Float)  return t.Float;
        if (t.TokenType == TokenType.Int)    return t.Int;
        if (t.TokenType == TokenType.Long)   return t.Long;
        return fallback;
    }

    // ---------- โหลดรูป ----------

    /// ยิงทีละใบ แล้วค่อยยิงใบถัดไปตอน callback กลับมา
    /// (VRChat จำกัด 1 รูป / 5 วินาทีอยู่แล้ว การยิงเรียงกันช่วยคุมลำดับภาพ)
    private void FetchNext()
    {
        while (_fetched < _count)
        {
            VRCUrl u = imageUrls[_fetched];
            if (HasUrl(u))
            {
                TextureInfo info = new TextureInfo();
                info.GenerateMipMaps = true;

                _downloader.DownloadImage(u, null, (IUdonEventReceiver)this, info);
                _fetched++;
                return;
            }
            _fetched++;   // ช่องว่าง ข้ามไป
        }
    }

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
        if (_ready < _textures.Length)
        {
            Texture2D tex = result.Result;
            _textures[_ready] = tex;
            _aspects[_ready]  = (tex != null && tex.height > 0)
                              ? ((float)tex.width / (float)tex.height) : 1f;
            _ready++;
        }

        if (_cur < 0) ShowFirst();   // ได้ใบแรกก็เริ่มฉายเลย ไม่ต้องรอครบ
        ReportIfDone();
        FetchNext();
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
        // _fetched ถูกบวกไปแล้วตอนยิง request ใบนี้
        Debug.LogWarning("[Gallery] ช่องที่ " + (_fetched - 1) + " โหลดไม่สำเร็จ: " + result.Error);
        ReportIfDone();
        FetchNext();
    }

    /// สรุปผลให้ดูใน Console ตอนยิงครบทุกใบแล้ว
    private void ReportIfDone()
    {
        if (_fetched < _count) return;

        if (_ready == _count)
        {
            Debug.Log("[Gallery] โหลดครบ " + _ready + " ใบ ฉายบน " + screens.Length + " จอ");
        }
        else
        {
            Debug.LogWarning("[Gallery] โหลดได้ " + _ready + " จาก " + _count +
                             " ใบ — จะวนเฉพาะใบที่โหลดสำเร็จ");
        }
    }

    // ---------- แสดงผล ----------

    private void ShowFirst()
    {
        _cur    = 0;
        _timer  = 0f;
        _fading = false;

        _texA  = _textures[0];
        _aspA  = _aspects[0];
        _blend = 0f;
        PushToScreens();
    }

    void Update()
    {
        if (_cur < 0 || _finished) return;

        _timer += Time.deltaTime;

        if (!_fading)
        {
            if (_timer < _duration) return;

            // ยิง request ครบแล้วให้ยึดจำนวนที่โหลดสำเร็จจริง ไม่ใช่ count ใน config
            // ไม่งั้นถ้ามีใบไหนโหลดพลาด สไลด์จะค้างรอรูปที่ไม่มีวันมาแล้ววนกลับไม่ได้
            bool doneFetching = _fetched >= _count;
            int  total        = doneFetching ? _ready : _count;
            if (total <= 0) return;

            int n = _cur + 1;
            if (n >= total)
            {
                if (!_loop) { _finished = true; return; }
                n = 0;
            }

            // ใบถัดไปยังโหลดไม่เสร็จ ค้างภาพเดิมไว้ก่อน
            if (n >= _ready) { _timer = _duration; return; }

            _next  = n;
            _texB  = _textures[_next];
            _aspB  = _aspects[_next];
            _blend = 0f;
            PushToScreens();

            _fading = true;
            _timer  = 0f;
            return;
        }

        _blend = (fadeTime <= 0f) ? 1f : Mathf.Clamp01(_timer / fadeTime);

        if (_blend >= 1f)
        {
            _cur   = _next;
            _texA  = _textures[_cur];
            _aspA  = _aspects[_cur];
            _blend = 0f;
            _fading = false;
            _timer  = 0f;
        }

        PushToScreens();
    }

    private bool HasUrl(VRCUrl u)
    {
        return u != null && !string.IsNullOrEmpty(u.Get());
    }
}
