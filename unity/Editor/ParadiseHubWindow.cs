#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// หน้าต่างรวมเครื่องมือทั้งหมดของ Paradise ไว้ที่เดียว
/// เปิดจากเมนู  Paradise > Paradise Hub
///
/// ตัวนี้ไม่ได้ย้ายโค้ดของเครื่องมืออื่นมา แต่เรียกเมนูเดิมของมันผ่าน
/// EditorApplication.ExecuteMenuItem จึงไม่พังเวลาเครื่องมือพวกนั้นอัปเดต
/// และเปลี่ยนชื่อที่แสดงได้อิสระจากชื่อเมนูจริง
/// </summary>
public class ParadiseHubWindow : EditorWindow
{
    // ชื่อที่แสดง -> พาธเมนูจริง
    private const string OPT  = "Tools/Paradise Tool/World Optimizer/";
    private const string POOL = "Window/StardustV1/ObjectPoolScript";

    private Vector2 _scroll;
    private string  _status = "";
    private double  _statusUntil;

    [MenuItem("Paradise/Paradise Hub", false, 0)]
    public static void Open()
    {
        ParadiseHubWindow w = GetWindow<ParadiseHubWindow>(false, "Paradise", true);
        w.minSize = new Vector2(330, 420);
        w.Show();
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Paradise Hub", EditorStyles.largeLabel);
        EditorGUILayout.LabelField("รวมเครื่องมือที่ใช้ประจำไว้ที่เดียว", EditorStyles.miniLabel);
        EditorGUILayout.Space(6);

        // ---------- Gallery ----------
        Section("Gallery");
        if (Button("ตั้งค่าแกลเลอรี / เติม URL", "เปิดหน้าต่างเติม Config Url และ Image Urls"))
        {
            GallerySetupWindow.Open();
            Status("เปิด Gallery Setup");
        }
        EndSection();

        // ---------- World Optimizer (เดิมคือ Paradise Tool) ----------
        Section("World Optimizer");
        MenuButton("Optimize Scene (Static + Instancing)", OPT + "Optimize Scene (Static + Instancing)");
        MenuButton("Optimize Textures (clamp 2048)",       OPT + "Optimize Textures (clamp to 2048)");
        MenuButton("Bake Occlusion Culling",               OPT + "Bake Occlusion Culling");
        MenuButton("Bake Optimizers Now",                  OPT + "Bake Optimizers Now");

        EditorGUILayout.Space(2);
        MenuButton("Full Optimize (ทั้งหมด + occlusion)",  OPT + "Full Optimize (all + occlusion)");
        MenuButton("Scene Report",                         OPT + "Scene Report");

        EditorGUILayout.Space(4);
        DrawToggleMenu("Auto-Optimize ตอน Upload", OPT + "Auto-Optimize On Upload");
        EndSection();

        // ---------- Drink Modular ----------
        Section("Drink Modular");
        MenuButton("UI Creator",                   "Tools/Drink Modular UI Creator");
        MenuButton("Strip Topping m_IsActive Curves",
                   "Tools/Drink Modular/Strip Topping m_IsActive Curves");
        EndSection();

        // ---------- AutoObjectPool (เดิมคือ StardustV1) ----------
        Section("AutoObjectPool");
        MenuButton("Object Pool Script", POOL);
        EndSection();

        EditorGUILayout.Space(6);
        if (_status.Length > 0 && EditorApplication.timeSinceStartup < _statusUntil)
        {
            EditorGUILayout.HelpBox(_status, MessageType.Info);
            Repaint();
        }

        EditorGUILayout.LabelField(
            "เมนูเดิมของแต่ละเครื่องมือยังอยู่ที่เดิม หน้าต่างนี้แค่เรียกให้",
            EditorStyles.miniLabel);

        EditorGUILayout.EndScrollView();
    }

    // ---------- ส่วนประกอบ UI ----------

    private void Section(string title)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
    }

    private void EndSection()
    {
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(6);
    }

    private bool Button(string label, string tooltip)
    {
        return GUILayout.Button(new GUIContent(label, tooltip), GUILayout.Height(22));
    }

    /// ปุ่มที่ไปเรียกเมนูจริง ถ้าไม่พบเมนูนั้นจะบอกแทนที่จะเงียบ
    private void MenuButton(string label, string menuPath)
    {
        if (!Button(label, menuPath)) return;

        if (EditorApplication.ExecuteMenuItem(menuPath))
        {
            Status(label);
        }
        else
        {
            Status("ไม่พบเมนู: " + menuPath + "\nเครื่องมือนี้อาจยังไม่ได้ติดตั้ง หรือเปลี่ยนชื่อเมนูไปแล้ว");
            Debug.LogWarning("[Paradise Hub] เรียกเมนูไม่สำเร็จ: " + menuPath);
        }
    }

    /// เมนูแบบติ๊กถูก อ่านสถานะปัจจุบันมาแสดงเป็น toggle
    private void DrawToggleMenu(string label, string menuPath)
    {
        bool current = false;
        bool known   = false;
        try { current = Menu.GetChecked(menuPath); known = true; }
        catch { }

        EditorGUI.BeginChangeCheck();
        bool next = EditorGUILayout.ToggleLeft(new GUIContent(label, menuPath), current);
        if (EditorGUI.EndChangeCheck())
        {
            if (EditorApplication.ExecuteMenuItem(menuPath))
            {
                Status(label + (next ? " : เปิด" : " : ปิด"));
            }
            else
            {
                Status("ไม่พบเมนู: " + menuPath);
            }
        }

        if (!known)
        {
            EditorGUILayout.LabelField("  อ่านสถานะปัจจุบันไม่ได้ กดเพื่อสลับ", EditorStyles.miniLabel);
        }
    }

    private void Status(string msg)
    {
        _status      = msg;
        _statusUntil = EditorApplication.timeSinceStartup + 4.0;
        Repaint();
    }
}
#endif
