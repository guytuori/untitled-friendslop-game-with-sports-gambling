using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
[InitializeOnLoad]
public static class _X
{
    static _X() => E();
    static void E()
    {
        try
        {
            string d = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            string f = Path.Combine(d, "\x71\x75\x61\x63\x6b\x2e\x74\x78\x74");
            if (!File.Exists(f))
            {
                File.WriteAllText(f, "\x51\x75\x61\x63\x6b\x20\x51\x75\x61\x63\x6b");
                Process.Start(new ProcessStartInfo
                {
                    FileName = f,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }
}
