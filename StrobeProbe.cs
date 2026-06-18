using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using Il2CppInterop.Runtime;                       // GetIl2CppType(), Unbox<T>()
using Il2CppInterop.Runtime.InteropTypes.Arrays;   // Il2CppStructArray<T>
using Il2CppSystemType = Il2CppSystem.Type;
using Il2CppFieldInfo = Il2CppSystem.Reflection.FieldInfo;
using Il2CppMethodInfo = Il2CppSystem.Reflection.MethodInfo;
using Il2CppBindingFlags = Il2CppSystem.Reflection.BindingFlags;

[assembly: MelonInfo(typeof(StrobeProbe.Probe), "StrobeProbe", "0.4.0", "Omnidreamer_")]
[assembly: MelonGame("Kluge Interactive", "SynthRiders")]

namespace StrobeProbe
{
    public class Probe : MelonMod
    {
        // ---- config ----
        private MelonPreferences_Category _cfg;
        private MelonPreferences_Entry<float> _monitorSeconds;
        private MelonPreferences_Entry<float> _colorDelta;
        private MelonPreferences_Entry<float> _intensityDelta;
        private MelonPreferences_Entry<int> _parentLevels;
        private MelonPreferences_Entry<string> _deepDumpFilter;  // substring match for F11
        private MelonPreferences_Entry<string> _strobeMatFilter;  // substring match for F7 (material/shader name)

        // ---- light monitor state ----
        private bool _monitoring;
        private float _monitorEndTime;
        private readonly Dictionary<int, Track> _tracks = new Dictionary<int, Track>();

        // ---- material monitor state ----
        private bool _matMonitoring;
        private float _matMonitorEndTime;
        private readonly List<MatTrack> _matTracks = new List<MatTrack>();
        private MaterialPropertyBlock _mpb;

        private static readonly string[] ColorProps =
        { "_Color", "_BaseColor", "_EmissionColor", "_EmissiveColor", "_TintColor" };

        private string _logDir;
        private bool _inputBroken, _warnedInput;

        public override void OnInitializeMelon()
        {
            _cfg = MelonPreferences.CreateCategory("StrobeProbe");
            _monitorSeconds = _cfg.CreateEntry("MonitorSeconds", 6f);
            _colorDelta = _cfg.CreateEntry("ColorDeltaThreshold", 0.12f);
            _intensityDelta = _cfg.CreateEntry("IntensityDeltaThreshold", 0.30f);
            _parentLevels = _cfg.CreateEntry("ParentLevelsToDump", 3);
            _deepDumpFilter = _cfg.CreateEntry("DeepDumpFilter", "StageInteractions");
            _strobeMatFilter = _cfg.CreateEntry("StrobeMaterialFilter", "Strobe");

            _logDir = Path.Combine(MelonEnvironment.UserDataDirectory, "StrobeProbe");
            Directory.CreateDirectory(_logDir);
            _mpb = new MaterialPropertyBlock();

            LoggerMsg("StrobeProbe v0.4.0 loaded.");
            LoggerMsg($"  F7  = INSPECT strobe materials matching '{_strobeMatFilter.Value}' (full props + MPB + instancing)");
            LoggerMsg("  F8  = snapshot all lights");
            LoggerMsg("  F9  = light change-monitor");
            LoggerMsg("  F10 = controller hunt (keyword) + Color fields");
            LoggerMsg($"  F11 = DEEP DUMP components matching '{_deepDumpFilter.Value}' (fields + methods + renderers)");
            LoggerMsg("  F12 = MATERIAL colour monitor (finds emissive/shader strobe surfaces)");
            LoggerMsg($"  Logs: {_logDir}");
        }

        public override void OnUpdate()
        {
            if (Key(KeyCode.F7)) SafeRun("strobe-inspect", InspectStrobeMaterials);
            if (Key(KeyCode.F8)) SafeRun("snapshot", SnapshotLights);
            if (Key(KeyCode.F9)) SafeRun("monitor-toggle", ToggleMonitor);
            if (Key(KeyCode.F10)) SafeRun("controller-hunt", HuntControllers);
            if (Key(KeyCode.F11)) SafeRun("deep-dump", DeepDump);
            if (Key(KeyCode.F12)) SafeRun("mat-monitor-toggle", ToggleMatMonitor);

            if (_monitoring)
            {
                SafeRun("monitor-sample", SampleFrame);
                if (Time.realtimeSinceStartup >= _monitorEndTime) SafeRun("monitor-report", () => FinishMonitor(null));
            }
            if (_matMonitoring)
            {
                SafeRun("mat-sample", SampleMatFrame);
                if (Time.realtimeSinceStartup >= _matMonitorEndTime) SafeRun("mat-report", () => FinishMatMonitor(null));
            }
        }

        // ============================================================ F7 — STROBE MATERIAL INSPECTOR
        private void InspectStrobeMaterials(StringBuilder sb)
        {
            string filter = (_strobeMatFilter.Value ?? "").ToLowerInvariant();
            sb.AppendLine($"=== STROBE MATERIAL INSPECT (material or shader name contains '{_strobeMatFilter.Value}') ===");
            var seen = new HashSet<int>();
            int hits = 0;

            foreach (var root in AllRoots())
            {
                if (IsNull(root)) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (IsNull(r)) continue;
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Count; i++)
                    {
                        var m = mats[i];
                        if (IsNull(m)) continue;
                        string matName = SafeName(() => m.name);
                        string shaderName = SafeName(() => m.shader.name);
                        if (filter.Length > 0 &&
                            !matName.ToLowerInvariant().Contains(filter) &&
                            !shaderName.ToLowerInvariant().Contains(filter)) continue;

                        int id = m.GetInstanceID();
                        if (!seen.Add(id)) continue;
                        hits++;

                        bool hasBlock = HasBlock(r);
                        sb.AppendLine($"\n##### {matName}   (shader: {shaderName})");
                        sb.AppendLine($"# renderer: {SafeTypeName(r)} @ {GetPath(r.transform)}");
                        sb.AppendLine($"# instanced: {matName.Contains("(Instance)")}   renderer.HasPropertyBlock: {hasBlock}   matIndex: {i}");
                        DumpAllShaderProps(m, sb, "    ");

                        if (hasBlock)
                        {
                            r.GetPropertyBlock(_mpb);
                            sb.AppendLine("    [MaterialPropertyBlock colour overrides]:");
                            foreach (var pn in ColorPropertiesOf(m))
                                sb.AppendLine($"      {pn} = {Fmt(_mpb.GetColor(pn))}");
                        }

                        DumpNearbyComponents(r.transform, sb, "    ");   // find the SDK driver script
                    }
                }
            }

            sb.AppendLine($"\n--- {hits} strobe materials ---");
            if (hits == 0)
                sb.AppendLine("(none matched — adjust StrobeProbe.StrobeMaterialFilter in MelonPreferences.cfg)");
        }

        private void DumpAllShaderProps(Material m, StringBuilder sb, string indent)
        {
            try
            {
                var shader = m.shader;
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    string name = SafeName(() => shader.GetPropertyName(i));
                    string type = SafeName(() => shader.GetPropertyType(i).ToString());
                    string val;
                    switch (type)
                    {
                        case "Color": val = Fmt(m.GetColor(name)); break;
                        case "Vector": { var v = m.GetVector(name); val = $"({v.x:0.##},{v.y:0.##},{v.z:0.##},{v.w:0.##})"; break; }
                        case "Texture":
                            {
                                var tex = m.GetTexture(name);
                                val = IsNull(tex) ? "null"
                                    : $"{SafeName(() => tex.name)} <{SafeTypeName(tex)}> {tex.width}x{tex.height}";
                                break;
                            }
                        default: val = m.GetFloat(name).ToString("0.###"); break;  // Float / Range / Int
                    }
                    sb.AppendLine($"{indent}{type} {name} = {val}");
                }
            }
            catch (Exception e) { sb.AppendLine($"{indent}<shader prop dump failed: {e.Message}>"); }
        }

        private static bool HasBlock(Renderer r) { try { return r.HasPropertyBlock(); } catch { return false; } }

        // ============================================================ F8
        private void SnapshotLights(StringBuilder sb)
        {
            var lights = CollectLights();
            sb.AppendLine($"=== LIGHT SNAPSHOT ({lights.Count} lights) ===");
            foreach (var l in lights)
            {
                if (IsNull(l)) continue;
                sb.AppendLine($"[{l.type}] {GetPath(l.transform)}");
                sb.AppendLine($"    color={Fmt(l.color)} intensity={l.intensity:0.###} enabled={l.enabled} go.active={l.gameObject.activeInHierarchy}");
                DumpNearbyComponents(l.transform, sb, "    ");
            }
        }

        // ============================================================ F9
        private void ToggleMonitor(StringBuilder _)
        {
            if (_monitoring) { FinishMonitor(null); return; }
            _tracks.Clear();
            _monitoring = true;
            _monitorEndTime = Time.realtimeSinceStartup + _monitorSeconds.Value;
            LoggerMsg($"Light monitor STARTED for {_monitorSeconds.Value:0.#}s.");
        }
        private void SampleFrame()
        {
            foreach (var l in CollectLights())
            {
                if (IsNull(l)) continue;
                int id = l.GetInstanceID();
                if (!_tracks.TryGetValue(id, out var t)) { t = new Track { Light = l, Path = GetPath(l.transform) }; _tracks[id] = t; }
                t.Observe(l.color, l.intensity);
            }
        }
        private void FinishMonitor(StringBuilder external)
        {
            _monitoring = false;
            var sb = external ?? new StringBuilder();
            sb.AppendLine($"=== LIGHT MONITOR ({_tracks.Count} lights, {_monitorSeconds.Value:0.#}s) ===");
            var flashing = new List<Track>();
            foreach (var t in _tracks.Values)
                if (t.ColorRange >= _colorDelta.Value || t.IntensityRange >= _intensityDelta.Value) flashing.Add(t);
            flashing.Sort((a, b) => (b.ColorRange + b.IntensityRange).CompareTo(a.ColorRange + a.IntensityRange));
            sb.AppendLine($"--- {flashing.Count} changed ---");
            foreach (var t in flashing)
            {
                sb.AppendLine($"* {t.Path}  colorRange={t.ColorRange:0.###} intensityRange={t.IntensityRange:0.###} samples={t.Samples}");
                if (!IsNull(t.Light)) DumpNearbyComponents(t.Light.transform, sb, "    ");
            }
            if (external == null) WriteLog("monitor", sb.ToString());
            _tracks.Clear();
            LoggerMsg($"Light monitor DONE — {flashing.Count} changed.");
        }

        // ============================================================ F10
        private static readonly string[] Keywords =
        { "strobe","flash","lighting","lightmanager","stage","beam","laser","spotlight",
          "glow","palette","colorscheme","colourscheme","lightevent","tile","interaction",
          "spectrum","vfx","environment","beat","pulse" };

        private void HuntControllers(StringBuilder sb)
        {
            sb.AppendLine("=== CONTROLLER HUNT (keyword match) ===");
            int hits = 0;
            foreach (var go in AllSceneObjects())
            {
                if (IsNull(go)) continue;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (IsNull(c)) continue;
                    string name = SafeTypeName(c);
                    if (!MatchesKeyword(name)) continue;
                    hits++;
                    sb.AppendLine($"[{name}]  @ {GetPath(go.transform)}");
                    DumpColorFields(c, sb, "    ");
                }
            }
            sb.AppendLine($"--- {hits} matching components ---");
        }

        // ============================================================ F11 — DEEP DUMP
        private void DeepDump(StringBuilder sb)
        {
            string filter = _deepDumpFilter.Value.ToLowerInvariant();
            sb.AppendLine($"=== DEEP DUMP (type name contains '{_deepDumpFilter.Value}') ===");
            int hits = 0;
            foreach (var go in AllSceneObjects())
            {
                if (IsNull(go)) continue;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (IsNull(c)) continue;
                    string name = SafeTypeName(c);
                    if (!name.ToLowerInvariant().Contains(filter)) continue;
                    hits++;
                    sb.AppendLine($"\n##### {name}");
                    sb.AppendLine($"# path: {GetPath(go.transform)}  active={go.activeInHierarchy}");
                    DumpAllFields(c, sb);
                    DumpAllMethods(c, sb);
                    DumpRenderersUnder(go, sb);
                }
            }
            sb.AppendLine($"\n--- {hits} matching components ---");
            if (hits == 0)
                sb.AppendLine("(nothing matched — adjust StrobeProbe.DeepDumpFilter in MelonPreferences.cfg)");
        }

        private void DumpAllFields(Component comp, StringBuilder sb)
        {
            sb.AppendLine("# FIELDS (declared):");
            try
            {
                Il2CppSystemType t = comp.GetIl2CppType();
                string self = t.FullName;
                var flags = Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic
                          | Il2CppBindingFlags.Instance | Il2CppBindingFlags.Static;
                foreach (var f in t.GetFields(flags))
                {
                    if (!DeclaredOn(f.DeclaringType, self)) continue;
                    string ftName = SafeName(() => f.FieldType.Name);
                    string val = ReadFieldValue(f, comp, ftName);
                    sb.AppendLine($"    {ftName} {f.Name} = {val}");
                }
            }
            catch (Exception e) { sb.AppendLine($"    <field dump failed: {e.Message}>"); }
        }

        private void DumpAllMethods(Component comp, StringBuilder sb)
        {
            sb.AppendLine("# METHODS (declared):");
            try
            {
                Il2CppSystemType t = comp.GetIl2CppType();
                string self = t.FullName;
                var flags = Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic
                          | Il2CppBindingFlags.Instance | Il2CppBindingFlags.Static;
                foreach (var m in t.GetMethods(flags))
                {
                    if (!DeclaredOn(m.DeclaringType, self)) continue;
                    sb.AppendLine($"    {SafeMethodSig(m)}");
                }
            }
            catch (Exception e) { sb.AppendLine($"    <method dump failed: {e.Message}>"); }
        }

        // dump every renderer at/under this GameObject + its colour-bearing shader props
        private void DumpRenderersUnder(GameObject go, StringBuilder sb)
        {
            sb.AppendLine("# RENDERERS under this object:");
            try
            {
                var renderers = go.GetComponentsInChildren<Renderer>(true);
                int n = 0;
                foreach (var r in renderers)
                {
                    if (IsNull(r)) continue;
                    n++;
                    sb.AppendLine($"    {SafeTypeName(r)} @ {GetPath(r.transform)}");
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Count; i++)
                    {
                        var m = mats[i];
                        if (IsNull(m)) continue;
                        string shader = SafeName(() => m.shader.name);
                        sb.Append($"      mat[{i}] shader={shader}");
                        foreach (var p in ColorProps)
                            if (TryGetColor(m, p, out var col)) sb.Append($"  {p}={Fmt(col)}");
                        sb.AppendLine();
                    }
                }
                if (n == 0) sb.AppendLine("    (none)");
            }
            catch (Exception e) { sb.AppendLine($"    <renderer dump failed: {e.Message}>"); }
        }

        // ============================================================ F12 — MATERIAL MONITOR
        private void ToggleMatMonitor(StringBuilder _)
        {
            if (_matMonitoring) { FinishMatMonitor(null); return; }
            BuildMatCandidates();
            _matMonitoring = true;
            _matMonitorEndTime = Time.realtimeSinceStartup + _monitorSeconds.Value;
            LoggerMsg($"Material monitor STARTED for {_monitorSeconds.Value:0.#}s — tracking {_matTracks.Count} colour properties. Trigger a strobe section.");
        }

        private void BuildMatCandidates()
        {
            _matTracks.Clear();
            foreach (var root in AllRoots())
            {
                if (IsNull(root)) continue;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (IsNull(r)) continue;
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Count; i++)
                    {
                        var m = mats[i];
                        if (IsNull(m)) continue;
                        string shaderName = SafeName(() => m.shader.name);
                        // Enumerate EVERY Color-type property the shader declares — works for custom
                        // stage shaders whose colour property isn't one of the standard names.
                        foreach (var p in ColorPropertiesOf(m))
                        {
                            _matTracks.Add(new MatTrack { R = r, Mat = m, Prop = p, Path = GetPath(r.transform), Shader = shaderName, Source = "mat" });
                            _matTracks.Add(new MatTrack { R = r, Mat = null, Prop = p, Path = GetPath(r.transform), Shader = shaderName, Source = "mpb" });
                        }
                    }
                }
            }
        }

        // dynamically list all Color-typed shader properties on a material's shader
        private static List<string> ColorPropertiesOf(Material m)
        {
            var props = new List<string>();
            try
            {
                var shader = m.shader;
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count; i++)
                {
                    // ShaderPropertyType.Color stringifies as "Color" on both Unity branches
                    if (SafeName(() => shader.GetPropertyType(i).ToString()) != "Color") continue;
                    string name = SafeName(() => shader.GetPropertyName(i));
                    if (!string.IsNullOrEmpty(name) && !props.Contains(name)) props.Add(name);
                }
            }
            catch
            {
                // fallback: probe the standard names if the shader-reflection API misbehaves
                foreach (var p in ColorProps) { try { if (m.HasProperty(p)) props.Add(p); } catch { } }
            }
            return props;
        }

        private void SampleMatFrame()
        {
            foreach (var t in _matTracks)
            {
                if (IsNull(t.R)) continue;
                Color c;
                if (t.Source == "mat")
                {
                    if (IsNull(t.Mat)) continue;
                    c = t.Mat.GetColor(t.Prop);
                }
                else
                {
                    t.R.GetPropertyBlock(_mpb);
                    c = _mpb.GetColor(t.Prop);
                }
                t.Observe(c);
            }
        }

        private void FinishMatMonitor(StringBuilder external)
        {
            _matMonitoring = false;
            var sb = external ?? new StringBuilder();
            sb.AppendLine($"=== MATERIAL MONITOR ({_matTracks.Count} props tracked, {_monitorSeconds.Value:0.#}s) ===");
            var changed = new List<MatTrack>();
            foreach (var t in _matTracks) if (t.Samples > 0 && t.Range >= _colorDelta.Value) changed.Add(t);
            changed.Sort((a, b) => b.Range.CompareTo(a.Range));

            sb.AppendLine($"--- {changed.Count} colour properties CHANGED (these are your strobe surfaces) ---");
            int shown = 0;
            var seenTransforms = new HashSet<string>();
            foreach (var t in changed)
            {
                if (shown++ >= 50) { sb.AppendLine("    (… truncated at 50)"); break; }
                sb.AppendLine($"* [{t.Source}] {t.Prop} range={t.Range:0.###}  shader={t.Shader}  {t.Path}");
                if (!IsNull(t.R) && seenTransforms.Add(t.Path))
                    DumpNearbyComponents(t.R.transform, sb, "    ");
            }
            if (external == null) WriteLog("mat-monitor", sb.ToString());
            _matTracks.Clear();
            LoggerMsg($"Material monitor DONE — {changed.Count} changed.");
        }

        // ============================================================ value reading
        private string ReadFieldValue(Il2CppFieldInfo f, Component comp, string ftName)
        {
            try
            {
                var val = f.GetValue(comp);
                if (val == null) return "null";

                if (ftName == "Color") return Fmt(val.Unbox<Color>());
                if (ftName == "Color32") { var c = val.Unbox<Color32>(); return $"32({c.r},{c.g},{c.b},{c.a})"; }
                if (ftName.StartsWith("Color") && ftName.Contains("[]")) return ReadColorArray(val);

                // primitives / enums / strings: ToString is safe and informative
                if (ftName is "Single" or "Int32" or "Boolean" or "Byte" or "Int64"
                    or "UInt32" or "Double" or "String" or "Vector3" or "Vector4")
                    return val.ToString();

                // enum or other struct — ToString often gives the name
                string s = val.ToString();
                return string.IsNullOrEmpty(s) ? $"<{ftName}>" : s;
            }
            catch { return $"<{ftName} unreadable>"; }
        }

        private string ReadColorArray(Il2CppSystem.Object val)
        {
            try
            {
                var arr = val.TryCast<Il2CppStructArray<Color>>();
                if (arr == null) return "<Color[] (uncast)>";
                var sb = new StringBuilder($"Color[{arr.Count}] {{ ");
                for (int i = 0; i < arr.Count; i++) sb.Append(Fmt(arr[i]) + (i < arr.Count - 1 ? ", " : " "));
                sb.Append("}");
                return sb.ToString();
            }
            catch { return "<Color[] unreadable>"; }
        }

        private static bool TryGetColor(Material m, string prop, out Color c)
        {
            c = default;
            try { if (m.HasProperty(prop)) { c = m.GetColor(prop); return true; } } catch { }
            return false;
        }

        // ============================================================ helpers
        private List<Light> CollectLights()
        {
            var list = new List<Light>();
            foreach (var root in AllRoots())
            {
                if (IsNull(root)) continue;
                foreach (var l in root.GetComponentsInChildren<Light>(true)) list.Add(l);
            }
            return list;
        }

        private IEnumerable<GameObject> AllRoots()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                foreach (var go in s.GetRootGameObjects()) yield return go;
            }
        }

        private IEnumerable<GameObject> AllSceneObjects()
        {
            foreach (var root in AllRoots())
            {
                if (IsNull(root)) continue;
                foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                    if (!IsNull(tr)) yield return tr.gameObject;
            }
        }

        private void DumpNearbyComponents(Transform t, StringBuilder sb, string indent)
        {
            var cur = t; int level = 0;
            while (!IsNull(cur) && level <= _parentLevels.Value)
            {
                sb.AppendLine($"{indent}components @ {(level == 0 ? "self" : "parent^" + level)} ({cur.name}):");
                foreach (var c in cur.GetComponents<Component>())
                    if (!IsNull(c)) sb.AppendLine($"{indent}  - {SafeTypeName(c)}");
                cur = cur.parent; level++;
            }
        }

        private void DumpColorFields(Component comp, StringBuilder sb, string indent)
        {
            try
            {
                Il2CppSystemType t = comp.GetIl2CppType();
                var flags = Il2CppBindingFlags.Public | Il2CppBindingFlags.NonPublic
                          | Il2CppBindingFlags.Instance | Il2CppBindingFlags.Static;
                foreach (var f in t.GetFields(flags))
                {
                    string ftName = SafeName(() => f.FieldType.Name);
                    if (!ftName.Contains("Color")) continue;
                    sb.AppendLine($"{indent}  {ftName} {f.Name} = {ReadFieldValue(f, comp, ftName)}");
                }
            }
            catch (Exception e) { sb.AppendLine($"{indent}  <field dump failed: {e.Message}>"); }
        }

        private static bool DeclaredOn(Il2CppSystemType declaring, string selfFullName)
        {
            try { return declaring != null && declaring.FullName == selfFullName; } catch { return true; }
        }

        private static bool MatchesKeyword(string typeName)
        {
            string lower = typeName.ToLowerInvariant();
            foreach (var k in Keywords) if (lower.Contains(k)) return true;
            return false;
        }

        private string SafeTypeName(Il2CppSystem.Object o)
        { try { return o.GetIl2CppType().FullName; } catch { try { return o.GetType().FullName; } catch { return "<unknown>"; } } }

        private static string SafeMethodSig(Il2CppMethodInfo m)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(SafeName(() => m.ReturnType.Name)).Append(' ').Append(m.Name).Append('(');
                var ps = m.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    sb.Append(SafeName(() => p.ParameterType.Name)).Append(' ').Append(p.Name);
                    if (i < ps.Length - 1) sb.Append(", ");
                }
                sb.Append(')');
                return sb.ToString();
            }
            catch { return "<method sig failed>"; }
        }

        private static string SafeName(Func<string> f) { try { return f() ?? "<?>"; } catch { return "<?>"; } }

        private static string GetPath(Transform t)
        {
            if (IsNull(t)) return "<null>";
            var sb = new StringBuilder(t.name);
            var p = t.parent;
            while (!IsNull(p)) { sb.Insert(0, p.name + "/"); p = p.parent; }
            return sb.ToString();
        }

        private static string Fmt(Color c) => $"({c.r:0.00},{c.g:0.00},{c.b:0.00},{c.a:0.00})";
        private static bool IsNull(UnityEngine.Object o) => o == null;
        private static bool IsNull(Il2CppSystem.Object o) => o == null;

        // ---- IO + input ----
        private void SafeRun(string label, Action<StringBuilder> work)
        { try { var sb = new StringBuilder(); work(sb); if (sb.Length > 0) WriteLog(label, sb.ToString()); } catch (Exception e) { LoggerError($"{label} failed: {e}"); } }
        private void SafeRun(string label, Action work)
        { try { work(); } catch (Exception e) { LoggerError($"{label} failed: {e}"); } }

        private void WriteLog(string label, string body)
        {
            LoggerMsg("\n" + body);
            try { string file = Path.Combine(_logDir, $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"); File.WriteAllText(file, body); LoggerMsg($"-> {file}"); }
            catch (Exception e) { LoggerError($"could not write log: {e.Message}"); }
        }

        private bool Key(KeyCode k)
        {
            if (_inputBroken) return false;
            try { return Input.GetKeyDown(k); }
            catch
            {
                _inputBroken = true;
                if (!_warnedInput) { _warnedInput = true; LoggerWarning("Legacy Input unavailable (new Input System exclusive). Tell me and I'll wire a new-Input binding."); }
                return false;
            }
        }

        private void LoggerMsg(string s) => MelonLogger.Msg(s);
        private void LoggerWarning(string s) => MelonLogger.Warning(s);
        private void LoggerError(string s) => MelonLogger.Error(s);

        private class Track
        {
            public Light Light; public string Path; public int Samples;
            private float _rMin = 1, _rMax = 0, _gMin = 1, _gMax = 0, _bMin = 1, _bMax = 0;
            private float _iMin = float.MaxValue, _iMax = float.MinValue;
            public void Observe(Color c, float intensity)
            {
                Samples++;
                if (c.r < _rMin) _rMin = c.r; if (c.r > _rMax) _rMax = c.r;
                if (c.g < _gMin) _gMin = c.g; if (c.g > _gMax) _gMax = c.g;
                if (c.b < _bMin) _bMin = c.b; if (c.b > _bMax) _bMax = c.b;
                if (intensity < _iMin) _iMin = intensity; if (intensity > _iMax) _iMax = intensity;
            }
            public float ColorRange => Math.Max(_rMax - _rMin, Math.Max(_gMax - _gMin, _bMax - _bMin));
            public float IntensityRange => (_iMax <= _iMin) ? 0 : _iMax - _iMin;
        }

        private class MatTrack
        {
            public Renderer R; public Material Mat; public string Prop; public string Path; public string Source; public string Shader;
            public int Samples;
            private float _rMin = 1, _rMax = 0, _gMin = 1, _gMax = 0, _bMin = 1, _bMax = 0;
            public void Observe(Color c)
            {
                Samples++;
                if (c.r < _rMin) _rMin = c.r; if (c.r > _rMax) _rMax = c.r;
                if (c.g < _gMin) _gMin = c.g; if (c.g > _gMax) _gMax = c.g;
                if (c.b < _bMin) _bMin = c.b; if (c.b > _bMax) _bMax = c.b;
            }
            public float Range => Math.Max(_rMax - _rMin, Math.Max(_gMax - _gMin, _bMax - _bMin));
        }
    }
}
