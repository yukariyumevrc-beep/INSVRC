using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.Image;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// สไลด์โชว์ที่ดึงรูปจาก GitHub Pages มาแสดงบนวัสดุ (material)
/// อ่าน config.json เพื่อเอา count / duration / loop แล้วค่อยไล่โหลดรูปทีละใบ
///
/// หมายเหตุ: แต่ละคนในเวิลด์โหลดเองแยกกัน ภาพที่เห็นจึงอาจไม่ตรงกัน
/// ถ้าต้องการให้ทุกคนเห็นภาพเดียวกันพร้อมกันต้องเพิ่มการ sync ทีหลัง
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GallerySlideshow : UdonSharpBehaviour
{
    [Header("URL — ก๊อปจากหน้า gallery/index.html")]
    [Tooltip(".../gallery/config.json")]
    [SerializeField] private VRCUrl configUrl;

    [Tooltip(".../gallery/images/0.jpg, 1.jpg, ... เรียงตามลำดับ ช่องที่ไม่ใช้เว้นว่างได้")]
    [SerializeField] private VRCUrl[] imageUrls;

    [Header("จอแสดงผล")]
    [Tooltip("material ที่ใช้เชดเดอร์ INS/GalleryCrossfade")]
    [SerializeField] private Material targetMat;

    [Tooltip("ระยะเวลาเฟดข้ามภาพ (วินาที)")]
    [SerializeField] private float fadeTime = 1.5f;

    [Header("ค่าสำรอง — ใช้เมื่ออ่าน config.json ไม่ได้")]
    [SerializeField] private float fallbackDuration = 10f;

    // ต้องเก็บ downloader ไว้เป็น field ไม่งั้นโดน garbage collector เก็บกลางคัน
    private VRCImageDownloader _downloader;

    private Texture2D[] _textures;
    private int   _count;      // จำนวนรูปที่ config.json บอก
    private int   _fetched;    // ยิง request ไปแล้วกี่ใบ
    private int   _ready;      // โหลดสำเร็จแล้วกี่ใบ
    private float _duration;
    private bool  _loop = true;

    private int   _cur = -1;   // index ที่กำลังแสดง
    private int   _next;
    private float _timer;
    private bool  _fading;
    private bool  _finished;

    void Start()
    {
        _duration   = fallbackDuration;
        _textures   = new Texture2D[imageUrls.Length];
        _downloader = new VRCImageDownloader();

        if (targetMat != null) targetMat.SetFloat("_Blend", 0f);

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
            _textures[_ready] = result.Result;
            _ready++;
        }

        if (_cur < 0) ShowFirst();   // ได้ใบแรกก็เริ่มฉายเลย ไม่ต้องรอครบ
        FetchNext();
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
        Debug.LogWarning("[Gallery] โหลดรูปไม่สำเร็จ: " + result.Error);
        FetchNext();
    }

    // ---------- แสดงผล ----------

    private void ShowFirst()
    {
        _cur    = 0;
        _timer  = 0f;
        _fading = false;
        targetMat.SetTexture("_TexA", _textures[0]);
        targetMat.SetFloat("_Blend", 0f);
    }

    void Update()
    {
        if (_cur < 0 || _finished || targetMat == null) return;

        _timer += Time.deltaTime;

        if (!_fading)
        {
            if (_timer < _duration) return;

            int n = _cur + 1;
            if (n >= _count)
            {
                if (!_loop) { _finished = true; return; }
                n = 0;
            }

            // ใบถัดไปยังโหลดไม่เสร็จ ค้างภาพเดิมไว้ก่อน
            if (n >= _ready) { _timer = _duration; return; }

            _next = n;
            targetMat.SetTexture("_TexB", _textures[_next]);
            _fading = true;
            _timer  = 0f;
            return;
        }

        float k = (fadeTime <= 0f) ? 1f : Mathf.Clamp01(_timer / fadeTime);
        targetMat.SetFloat("_Blend", k);

        if (k >= 1f)
        {
            _cur = _next;
            targetMat.SetTexture("_TexA", _textures[_cur]);
            targetMat.SetFloat("_Blend", 0f);
            _fading = false;
            _timer  = 0f;
        }
    }

    private bool HasUrl(VRCUrl u)
    {
        return u != null && !string.IsNullOrEmpty(u.Get());
    }
}
