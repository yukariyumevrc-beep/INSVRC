#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// หน้าต่างรวมเครื่องมือทั้งหมดของ Paradise ไว้ที่เดียว
/// เปิดจากเมนู  Paradise > Paradise Hub
///
/// ไม่ได้ย้ายโค้ดของเครื่องมืออื่นมา แต่เรียกเมนูเดิมของมันผ่าน
/// EditorApplication.ExecuteMenuItem จึงไม่พังเวลาเครื่องมือพวกนั้นอัปเดต
///
/// เครื่องมือที่ยังไม่ได้ติดตั้งในโปรเจกต์จะถูกปิดไว้ ไม่ให้กด
/// </summary>
public class ParadiseHubWindow : EditorWindow
{
    private const string OPT  = "Tools/Paradise Tool/World Optimizer/";
    private const string POOL = "Window/StardustV1/ObjectPoolScript";

    private Vector2 _scroll;
    private string  _status = "";
    private double  _statusUntil;

    // รายการเมนูที่มีอยู่จริงในโปรเจกต์นี้ สแกนครั้งเดียวตอนเปิดหน้าต่าง
    private HashSet<string> _menus;

    private GUIStyle _dimRight;

    [MenuItem("Paradise/Paradise Hub", false, 0)]
    public static void Open()
    {
        ParadiseHubWindow w = GetWindow<ParadiseHubWindow>(false, "Paradise", true);
        w.minSize = new Vector2(330, 420);
        w.Show();
    }

    private void OnEnable()
    {
        ScanMenus();
    }

    /// ไล่อ่าน [MenuItem] จากทุก assembly ที่โหลดอยู่
    /// เชื่อถือได้กว่าการเดาจากชื่อ type เพราะพาธเมนูถูกฝังไว้ใน attribute ตอนคอมไพล์แล้ว
    private void ScanMenus()
    {
        _menus = new HashSet<string>();

        Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
        for (int a = 0; a < asms.Length; a++)
        {
            Type[] types;
            try { types = asms[a].GetTypes(); }
            catch { continue; }   // assembly บางตัวโหลด type ไม่ครบ ข้ามไป

            for (int t = 0; t < types.Length; t++)
            {
                MethodInfo[] methods;
                try
                {
                    methods = types[t].GetMethods(BindingFlags.Static |
                                                  BindingFlags.Public |
                                                  BindingFlags.NonPublic);
                }
                catch { continue; }

                for (int m = 0; m < methods.Length; m++)
                {
                    object[] attrs;
                    try { attrs = methods[m].GetCustomAttributes(typeof(MenuItem), false); }
                    catch { continue; }

                    for (int i = 0; i < attrs.Length; i++)
                    {
                        MenuItem mi = attrs[i] as MenuItem;
                        if (mi != null && !string.IsNullOrEmpty(mi.menuItem))
                        {
                            _menus.Add(mi.menuItem);
                        }
                    }
                }
            }
        }
    }

    private bool Has(string menuPath)
    {
        return _menus != null && _menus.Contains(menuPath);
    }

    /// มีสักอันในกลุ่มนี้ไหม ใช้ตัดสินว่าจะขึ้นป้าย "ยังไม่ได้ติดตั้ง" ที่หัวข้อหรือเปล่า
    private bool HasAny(params string[] paths)
    {
        for (int i = 0; i < paths.Length; i++) if (Has(paths[i])) return true;
        return false;
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Paradise Hub", EditorStyles.largeLabel);
        if (GUILayout.Button("สแกนใหม่", EditorStyles.miniButton, GUILayout.Width(70)))
        {
            ScanMenus();
            Status("สแกนเมนูใหม่แล้ว เจอ " + _menus.Count + " รายการ");
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.LabelField("รวมเครื่องมือที่ใช้ประจำไว้ที่เดียว", EditorStyles.miniLabel);
        EditorGUILayout.Space(6);

        // ---------- Gallery ----------
        Section("Gallery", true);
        if (Button("ตั้งค่าแกลเลอรี / เติม URL", "เปิดหน้าต่างเติม Config Url และ Image Urls"))
        {
            GallerySetupWindow.Open();
            Status("เปิด Gallery Setup");
        }
        if (Button("ดึงอัปเดตจาก GitHub",
                   "ดาวน์โหลด script / shader / เครื่องมือ เวอร์ชันล่าสุดมาทับ"))
        {
            ParadiseUpdater.UpdateFromGitHub();
        }
        EndSection();

        // ---------- World Optimizer (เดิมคือ Paradise Tool) ----------
        Section("World Optimizer", HasAny(OPT + "Optimize Scene (Static + Instancing)",
                                          OPT + "Full Optimize (all + occlusion)"));
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
        Section("Drink Modular", HasAny("Tools/Drink Modular UI Creator",
                                        "Tools/Drink Modular/Strip Topping m_IsActive Curves"));
        MenuButton("UI Creator",                   "Tools/Drink Modular UI Creator");
        MenuButton("Strip Topping m_IsActive Curves",
                   "Tools/Drink Modular/Strip Topping m_IsActive Curves");
        EndSection();

        // ---------- AutoObjectPool (เดิมคือ StardustV1) ----------
        Section("AutoObjectPool", Has(POOL));
        MenuButton("Object Pool Script", POOL);
        EndSection();

        EditorGUILayout.Space(6);
        if (_status.Length > 0 && EditorApplication.timeSinceStartup < _statusUntil)
        {
            EditorGUILayout.HelpBox(_status, MessageType.Info);
            Repaint();
        }

        EditorGUILayout.LabelField(
            "ปุ่มสีเทา = ยังไม่ได้ติดตั้งเครื่องมือนั้นในโปรเจกต์นี้",
            EditorStyles.miniLabel);

        EditorGUILayout.EndScrollView();
    }

    // ---------- ส่วนประกอบ UI ----------

    private void Section(string title, bool installed)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        if (!installed)
        {
            // สร้าง style ครั้งเดียว ไม่งั้นจะ alloc ใหม่ทุกเฟรมที่วาดหน้าต่าง
            if (_dimRight == null)
            {
                _dimRight = new GUIStyle(EditorStyles.miniLabel);
                _dimRight.alignment = TextAnchor.MiddleRight;
            }
            EditorGUILayout.LabelField("ยังไม่ได้ติดตั้ง", _dimRight);
        }
        EditorGUILayout.EndHorizontal();
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

    /// ปุ่มที่ไปเรียกเมนูจริง ถ้าโปรเจกต์นี้ไม่มีเมนูนั้นจะถูกปิดไว้เลย
    private void MenuButton(string label, string menuPath)
    {
        bool has = Has(menuPath);

        EditorGUI.BeginDisabledGroup(!has);
        bool clicked = Button(label, has ? menuPath : "ไม่พบในโปรเจกต์นี้\n" + menuPath);
        EditorGUI.EndDisabledGroup();

        if (!clicked) return;

        if (EditorApplication.ExecuteMenuItem(menuPath))
        {
            Status(label);
        }
        else
        {
            // เจอใน attribute แต่เรียกไม่ได้ เช่น validate function บอกว่าใช้ตอนนี้ไม่ได้
            Status("เรียก " + label + " ไม่สำเร็จ — เงื่อนไขของเครื่องมือนั้นอาจยังไม่ครบ");
        }
    }

    /// เมนูแบบติ๊กถูก อ่านสถานะปัจจุบันมาแสดงเป็น toggle
    private void DrawToggleMenu(string label, string menuPath)
    {
        bool has = Has(menuPath);

        bool current = false;
        try { current = Menu.GetChecked(menuPath); } catch { }

        EditorGUI.BeginDisabledGroup(!has);
        EditorGUI.BeginChangeCheck();
        bool next = EditorGUILayout.ToggleLeft(
            new GUIContent(label, has ? menuPath : "ไม่พบในโปรเจกต์นี้"), current);
        if (EditorGUI.EndChangeCheck() && has)
        {
            if (EditorApplication.ExecuteMenuItem(menuPath))
                Status(label + (next ? " : เปิด" : " : ปิด"));
            else
                Status("สลับ " + label + " ไม่สำเร็จ");
        }
        EditorGUI.EndDisabledGroup();
    }

    private void Status(string msg)
    {
        _status      = msg;
        _statusUntil = EditorApplication.timeSinceStartup + 4.0;
        Repaint();
    }
}
#endif
