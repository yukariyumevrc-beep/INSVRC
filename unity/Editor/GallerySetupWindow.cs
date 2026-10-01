#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// เครื่องมือช่วยตั้งค่า GallerySlideshow — เติม URL ทุกช่องให้ในคลิกเดียว
/// เปิดจากเมนู  Paradise > Gallery Setup
///
/// ไฟล์นี้อยู่ในโฟลเดอร์ Editor จึงไม่ถูกรวมเข้าไปในเวิลด์ตอน build
/// </summary>
public class GallerySetupWindow : EditorWindow
{
    // ฐานของเว็บทั้งหมด ไม่ใช่แค่โฟลเดอร์ gallery เพราะบางแกลเลอรีอยู่นอกโฟลเดอร์นั้น
    private const string SiteBase = "https://yukariyumevrc-beep.github.io/INSVRC/";
    private const string GalleryDir = "Paradise/paradise_central_control/gallery/";

    // ชื่อที่แสดง / ที่อยู่เทียบกับ SiteBase
    private static readonly string[] Shortcuts = new string[]
    {
        "Paradise ตั้ง",  GalleryDir + "Paradise/paradise_vertical",
        "Paradise นอน",   GalleryDir + "Paradise/paradise_wide",
        "GoldenAge ตั้ง", "Paradise/golden-age/Gallery/goldenage_vertical",
        "GoldenAge นอน",  "Paradise/golden-age/Gallery/goldenage_wide",
        "Partner ตั้ง",   GalleryDir + "Partner/partner_vertical",
        "Partner นอน",    GalleryDir + "Partner/partner_wide",
    };

    private string _galleryUrl = SiteBase + GalleryDir + "Paradise/paradise_vertical/";
    private int    _slots      = 32;   // ช่องว่างไม่กิน VRAM โหลดเฉพาะเท่าที่ config บอก
    private int    _startIndex = 0;
    private int    _extIndex   = 0;                                   // 0 = png, 1 = jpg
    private readonly string[] _extNames = new string[] { "png", "jpg" };

    private string _prefabDir = "Assets/whitelistsystem/URLGallery/Prefab/Partner";
    private Vector2 _scroll;

    // ไม่มี [MenuItem] โดยตั้งใจ — เปิดจากปุ่มใน Paradise Hub ที่เดียว
    // เมนู Paradise จะได้ไม่รก
    public static void Open()
    {
        GallerySetupWindow w = GetWindow<GallerySetupWindow>(false, "Gallery Setup", true);
        w.minSize = new Vector2(430, 330);
        w.Show();
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("ตั้งค่าแกลเลอรี", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "1. เลือกออบเจกต์ที่มี GallerySlideshow ใน Hierarchy (เลือกหลายอันพร้อมกันได้)\n" +
            "2. ใส่ URL ของแกลเลอรีด้านล่าง ต้องมี / ปิดท้าย\n" +
            "3. กดปุ่มเติม URL",
            MessageType.None);

        EditorGUILayout.Space();
        _galleryUrl = EditorGUILayout.TextField("Gallery URL", _galleryUrl);

        // แบ่งเป็นแถวละ 3 ปุ่ม ไม่งั้นพอมีหลายแกลเลอรีแล้วชื่อจะถูกบีบจนอ่านไม่ออก
        const int PerRow = 3;
        int count = Shortcuts.Length / 2;

        for (int row = 0; row * PerRow < count; row++)
        {
            EditorGUILayout.BeginHorizontal();
            for (int col = 0; col < PerRow; col++)
            {
                int idx = row * PerRow + col;
                if (idx >= count) { GUILayout.Label(GUIContent.none); continue; }

                int i = idx * 2;
                GUIStyle style = (col == 0) ? EditorStyles.miniButtonLeft
                               : (col == PerRow - 1) ? EditorStyles.miniButtonRight
                               : EditorStyles.miniButtonMid;

                if (GUILayout.Button(new GUIContent(Shortcuts[i], Shortcuts[i + 1]), style))
                {
                    _galleryUrl = SiteBase + Shortcuts[i + 1] + "/";
                    GUI.FocusControl(null);   // ให้ช่อง URL อัปเดตที่แสดงทันที
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space();
        _slots      = Mathf.Clamp(EditorGUILayout.IntField("จำนวนช่อง", _slots), 1, 128);
        _startIndex = Mathf.Clamp(EditorGUILayout.IntField("เลขไฟล์เริ่มที่", _startIndex), 0, 1);
        _extIndex   = EditorGUILayout.Popup("นามสกุลไฟล์", _extIndex, _extNames);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("ตัวอย่างที่จะได้", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel(ConfigUrl(), EditorStyles.textField,
                                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
        EditorGUILayout.SelectableLabel(ImageUrl(0), EditorStyles.textField,
                                        GUILayout.Height(EditorGUIUtility.singleLineHeight));
        EditorGUILayout.SelectableLabel(ImageUrl(_slots - 1), EditorStyles.textField,
                                        GUILayout.Height(EditorGUIUtility.singleLineHeight));

        EditorGUILayout.Space();
        int targets = CountTargets();
        EditorGUI.BeginDisabledGroup(targets == 0);
        if (GUILayout.Button("เติม URL ให้ที่เลือกอยู่ (" + targets + " ตัว)", GUILayout.Height(30)))
        {
            FillSelection();
        }
        EditorGUI.EndDisabledGroup();

        if (targets == 0)
        {
            EditorGUILayout.HelpBox("ยังไม่ได้เลือกออบเจกต์ที่มี GallerySlideshow", MessageType.Warning);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("บันทึกเป็น Prefab", EditorStyles.boldLabel);
        _prefabDir = EditorGUILayout.TextField("โฟลเดอร์", _prefabDir);

        EditorGUI.BeginDisabledGroup(Selection.activeGameObject == null);
        if (GUILayout.Button("บันทึกออบเจกต์ที่เลือกเป็น Prefab"))
        {
            SaveSelectedAsPrefab();
        }
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.EndScrollView();
    }

    // ---------- สร้าง URL ----------

    private string Base()
    {
        string u = (_galleryUrl == null) ? "" : _galleryUrl.Trim();
        if (u.Length == 0) return u;

        // ยุบ / ที่ซ้อนกันจากการแก้มือ แต่อย่าไปแตะ // หลัง https:
        int head = u.IndexOf("://");
        string scheme = "";
        if (head >= 0) { scheme = u.Substring(0, head + 3); u = u.Substring(head + 3); }
        while (u.Contains("//")) u = u.Replace("//", "/");

        u = scheme + u;
        if (!u.EndsWith("/")) u += "/";
        return u;
    }

    private string ConfigUrl()          { return Base() + "config.json"; }
    private string ImageUrl(int slot)   { return Base() + "images/" + (slot + _startIndex) + "." + _extNames[_extIndex]; }

    // ---------- เติมค่า ----------

    private int CountTargets()
    {
        int n = 0;
        GameObject[] sel = Selection.gameObjects;
        for (int i = 0; i < sel.Length; i++)
        {
            if (sel[i] != null && sel[i].GetComponent<GallerySlideshow>() != null) n++;
        }
        return n;
    }

    private void FillSelection()
    {
        GameObject[] sel = Selection.gameObjects;
        int done = 0;

        for (int i = 0; i < sel.Length; i++)
        {
            if (sel[i] == null) continue;
            GallerySlideshow gs = sel[i].GetComponent<GallerySlideshow>();
            if (gs == null) continue;

            Undo.RecordObject(gs, "Fill Gallery URLs");

            SerializedObject so = new SerializedObject(gs);

            SetUrl(so.FindProperty("configUrl"), ConfigUrl());

            SerializedProperty arr = so.FindProperty("imageUrls");
            if (arr != null)
            {
                arr.arraySize = _slots;
                for (int s = 0; s < _slots; s++)
                {
                    SetUrl(arr.GetArrayElementAtIndex(s), ImageUrl(s));
                }
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(gs);
            SyncToUdon(gs);
            done++;
        }

        Debug.Log("[Gallery Setup] เติม URL ให้ " + done + " ตัว — " + Base());
    }

    /// VRCUrl เก็บข้อความไว้ในฟิลด์ private ข้างใน ชื่อฟิลด์ต่างกันได้ตามเวอร์ชัน SDK
    private void SetUrl(SerializedProperty urlProp, string value)
    {
        if (urlProp == null) return;

        SerializedProperty s = urlProp.FindPropertyRelative("url");
        if (s == null) s = urlProp.FindPropertyRelative("m_Url");
        if (s == null) s = urlProp.FindPropertyRelative("_url");

        // หาไม่เจอก็ไล่ดูลูกทั้งหมด เอาตัวแรกที่เป็น string
        if (s == null)
        {
            SerializedProperty it  = urlProp.Copy();
            SerializedProperty end = urlProp.GetEndProperty();
            while (it.NextVisible(true) && !SerializedProperty.EqualContents(it, end))
            {
                if (it.propertyType == SerializedPropertyType.String) { s = it.Copy(); break; }
            }
        }

        if (s != null) s.stringValue = value;
        else Debug.LogWarning("[Gallery Setup] อ่านโครงสร้าง VRCUrl ไม่ออก — ต้องกรอกเองช่องนั้น");
    }

    /// UdonSharp เก็บค่าจริงไว้ใน UdonBehaviour ที่อยู่เบื้องหลัง ต้องสั่งคัดลอกตาม
    private void SyncToUdon(GallerySlideshow gs)
    {
        try
        {
            UdonSharpEditor.UdonSharpEditorUtility.CopyProxyToUdon(gs);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("[Gallery Setup] คัดลอกค่าเข้า UdonBehaviour ไม่สำเร็จ: " + e.Message +
                             "\nลองกด Play หนึ่งรอบ UdonSharp จะ sync ให้เอง");
        }
    }

    // ---------- Prefab ----------

    private void SaveSelectedAsPrefab()
    {
        GameObject go = Selection.activeGameObject;
        if (go == null) return;

        string dir = string.IsNullOrEmpty(_prefabDir) ? "Assets" : _prefabDir.Trim();
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();
        }

        string path = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + go.name + ".prefab");
        GameObject saved = PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.UserAction);

        if (saved != null)
        {
            Debug.Log("[Gallery Setup] บันทึก Prefab แล้ว: " + path);
            EditorGUIUtility.PingObject(saved);
        }
        else
        {
            Debug.LogError("[Gallery Setup] บันทึก Prefab ไม่สำเร็จ: " + path);
        }
    }
}
#endif
