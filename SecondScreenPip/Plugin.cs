using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace SecondScreenPip
{
    public enum PipRole
    {
        Auto,         // первое запущенное окно = хост (показывает), остальные = второе окно (передаёт)
        Host,         // всегда показывать окошко
        SecondWindow  // всегда передавать свой экран
    }

    [BepInDependency("com.github.zehsteam.LocalMultiplayer", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin("local.secondscreenpip", "Second Screen PiP", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private const string MutexName = "Local\\RepoSecondScreenPip_Host";

        private ConfigEntry<PipRole> _role;
        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<KeyCode> _launchKey;
        private ConfigEntry<KeyCode> _switchKey;
        private ConfigEntry<bool> _passArgs;
        private ConfigEntry<bool> _showOnStart;
        private ConfigEntry<string> _title;
        private ConfigEntry<int> _windowWidth;
        private ConfigEntry<int> _windowX;
        private ConfigEntry<int> _windowY;
        private ConfigEntry<int> _captureWidth;
        private ConfigEntry<int> _fps;
        private ConfigEntry<int> _jpgQuality;
        private ConfigEntry<bool> _flipVertical;

        private static Mutex _mutex;

        private bool _isSender;
        private string _dir;
        private string _frameFile;
        private string _tmpFile;

        // sender
        private RenderTexture _full;
        private RenderTexture _small;
        private Texture2D _readTex;
        private bool _errorLogged;

        // host
        private bool _show;
        private Texture2D _tex;
        private DateTime _lastWrite = DateTime.MinValue;
        private float _lastFrameTime = -100f;
        private float _nextRead;
        private Rect _rect;

        private void Awake()
        {
            _role = Config.Bind("General", "Role", PipRole.Auto,
                "Auto: первое окно игры - хост (показывает окошко), следующие - второе окно (передают экран). " +
                "Host / SecondWindow - задать роль вручную.");
            _toggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8, "Показать / скрыть окошко (в окне хоста).");
            _launchKey = Config.Bind("General", "LaunchKey", KeyCode.F2, "Запустить вторую копию REPO.exe (в окне хоста).");
            _switchKey = Config.Bind("General", "SwitchKey", KeyCode.F4, "Переключиться на другое окно игры (работает в обоих окнах).");
            _passArgs = Config.Bind("General", "LaunchPassArgs", false, "Передавать второй копии аргументы командной строки первой (обычно не нужно).");
            _showOnStart = Config.Bind("General", "ShowOnStart", true, "Показывать окошко сразу при запуске.");
            _title = Config.Bind("Window", "Title", "Игрок 2", "Заголовок окошка.");
            _windowWidth = Config.Bind("Window", "Width", 360, "Ширина окошка в пикселях.");
            _windowX = Config.Bind("Window", "X", 20, "Позиция окошка по X.");
            _windowY = Config.Bind("Window", "Y", 20, "Позиция окошка по Y.");
            _captureWidth = Config.Bind("Capture", "Width", 640, "Ширина передаваемой картинки (выше = чётче, но тяжелее).");
            _fps = Config.Bind("Capture", "Fps", 12, "Сколько кадров в секунду передавать.");
            _jpgQuality = Config.Bind("Capture", "JpgQuality", 70, "Качество JPEG, 10-100.");
            _flipVertical = Config.Bind("Capture", "FlipVertical", true,
                "Если картинка во втором окне получается вверх ногами - переключите это значение.");

            _dir = Path.Combine(Path.GetTempPath(), "RepoSecondScreenPip");
            _frameFile = Path.Combine(_dir, "frame.jpg");
            _tmpFile = Path.Combine(_dir, "frame.tmp");
            try { Directory.CreateDirectory(_dir); } catch (Exception e) { Logger.LogWarning("Temp dir: " + e.Message); }

            bool created = false;
            try { _mutex = new Mutex(true, MutexName, out created); } catch (Exception e) { Logger.LogWarning("Mutex: " + e.Message); created = true; }

            switch (_role.Value)
            {
                case PipRole.Host: _isSender = false; break;
                case PipRole.SecondWindow: _isSender = true; break;
                default: _isSender = !created; break;
            }

            // Без этого неактивное окно игры "засыпает" и отваливается от Photon по таймауту.
            Application.runInBackground = true;

            _show = _showOnStart.Value;
            _rect = new Rect(_windowX.Value, _windowY.Value, _windowWidth.Value, 100f);

            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.github.zehsteam.LocalMultiplayer"))
            {
                Logger.LogWarning("Найден отдельный LocalMultiplayer - встроенная версия отключена. Удалите его из plugins.");
            }
            else
            {
                try { LocalMp.Init(Config, Logger); }
                catch (Exception e) { Logger.LogError("LocalMp init failed: " + e); }
            }

            Logger.LogInfo("Second Screen PiP loaded. Role: " + (_isSender ? "SecondWindow (sends screen)" : "Host (shows window)"));

            if (_isSender)
            {
                try { if (File.Exists(_frameFile)) File.Delete(_frameFile); } catch { }
                StartCoroutine(CaptureLoop());
            }
        }

        private void Update()
        {
            if (!Application.runInBackground) Application.runInBackground = true;

            if (Input.GetKeyDown(_switchKey.Value)) SwitchWindow();

            if (_isSender) return;

            if (Input.GetKeyDown(_toggleKey.Value)) _show = !_show;
            if (Input.GetKeyDown(_launchKey.Value)) LaunchSecondWindow();

            if (_show && Time.unscaledTime >= _nextRead)
            {
                _nextRead = Time.unscaledTime + 1f / Mathf.Max(1, _fps.Value);
                TryLoadFrame();
            }
        }

        // ---------------- переключение между окнами ----------------

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

        private void SwitchWindow()
        {
            try
            {
                uint me = GetCurrentProcessId();
                IntPtr target = IntPtr.Zero;
                EnumWindows(delegate (IntPtr h, IntPtr l)
                {
                    if (!IsWindowVisible(h)) return true;
                    StringBuilder cls = new StringBuilder(64);
                    GetClassName(h, cls, cls.Capacity);
                    if (cls.ToString() != "UnityWndClass") return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == me) return true;
                    target = h;
                    return false;
                }, IntPtr.Zero);

                if (target == IntPtr.Zero) { Logger.LogInfo("Switch: другое окно игры не найдено."); return; }
                if (IsIconic(target)) ShowWindow(target, 9); // SW_RESTORE
                SetForegroundWindow(target);
            }
            catch (Exception e)
            {
                Logger.LogError("Switch window failed: " + e.Message);
            }
        }

        // ---------------- запуск второй копии игры ----------------

        private void LaunchSecondWindow()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                string exe = args[0];
                if (!File.Exists(exe))
                    exe = Application.dataPath.Substring(0, Application.dataPath.Length - "_Data".Length) + ".exe";

                StringBuilder sb = new StringBuilder();
                for (int i = 1; _passArgs.Value && i < args.Length; i++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append('"').Append(args[i].Replace("\"", "\\\"")).Append('"');
                }

                // Через reflection: тип Process лежит в разных сборках в зависимости от окружения.
                Type psiType = Type.GetType("System.Diagnostics.ProcessStartInfo, System", false)
                            ?? Type.GetType("System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process", true);
                Type procType = Type.GetType("System.Diagnostics.Process, System", false)
                             ?? Type.GetType("System.Diagnostics.Process, System.Diagnostics.Process", true);

                object psi = Activator.CreateInstance(psiType, new object[] { exe, sb.ToString() });
                psiType.GetProperty("WorkingDirectory").SetValue(psi, Path.GetDirectoryName(exe), null);
                psiType.GetProperty("UseShellExecute").SetValue(psi, false, null);

                // Doorstop (загрузчик BepInEx) помечает процесс переменной DOORSTOP_INITIALIZED,
                // и дочерняя копия игры иначе стартует БЕЗ BepInEx и модов. Сбрасываем её.
                object env = psiType.GetProperty("EnvironmentVariables").GetValue(psi, null);
                Type envType = env.GetType();
                System.Collections.ArrayList keys = new System.Collections.ArrayList((System.Collections.ICollection)envType.GetProperty("Keys").GetValue(env, null));
                MethodInfo remove = envType.GetMethod("Remove", new Type[] { typeof(string) });
                foreach (object k in keys)
                {
                    string key = (string)k;
                    if (key.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase))
                    {
                        remove.Invoke(env, new object[] { key });
                        Logger.LogInfo("Launch: сброшена переменная " + key);
                    }
                }
                procType.GetMethod("Start", new Type[] { psiType }).Invoke(null, new object[] { psi });

                Logger.LogInfo("Launched second window: " + exe + " " + sb);
            }
            catch (Exception e)
            {
                Logger.LogError("Launch second window failed: " + e.Message);
            }
        }

        // ---------------- второе окно: снимает экран ----------------

        private IEnumerator CaptureLoop()
        {
            WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
            float next = 0f;
            while (true)
            {
                yield return endOfFrame;
                if (Time.unscaledTime < next) continue;
                next = Time.unscaledTime + 1f / Mathf.Max(1, _fps.Value);
                try
                {
                    CaptureFrame();
                }
                catch (Exception e)
                {
                    if (!_errorLogged)
                    {
                        _errorLogged = true;
                        Logger.LogError("Capture failed: " + e);
                    }
                }
            }
        }

        private void CaptureFrame()
        {
            int sw = Screen.width;
            int sh = Screen.height;
            if (sw < 16 || sh < 16) return;

            if (_full == null || _full.width != sw || _full.height != sh)
            {
                ReleaseRt(ref _full);
                _full = new RenderTexture(sw, sh, 0, RenderTextureFormat.ARGB32);
                _full.Create();
            }

            int tw = Mathf.Clamp(_captureWidth.Value, 160, 1920);
            int th = Mathf.Max(2, Mathf.RoundToInt(tw * (float)sh / sw));
            if (_small == null || _small.width != tw || _small.height != th)
            {
                ReleaseRt(ref _small);
                _small = new RenderTexture(tw, th, 0, RenderTextureFormat.ARGB32);
                _small.Create();
                if (_readTex != null) Destroy(_readTex);
                _readTex = new Texture2D(tw, th, TextureFormat.RGB24, false);
            }

            ScreenCapture.CaptureScreenshotIntoRenderTexture(_full);

            if (_flipVertical.Value)
                Graphics.Blit(_full, _small, new Vector2(1f, -1f), new Vector2(0f, 1f));
            else
                Graphics.Blit(_full, _small);

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _small;
            _readTex.ReadPixels(new Rect(0, 0, tw, th), 0, 0, false);
            RenderTexture.active = prev;

            byte[] jpg = _readTex.EncodeToJPG(Mathf.Clamp(_jpgQuality.Value, 10, 100));

            try
            {
                File.WriteAllBytes(_tmpFile, jpg);
                if (File.Exists(_frameFile)) File.Replace(_tmpFile, _frameFile, null);
                else File.Move(_tmpFile, _frameFile);
            }
            catch (IOException)
            {
                // хост как раз читает файл - пропускаем этот кадр
            }
        }

        private static void ReleaseRt(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Destroy(rt);
            rt = null;
        }

        // ---------------- хост: показывает окошко ----------------

        private void TryLoadFrame()
        {
            try
            {
                if (!File.Exists(_frameFile)) return;
                DateTime t = File.GetLastWriteTimeUtc(_frameFile);
                if (t == _lastWrite) return;
                byte[] bytes = File.ReadAllBytes(_frameFile);
                if (_tex == null) _tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (LoadJpg(_tex, bytes))
                {
                    _lastWrite = t;
                    _lastFrameTime = Time.unscaledTime;
                }
            }
            catch (IOException)
            {
                // файл занят - попробуем в следующий раз
            }
            catch (Exception e)
            {
                Logger.LogWarning("Read frame: " + e.Message);
            }
        }

        // Вызов через reflection: у ImageConversion.LoadImage есть перегрузка с ReadOnlySpan,
        // которую не понимает старый компилятор csc из Windows.
        private static System.Reflection.MethodInfo _loadImage;
        private static bool LoadJpg(Texture2D tex, byte[] data)
        {
            if (_loadImage == null)
            {
                _loadImage = typeof(ImageConversion).GetMethod("LoadImage",
                    new Type[] { typeof(Texture2D), typeof(byte[]) });
                if (_loadImage == null) throw new MissingMethodException("ImageConversion.LoadImage(Texture2D, byte[])");
            }
            return (bool)_loadImage.Invoke(null, new object[] { tex, data });
        }

        private void OnGUI()
        {
            if (_isSender || !_show) return;

            float innerW = Mathf.Max(120f, _rect.width - 8f);
            float aspect = (_tex != null && _tex.width > 0) ? (float)_tex.height / _tex.width : 9f / 16f;
            _rect.height = 24f + innerW * aspect + 6f;

            _rect = GUI.Window(0x5C2E01, _rect, DrawWindow, _title.Value);
        }

        private void DrawWindow(int id)
        {
            float innerW = Mathf.Max(120f, _rect.width - 8f);
            float aspect = (_tex != null && _tex.width > 0) ? (float)_tex.height / _tex.width : 9f / 16f;
            Rect area = new Rect(4f, 22f, innerW, innerW * aspect);

            bool hasSignal = _tex != null && (Time.unscaledTime - _lastFrameTime) < 2.5f;
            if (_tex != null)
                GUI.DrawTexture(area, _tex, ScaleMode.StretchToFill, false);

            if (!hasSignal)
            {
                GUI.Box(area, GUIContent.none);
                GUI.Label(area, "  Нет сигнала от 2-го окна.\n  Запустите вторую копию игры\n  (не сворачивайте её).");
            }

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
        }

        private void OnDestroy()
        {
            if (_isSender)
            {
                try { if (File.Exists(_frameFile)) File.Delete(_frameFile); } catch { }
            }
        }

        private void OnApplicationQuit()
        {
            OnDestroy();
        }
    }
}
