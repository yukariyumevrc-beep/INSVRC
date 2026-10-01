#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// ดึงไฟล์ URLGallery เวอร์ชันล่าสุดจาก GitHub มาทับในโปรเจกต์
/// เรียกจากปุ่มใน Paradise Hub
///
/// มีไว้เพื่อไม่ต้องไล่ก๊อปไฟล์เองทุกโปรเจกต์เวลาแก้อะไรทีหนึ่ง
/// ไฟล์ที่เหมือนเดิมอยู่แล้วจะถูกข้าม Unity จึงไม่ recompile ถ้าไม่มีอะไรเปลี่ยน
/// </summary>
public static class ParadiseUpdater
{
    private const string RawBase =
        "https://raw.githubusercontent.com/yukariyumevrc-beep/INSVRC/main/unity/";

    // พาธใน repo  ->  พาธในโปรเจกต์ (เทียบจากโฟลเดอร์ URLGallery)
    private static readonly string[] Files = new string[]
    {
        "GallerySlideshow.cs",          "Script/GallerySlideshow.cs",
        "GalleryCrossfade.shader",      "Script/GalleryCrossfade.shader",
        "README.md",                    "Script/README.md",
        "Editor/GallerySetupWindow.cs", "Editor/GallerySetupWindow.cs",
        "Editor/ParadiseHubWindow.cs",  "Editor/ParadiseHubWindow.cs",
        "Editor/ParadiseUpdater.cs",    "Editor/ParadiseUpdater.cs",
    };

    /// หาโฟลเดอร์ URLGallery จากที่อยู่ของไฟล์นี้เอง
    /// จะได้ไม่ต้องฝังพาธตายตัว ย้ายโฟลเดอร์ไปไหนก็ยังหาเจอ
    public static string FindRoot()
    {
        string[] guids = AssetDatabase.FindAssets("ParadiseUpdater t:MonoScript");
        for (int i = 0; i < guids.Length; i++)
        {
            string p = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (!p.EndsWith("/ParadiseUpdater.cs")) continue;

            // <root>/Editor/ParadiseUpdater.cs  ->  <root>
            string editorDir = Path.GetDirectoryName(p).Replace('\\', '/');
            return Path.GetDirectoryName(editorDir).Replace('\\', '/');
        }
        return null;
    }

    public static void UpdateFromGitHub()
    {
        string root = FindRoot();
        if (string.IsNullOrEmpty(root))
        {
            EditorUtility.DisplayDialog("Paradise Updater",
                "หาโฟลเดอร์ URLGallery ไม่เจอ\n" +
                "ParadiseUpdater.cs ต้องอยู่ใน <URLGallery>/Editor/", "ปิด");
            return;
        }

        if (!EditorUtility.DisplayDialog("ดึงอัปเดตจาก GitHub",
                "จะดาวน์โหลดไฟล์ล่าสุดมาทับที่\n\n" + root + "\n\n" +
                "ไฟล์ที่คุณแก้เองในโฟลเดอร์นี้จะหายนะครับ", "ดึงเลย", "ยกเลิก"))
        {
            return;
        }

        List<string> changed = new List<string>();
        List<string> same    = new List<string>();
        List<string> failed  = new List<string>();

        try
        {
            int total = Files.Length / 2;
            for (int i = 0; i < Files.Length; i += 2)
            {
                string remote = Files[i];
                string local  = root + "/" + Files[i + 1];
                int n = i / 2;

                EditorUtility.DisplayProgressBar("ดึงอัปเดตจาก GitHub",
                    remote, (float)n / total);

                string text;
                if (!Download(RawBase + remote, out text))
                {
                    failed.Add(remote);
                    continue;
                }

                // เหมือนเดิมก็ไม่ต้องเขียน Unity จะได้ไม่ recompile ฟรี ๆ
                if (File.Exists(local) && File.ReadAllText(local) == text)
                {
                    same.Add(Files[i + 1]);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(local));
                File.WriteAllText(local, text, new System.Text.UTF8Encoding(false));
                changed.Add(Files[i + 1]);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (changed.Count > 0) AssetDatabase.Refresh();

        string msg = "อัปเดต " + changed.Count + " ไฟล์";
        if (changed.Count > 0) msg += "\n  " + string.Join("\n  ", changed.ToArray());
        if (same.Count > 0)    msg += "\n\nเหมือนเดิม " + same.Count + " ไฟล์";
        if (failed.Count > 0)  msg += "\n\nโหลดไม่สำเร็จ:\n  " + string.Join("\n  ", failed.ToArray());

        Debug.Log("[Paradise Updater] " + msg);
        EditorUtility.DisplayDialog("Paradise Updater", msg, "ปิด");
    }

    private static bool Download(string url, out string text)
    {
        text = null;

        // ใส่ตัวกันแคช เพราะ raw.githubusercontent แคชไว้หลายนาที
        string full = url + "?t=" + DateTime.UtcNow.Ticks;

        using (UnityWebRequest req = UnityWebRequest.Get(full))
        {
            req.timeout = 20;
            UnityWebRequestAsyncOperation op = req.SendWebRequest();
            while (!op.isDone) { }

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("[Paradise Updater] " + url + " : " + req.error);
                return false;
            }

            text = req.downloadHandler.text;
            return true;
        }
    }
}
#endif
