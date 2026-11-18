using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
#if !Q9_REGISTER32
using System.Windows.Forms;
#endif

[ComImport, Guid("71c6e74c-0f28-11d8-a82a-00065b84435c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfInputProcessorProfileMgr
{
    [PreserveSig] int ActivateProfile();
    [PreserveSig] int DeactivateProfile();
    [PreserveSig] int GetProfile();
    [PreserveSig] int EnumProfiles();
    [PreserveSig] int ReleaseInputProcessor();
    [PreserveSig] int RegisterProfile(ref Guid clsid, ushort language, ref Guid profile,
        [MarshalAs(UnmanagedType.LPWStr)] string description, uint descriptionLength,
        [MarshalAs(UnmanagedType.LPWStr)] string iconPath, uint iconLength, uint iconIndex,
        IntPtr substitute, uint preferredLayout, [MarshalAs(UnmanagedType.Bool)] bool enabledByDefault, uint flags);
    [PreserveSig] int UnregisterProfile(ref Guid clsid, ushort language, ref Guid profile, uint flags);
}

[ComImport, Guid("c3acefb5-f69d-4905-938f-fcadcf4be830"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfCategoryMgr
{
    [PreserveSig] int RegisterCategory(ref Guid clsid, ref Guid category, ref Guid item);
    [PreserveSig] int UnregisterCategory(ref Guid clsid, ref Guid category, ref Guid item);
}

internal static class Registration
{
    private static readonly Guid ProfilesClass = new Guid("33c53a50-f456-4884-b049-85fd643ecfed");
    private static readonly Guid CategoriesClass = new Guid("a4b544a1-438d-4b41-9325-869523e2d6c7");
    private static readonly Guid[] Categories = {
        new Guid("34745c63-b2f0-4784-8b67-5e12c8701a31"),
        new Guid("25504fb4-7bab-4bc1-9c69-cf81890f0ef5")
    };
    internal static readonly string Tip = "0404:{" + TsfIdentity.ClassId + "}{" + TsfIdentity.ProfileId + "}";
    private const string ComKey = @"SOFTWARE\Classes\CLSID\{" + TsfIdentity.ClassId + "}";

    [DllImport("input.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InstallLayoutOrTip(string tip, uint flags);

    internal static void Apply(string install, bool remove)
    {
        Guid clsid = new Guid(TsfIdentity.ClassId), profile = new Guid(TsfIdentity.ProfileId);
        string panel = Path.Combine(install, "Q9Rebuild.exe");
        string dll = Path.Combine(install, "Q9Tsf.dll");
        RegistryView view = Environment.Is64BitProcess ? RegistryView.Registry64 : RegistryView.Registry32;
        using (var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
        {
            if (remove)
            {
                using (var key = registry.OpenSubKey(ComKey))
                    if (key == null) return;
            }
            if (!remove)
            {
                using (var key = registry.CreateSubKey(ComKey + @"\InprocServer32"))
                {
                    key.SetValue("", "mscoree.dll");
                    key.SetValue("ThreadingModel", "Apartment");
                    key.SetValue("Class", "TsfInput");
                    key.SetValue("Assembly", AssemblyName.GetAssemblyName(dll).FullName);
                    key.SetValue("RuntimeVersion", "v4.0.30319");
                    // Same form as RegAsm /codebase. The CLR does not decode %20, so Uri.AbsoluteUri breaks "Program Files".
                    key.SetValue("CodeBase", "file:///" + dll.Replace('\\', '/'));
                }
            }

            var profiles = (ITfInputProcessorProfileMgr)Activator.CreateInstance(Type.GetTypeFromCLSID(ProfilesClass, true));
            var categories = (ITfCategoryMgr)Activator.CreateInstance(Type.GetTypeFromCLSID(CategoriesClass, true));
            try
            {
                if (remove)
                {
                    foreach (Guid value in Categories)
                    {
                        Guid category = value;
                        categories.UnregisterCategory(ref clsid, ref category, ref clsid);
                    }
                    profiles.UnregisterProfile(ref clsid, TsfIdentity.LanguageId, ref profile, 0);
                    registry.DeleteSubKeyTree(ComKey, false);
                    return;
                }
                foreach (Guid value in Categories)
                {
                    Guid category = value;
                    Marshal.ThrowExceptionForHR(categories.RegisterCategory(ref clsid, ref category, ref clsid));
                }
                string name = TsfIdentity.DisplayName;
                Marshal.ThrowExceptionForHR(profiles.RegisterProfile(ref clsid, TsfIdentity.LanguageId, ref profile,
                    name, (uint)name.Length, panel, (uint)panel.Length, 0, IntPtr.Zero, 0, true, 0));
            }
            finally
            {
                Marshal.ReleaseComObject(categories);
                Marshal.ReleaseComObject(profiles);
            }
        }
    }
}

internal static class Setup
{
#if Q9_REGISTER32
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "/register" && args[0] != "/unregister") return 2;
        try { Registration.Apply(args[1], args[0] == "/unregister"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
#else
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Q9Rebuild";
    private static readonly string[] Payload = { "Q9Rebuild.exe", "Q9Tsf.dll", "Q9Register32.exe", "Q9Setup.exe" };
    private static readonly string InstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Q9Rebuild");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string path, string target, uint flags);

    [STAThread]
    private static int Main(string[] args)
    {
        bool remove = args.Length == 1 && args[0] == "/uninstall";
        if (args.Length > 1 || args.Length == 1 && !remove && args[0] != "/install") return 2;
        try
        {
            if (remove) Uninstall(); else Install();
            MessageBox.Show(remove ? "已解除安裝九方 W10 重建版。" :
                "安裝完成。請喺 Windows 輸入法清單選擇「九方 W10 重建版」。", TsfIdentity.DisplayName);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, TsfIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Install()
    {
        string original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Q9W10B5");
        if (!File.Exists(Path.Combine(original, "q9w10b5tsf64.dll")) || !Directory.Exists(Path.Combine(original, "SKINready")))
            throw new DirectoryNotFoundException("請先安裝原版九方 W10，以提供碼表同皮膚。");
        Directory.CreateDirectory(InstallPath);
        string suffix = "." + Guid.NewGuid().ToString("N");
        bool upgrading;
        using (var key = Registry.LocalMachine.OpenSubKey(UninstallKey)) upgrading = key != null;
        bool startedRegistration = false;
        try
        {
            foreach (string name in Payload)
                WritePayload(name, Path.Combine(InstallPath, name) + suffix + ".new");

            int moved = 0;
            try
            {
                foreach (string name in Payload)
                {
                    string path = Path.Combine(InstallPath, name);
                    string backup = path + suffix + ".old";
                    if (File.Exists(path)) File.Move(path, backup);
                    moved++;
                    File.Move(path + suffix + ".new", path);
                }
                startedRegistration = true;
                Registration.Apply(InstallPath, false);
                Register32(false);
                using (var uninstall = Registry.LocalMachine.CreateSubKey(UninstallKey))
                {
                    string setup = Path.Combine(InstallPath, "Q9Setup.exe");
                    uninstall.SetValue("DisplayName", TsfIdentity.DisplayName);
                    uninstall.SetValue("DisplayIcon", Path.Combine(InstallPath, "Q9Rebuild.exe"));
                    uninstall.SetValue("InstallLocation", InstallPath);
                    uninstall.SetValue("UninstallString", "\"" + setup + "\" /uninstall");
                }
            }
            catch
            {
                if (!upgrading && startedRegistration)
                {
                    try { Register32(true); }
                    finally
                    {
                        Registration.Apply(InstallPath, true);
                        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
                    }
                }
                for (int i = moved - 1; i >= 0; --i)
                {
                    string path = Path.Combine(InstallPath, Payload[i]);
                    string backup = path + suffix + ".old";
                    if (File.Exists(path)) File.Move(path, path + suffix + ".failed");
                    if (File.Exists(backup)) File.Move(backup, path);
                    RemoveFile(path + suffix + ".failed");
                }
                throw;
            }
            foreach (string name in Payload) RemoveFile(Path.Combine(InstallPath, name) + suffix + ".old");
        }
        finally
        {
            foreach (string name in Payload) RemoveFile(Path.Combine(InstallPath, name) + suffix + ".new");
        }
        if (!Registration.InstallLayoutOrTip(Registration.Tip, 0))
            throw new InvalidOperationException("已註冊輸入法。請喺 Windows 繁體中文語言設定手動新增「九方 W10 重建版」。");
    }

    private static void WritePayload(string name, string path)
    {
        var assembly = Assembly.GetExecutingAssembly();
        if (name == "Q9Setup.exe") { File.Copy(assembly.Location, path); return; }
        using (var input = assembly.GetManifestResourceStream(name))
        using (var output = File.Create(path))
        {
            if (input == null) throw new InvalidDataException("安裝檔缺少 " + name);
            input.CopyTo(output);
        }
    }

    private static void Register32(bool remove)
    {
        string helper = Path.Combine(InstallPath, "Q9Register32.exe");
        if (remove && !File.Exists(helper)) return;
        var options = new ProcessStartInfo(helper, (remove ? "/unregister" : "/register") + " \"" + InstallPath + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        using (var process = Process.Start(options))
        {
            string errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("32 位元輸入法註冊失敗。" + errors);
        }
    }

    private static void Uninstall()
    {
        Registration.InstallLayoutOrTip(Registration.Tip, 1);
        Register32(true);
        Registration.Apply(InstallPath, true);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        foreach (string name in Payload) RemoveFile(Path.Combine(InstallPath, name));
        if (!Directory.Exists(InstallPath)) return;
        try { Directory.Delete(InstallPath); }
        catch (IOException)
        {
            if (!MoveFileEx(InstallPath, null, 4))
                throw new IOException("未能排程移除安裝資料夾：" + InstallPath);
        }
    }

    private static void RemoveFile(string path)
    {
        try { File.Delete(path); }
        catch (UnauthorizedAccessException)
        {
            if (!MoveFileEx(path, null, 4)) throw;
        }
        catch (IOException)
        {
            if (!MoveFileEx(path, null, 4)) throw;
        }
    }
#endif
}
