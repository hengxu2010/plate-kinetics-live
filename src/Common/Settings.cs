// The program's settings (System\settings.json, GET/POST /api/settings) and the plate layouts people prepare before a
// run (System\layouts\<id>.json, /api/layouts), with the import of a layout from an Excel sheet that holds a plate grid.
//
// settings.json:
//   {"instruments": {"<key>": {"displayName": "Reader 1"}},          names shown on screen only (never in file names)
//    "notify": {"sound": true, "kinds": ["completed", "stopped", "interrupted", "stuck", "save-failed"]},
//    "keepAwake": true,                                               the PC does not sleep while a run is measuring
//    "nextExperiment": {"<key>": {"name": "...", "layoutId": "..."}}, used once, by the next run of that instrument
//    "operatorDefault": ""}                                           who runs the experiments ("" = the Windows user)
// A layout: {"id", "name", "plate": {"rows", "cols"}, "wells": {"A1": {"name", "group"}}, "updatedAt"}; a layout saved
// by the analysis page (its menu "Layouts", from 2026-11-26) can also have "groups": [{"name", "color"}] (the groups in
// order, with their colours), "excluded": ["A1", ...] (wells left out of the analysis) and "frames": {"width": "medium" |
// "thick", "groups": bool, "h": {"r:c": colour}, "v": {"r:c": colour}} (frame lines on the edges of the wells: h along
// the top of well (r, c), v along its left side; a colour "#rrggbb" or "w:A1", the colour of that well's group). Each of
// the three is written only when there is something in it, so a layout without them is stored as it always was.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace SAAControl
{
    public sealed class SettingsStore
    {
        readonly string file;
        readonly object gate = new object();
        Dictionary<string, object> current;

        public SettingsStore(string file)
        {
            this.file = file;
            current = Normalize(Load(file), null);
        }

        public string File => file;

        /// <summary>Layouts that exist (to check a next experiment's layout); null: not checked.</summary>
        public Func<string, bool> LayoutExists { get; set; }

        static Dictionary<string, object> Load(string file)
        {
            foreach (string path in new[] { file, file + SafeFile.BackupSuffix })
            {
                try
                {
                    if (global::System.IO.File.Exists(path) && Json.Parse(new UTF8Encoding(false, true).GetString(SafeFile.ReadAll(path))) is Dictionary<string, object> found)
                        return found;
                }
                catch (Exception error) { Log.Warning("the settings " + path + " cannot be read (" + error.Message + ")"); }
            }
            return new Dictionary<string, object>();
        }

        public static Dictionary<string, object> Defaults() => new Dictionary<string, object>
        {
            ["instruments"] = new Dictionary<string, object>(),
            ["notify"] = new Dictionary<string, object> { ["sound"] = true, ["kinds"] = Problems.EventKinds.Select(k => (object)k).ToList() },
            ["keepAwake"] = true,
            ["nextExperiment"] = new Dictionary<string, object>(),
            ["operatorDefault"] = "",
        };

        /// <summary>The settings as GET /api/settings gives them (a copy).</summary>
        public Dictionary<string, object> Get()
        {
            lock (gate) return (Dictionary<string, object>)Copy(current);
        }

        /// <summary>POST /api/settings: the items given replace those kept (each top-level item as a whole; "notify" item
        /// by item); unknown items are ignored. Throws ApiError(400) for a value that is not allowed. Returns the settings
        /// as saved.</summary>
        public Dictionary<string, object> Update(Dictionary<string, object> posted)
        {
            if (posted == null) throw new ApiError(400, "the settings must be a JSON object");
            lock (gate)
            {
                var next = (Dictionary<string, object>)Copy(current);
                foreach (var item in posted)
                {
                    if (item.Key == "notify" && item.Value is Dictionary<string, object> notify && next["notify"] is Dictionary<string, object> kept)
                        foreach (var part in notify) kept[part.Key] = part.Value;
                    else if (next.ContainsKey(item.Key)) next[item.Key] = item.Value;
                }
                next = Normalize(next, posted);
                Save(next);
                current = next;
                return (Dictionary<string, object>)Copy(current);
            }
        }

        void Save(Dictionary<string, object> settings)
        {
            try { SafeFile.WriteJson(file, settings); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new ApiError(500, "the settings could not be saved (" + error.Message + ")");
            }
        }

        /// <summary>The settings with every item there and of the right kind. posted: what was sent (its wrong values are
        /// refused with ApiError(400)); null: what was read from the file (wrong values fall back to the defaults).</summary>
        Dictionary<string, object> Normalize(Dictionary<string, object> raw, Dictionary<string, object> posted)
        {
            bool strict = posted != null;
            var result = Defaults();
            void Refuse(string message) { if (strict) throw new ApiError(400, message); }

            if (raw.TryGetValue("instruments", out object instruments))
            {
                var names = (Dictionary<string, object>)result["instruments"];
                if (instruments is Dictionary<string, object> map)
                    foreach (var entry in map)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 64) { Refuse("an instrument key is not allowed: " + entry.Key); continue; }
                        string shown = entry.Value is Dictionary<string, object> d && d.TryGetValue("displayName", out object n) ? n as string : null;
                        if (entry.Value is Dictionary<string, object> dd && dd.TryGetValue("displayName", out object nn) && nn != null && !(nn is string))
                            Refuse("displayName must be text");
                        shown = Plain(shown, 60);
                        if (shown.Length > 0) names[entry.Key] = new Dictionary<string, object> { ["displayName"] = shown };
                    }
                else if (instruments != null) Refuse("instruments must be an object");
            }
            if (raw.TryGetValue("notify", out object notifyValue))
            {
                var notify = (Dictionary<string, object>)result["notify"];
                if (notifyValue is Dictionary<string, object> n)
                {
                    if (n.TryGetValue("sound", out object sound))
                    {
                        if (sound is bool b) notify["sound"] = b;
                        else Refuse("notify.sound must be true or false");
                    }
                    if (n.TryGetValue("kinds", out object kinds))
                    {
                        if (kinds is List<object> list)
                        {
                            var chosen = new List<object>();
                            foreach (object kind in list)
                            {
                                if (kind is string k && Problems.EventKinds.Contains(k)) { if (!chosen.Contains(k)) chosen.Add(k); }
                                else Refuse("notify.kinds may hold only " + string.Join(", ", Problems.EventKinds));
                            }
                            notify["kinds"] = Problems.EventKinds.Where(k => chosen.Contains(k)).Select(k => (object)k).ToList();
                        }
                        else Refuse("notify.kinds must be a list");
                    }
                }
                else if (notifyValue != null) Refuse("notify must be an object");
            }
            if (raw.TryGetValue("keepAwake", out object awake))
            {
                if (awake is bool b) result["keepAwake"] = b;
                else Refuse("keepAwake must be true or false");
            }
            if (raw.TryGetValue("nextExperiment", out object nextValue))
            {
                var next = (Dictionary<string, object>)result["nextExperiment"];
                if (nextValue is Dictionary<string, object> map)
                    foreach (var entry in map)
                    {
                        if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 64) { Refuse("an instrument key is not allowed: " + entry.Key); continue; }
                        if (!(entry.Value is Dictionary<string, object> plan)) { if (entry.Value != null) Refuse("nextExperiment." + entry.Key + " must be an object"); continue; }
                        string name = Plain(plan.TryGetValue("name", out object nm) ? nm as string : null, 120);
                        string layout = (plan.TryGetValue("layoutId", out object l) ? l as string : null) ?? "";
                        if (layout.Length > 0 && !LayoutStore.IsId(layout)) { Refuse("not a layout id: " + layout); layout = ""; }
                        if (layout.Length > 0 && strict && LayoutExists != null && !LayoutExists(layout))
                        {
                            // a plan that asks for a layout that is not there is refused; the plan that is kept, whose layout
                            // was deleted since, goes on without the layout (else every later change of any setting would
                            // be refused for it)
                            if (KeptLayout(entry.Key) == layout) layout = "";
                            else Refuse("there is no layout " + layout);
                        }
                        if (name.Length == 0 && layout.Length == 0) continue;
                        next[entry.Key] = new Dictionary<string, object> { ["name"] = name, ["layoutId"] = layout };
                    }
                else if (nextValue != null) Refuse("nextExperiment must be an object");
            }
            if (raw.TryGetValue("operatorDefault", out object op))
            {
                if (op == null || op is string) result["operatorDefault"] = Plain(op as string, 100);
                else Refuse("operatorDefault must be text");
            }
            return result;
        }

        /// <summary>The layout of the plan that is kept for an instrument's next experiment; "" when there is none.</summary>
        string KeptLayout(string key) =>
            current != null && current.TryGetValue("nextExperiment", out object kept) && kept is Dictionary<string, object> plans
            && plans.TryGetValue(key, out object value) && value is Dictionary<string, object> plan
            && plan.TryGetValue("layoutId", out object layout) ? layout as string ?? "" : "";

        /// <summary>Text without control characters, trimmed, at most `length` characters.</summary>
        public static string Plain(string text, int length)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text) sb.Append(char.IsControl(c) ? ' ' : c);
            string s = sb.ToString().Trim();
            if (s.Length > length) s = s.Substring(0, char.IsHighSurrogate(s[length - 1]) ? length - 1 : length).TrimEnd();
            return s;
        }

        static object Copy(object value)
        {
            switch (value)
            {
                case Dictionary<string, object> d: return d.ToDictionary(e => e.Key, e => Copy(e.Value));
                case List<object> l: return l.Select(Copy).ToList();
                default: return value;
            }
        }

        // ---- what the parts of the program read

        T Item<T>(string name)
        {
            lock (gate) return current.TryGetValue(name, out object v) && v is T t ? t : default(T);
        }

        public bool KeepAwake => Item<bool>("keepAwake");

        public bool NotifySound
        {
            get
            {
                var notify = Item<Dictionary<string, object>>("notify");
                return notify != null && notify.TryGetValue("sound", out object s) && s is bool b && b;
            }
        }

        public List<string> NotifyKinds
        {
            get
            {
                var notify = Item<Dictionary<string, object>>("notify");
                return notify != null && notify.TryGetValue("kinds", out object k) && k is List<object> l ? l.OfType<string>().ToList() : new List<string>();
            }
        }

        public string OperatorDefault => Item<string>("operatorDefault") ?? "";

        /// <summary>The name shown for an instrument; null: none was given.</summary>
        public string DisplayName(string key)
        {
            var names = Item<Dictionary<string, object>>("instruments");
            return key != null && names != null && names.TryGetValue(key, out object v) && v is Dictionary<string, object> d
                   && d.TryGetValue("displayName", out object n) ? n as string : null;
        }

        /// <summary>The plan for the next run of an instrument, taken (it is used once): (name, layout id); null: none.</summary>
        public (string Name, string LayoutId)? TakeNextExperiment(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (gate)
            {
                if (!(current["nextExperiment"] is Dictionary<string, object> next) || !next.TryGetValue(key, out object v)
                    || !(v is Dictionary<string, object> plan)) return null;
                var taken = ((plan.TryGetValue("name", out object n) ? n as string : null) ?? "", (plan.TryGetValue("layoutId", out object l) ? l as string : null) ?? "");
                var after = (Dictionary<string, object>)Copy(current);
                ((Dictionary<string, object>)after["nextExperiment"]).Remove(key);
                try
                {
                    SafeFile.WriteJson(file, after);
                    current = after;
                }
                catch (Exception error)
                {
                    Log.Warning("the settings could not be saved after the next experiment of " + key + " was used (" + error.Message + ")");
                    current = after;                     // used once all the same
                }
                return taken;
            }
        }
    }

    public sealed class LayoutStore
    {
        readonly string folder;
        readonly object gate = new object();
        static readonly Regex Id = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

        public LayoutStore(string folder) { this.folder = folder; }

        public static bool IsId(string id) => id != null && Id.IsMatch(id);

        string PathOf(string id) => Path.Combine(folder, id + ".json");

        public bool Exists(string id) => IsId(id) && File.Exists(PathOf(id));

        /// <summary>GET /api/layouts: every layout, by name.</summary>
        public List<object> List()
        {
            var found = new List<Dictionary<string, object>>();
            lock (gate)
            {
                try
                {
                    if (Directory.Exists(folder))
                        foreach (string path in Directory.GetFiles(folder, "*.json"))
                        {
                            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                            var layout = Read(path);
                            if (layout != null) found.Add(layout);
                        }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
            }
            return found.OrderBy(l => l["name"] as string ?? "", StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(l => l["id"] as string ?? "", StringComparer.Ordinal).Select(l => (object)l).ToList();
        }

        public Dictionary<string, object> Get(string id)
        {
            if (!IsId(id)) return null;
            lock (gate) return Read(PathOf(id));
        }

        static Dictionary<string, object> Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                if (!(Json.Parse(new UTF8Encoding(false, true).GetString(SafeFile.ReadAll(path))) is Dictionary<string, object> raw)) return null;
                var layout = Normalize(raw, false);
                layout["id"] = Path.GetFileNameWithoutExtension(path);
                layout["updatedAt"] = raw.TryGetValue("updatedAt", out object at) ? at as string : null;
                return layout;
            }
            catch (Exception) { return null; }
        }

        /// <summary>POST /api/layouts: one layout (empty or no id: a new one). Returns its id.</summary>
        public string Save(Dictionary<string, object> posted)
        {
            if (posted == null) throw new ApiError(400, "a layout must be a JSON object");
            string id = posted.TryGetValue("id", out object i) ? i as string ?? "" : "";
            if (id.Length > 0 && !IsId(id)) throw new ApiError(400, "not a layout id: " + id);
            var layout = Normalize(posted, true);
            lock (gate)
            {
                if (id.Length == 0) id = Guid.NewGuid().ToString("N");
                layout["id"] = id;
                layout["updatedAt"] = Clock.Now();
                try { SafeFile.WriteJson(PathOf(id), layout); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    throw new ApiError(500, "the layout could not be saved (" + error.Message + ")");
                }
            }
            return id;
        }

        /// <summary>POST /api/layouts/delete?id=: false when there is no such layout.</summary>
        public bool Delete(string id)
        {
            if (!IsId(id)) throw new ApiError(400, "not a layout id: " + id);
            lock (gate)
            {
                string path = PathOf(id);
                if (!File.Exists(path)) return false;
                try
                {
                    File.Delete(path);
                    if (File.Exists(path + SafeFile.BackupSuffix))
                    {
                        File.SetAttributes(path + SafeFile.BackupSuffix, FileAttributes.Normal);
                        File.Delete(path + SafeFile.BackupSuffix);
                    }
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    throw new ApiError(500, "the layout could not be deleted (" + error.Message + ")");
                }
                return true;
            }
        }

        /// <summary>A layout in its stored form: {"id", "name", "plate": {"rows", "cols"}, "wells": {"A1": {"name",
        /// "group"}}, "updatedAt"}. strict: wrong values are refused (ApiError 400), else left out.</summary>
        public static Dictionary<string, object> Normalize(Dictionary<string, object> raw, bool strict)
        {
            void Refuse(string message) { if (strict) throw new ApiError(400, message); }
            string name = SettingsStore.Plain(raw.TryGetValue("name", out object n) ? n as string : null, 200);
            if (name.Length == 0) name = "Layout";
            long rows = 8, cols = 12;
            if (raw.TryGetValue("plate", out object p) && p is Dictionary<string, object> plate)
            {
                rows = plate.TryGetValue("rows", out object r) && r is long lr ? lr : 0;
                cols = plate.TryGetValue("cols", out object c) && c is long lc ? lc : 0;
                if (!((rows == 8 && cols == 12) || (rows == 16 && cols == 24)))
                {
                    Refuse("the plate must have 8 x 12 or 16 x 24 wells");
                    rows = 8; cols = 12;
                }
            }
            else Refuse("a layout needs its plate {rows, cols}");
            var wells = new Dictionary<string, object>();
            if (raw.TryGetValue("wells", out object w) && w is Dictionary<string, object> given)
            {
                foreach (var well in OrderedWells(given.Keys, rows, cols, Refuse))
                {
                    var cell = given[well] as Dictionary<string, object>;
                    if (cell == null) { if (given[well] != null) Refuse("well " + well + " must be an object"); continue; }
                    string sample = SettingsStore.Plain(cell.TryGetValue("name", out object s) ? s as string : null, 200);
                    string group = SettingsStore.Plain(cell.TryGetValue("group", out object g) ? g as string : null, 100);
                    if (sample.Length == 0 && group.Length == 0) continue;
                    wells[well] = new Dictionary<string, object> { ["name"] = sample, ["group"] = group };
                }
            }
            else if (raw.ContainsKey("wells") && raw["wells"] != null) Refuse("wells must be an object");
            var layout = new Dictionary<string, object>
            {
                ["id"] = raw.TryGetValue("id", out object id) ? id as string ?? "" : "",
                ["name"] = name,
                ["plate"] = new Dictionary<string, object> { ["rows"] = rows, ["cols"] = cols },
                ["wells"] = wells,
            };
            // what the analysis page adds (only when there is something in it)
            var groups = GroupColors(raw, wells, Refuse);
            if (groups.Count > 0) layout["groups"] = groups;
            var excluded = ExcludedWells(raw, rows, cols, Refuse);
            if (excluded.Count > 0) layout["excluded"] = excluded;
            var frames = Frames(raw, rows, cols, Refuse);
            if (frames != null) layout["frames"] = frames;
            layout["updatedAt"] = null;
            return layout;
        }

        static readonly Regex GroupColor = new Regex("^(?:#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})|slot:[1-9][0-9]{0,3}|ramp:#[0-9a-fA-F]{6}:[0-9]{1,3}:[1-9][0-9]{0,2}|mark:[1-4])$", RegexOptions.CultureInvariant);
        static readonly Regex EdgeKey = new Regex("^([0-9]{1,2}):([0-9]{1,2})$", RegexOptions.CultureInvariant);
        static readonly Regex LineColor = new Regex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);

        /// <summary>"groups": the groups in order with their colours, [{"name", "color"}]; only groups some well is in, each
        /// name once, at most 400. A colour as the page writes them: "#rgb", "#rrggbb", "slot:n", "ramp:#rrggbb:i:n" or
        /// "mark:1" to "mark:4" (a group of a grouping layer without colours: filled, hollow, half filled, dotted).</summary>
        static List<object> GroupColors(Dictionary<string, object> raw, Dictionary<string, object> wells, Action<string> refuse)
        {
            var list = new List<object>();
            if (!raw.TryGetValue("groups", out object value) || value == null) return list;
            if (!(value is List<object> given)) { refuse("groups must be a list"); return list; }
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cell in wells.Values.OfType<Dictionary<string, object>>())
                if (cell["group"] is string g && g.Length > 0) used.Add(g);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in given)
            {
                if (!(item is Dictionary<string, object> group)) { refuse("a group must be an object"); continue; }
                string name = SettingsStore.Plain(group.TryGetValue("name", out object n) ? n as string : null, 100);
                object c = group.TryGetValue("color", out object found) ? found : null;
                string color = c as string;
                if (c != null && (color == null || !GroupColor.IsMatch(color))) { refuse("not a colour of a group: " + c); color = null; }
                if (name.Length == 0 || !used.Contains(name) || seen.Contains(name) || list.Count >= 400) continue;
                seen.Add(name);
                var kept = new Dictionary<string, object> { ["name"] = name };
                if (color != null) kept["color"] = color;
                list.Add(kept);
            }
            return list;
        }

        /// <summary>"excluded": wells of the plate, each once, in plate order.</summary>
        static List<object> ExcludedWells(Dictionary<string, object> raw, long rows, long cols, Action<string> refuse)
        {
            if (!raw.TryGetValue("excluded", out object value) || value == null) return new List<object>();
            if (!(value is List<object> given)) { refuse("excluded must be a list of wells"); return new List<object>(); }
            var ids = new List<string>();
            foreach (object item in given)
            {
                if (!(item is string id)) { refuse("not a well of the plate: " + item); continue; }
                if (!ids.Contains(id)) ids.Add(id);
            }
            return OrderedWells(ids, rows, cols, refuse).Select(id => (object)id).ToList();
        }

        /// <summary>"frames": {"width", "groups", "h", "v"}; null when it has no line and its groups are off. An edge "r:c" of h
        /// lies on the plate when r is at most rows and c below cols, one of v when r is below rows and c at most cols.</summary>
        static Dictionary<string, object> Frames(Dictionary<string, object> raw, long rows, long cols, Action<string> refuse)
        {
            if (!raw.TryGetValue("frames", out object value) || value == null) return null;
            if (!(value is Dictionary<string, object> given)) { refuse("frames must be an object"); return null; }
            string width = "medium";
            if (given.TryGetValue("width", out object w) && w != null)
            {
                if (w is string s && (s == "medium" || s == "thick")) width = s;
                else refuse("frames.width must be medium or thick");
            }
            bool groups = false;
            if (given.TryGetValue("groups", out object g) && g != null)
            {
                if (g is bool b) groups = b;
                else refuse("frames.groups must be true or false");
            }
            var h = FrameLines(given, "h", rows, cols, refuse);
            var v = FrameLines(given, "v", rows, cols, refuse);
            if (!groups && h.Count == 0 && v.Count == 0) return null;
            return new Dictionary<string, object> { ["width"] = width, ["groups"] = groups, ["h"] = h, ["v"] = v };
        }

        static Dictionary<string, object> FrameLines(Dictionary<string, object> frames, string side, long rows, long cols, Action<string> refuse)
        {
            var found = new List<(int Row, int Col, string Color)>();
            if (frames.TryGetValue(side, out object value) && value != null)
            {
                if (!(value is Dictionary<string, object> lines)) refuse("frames." + side + " must be an object");
                else
                    foreach (var line in lines)
                    {
                        var m = EdgeKey.Match(line.Key);
                        int r = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
                        int c = m.Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : -1;
                        bool onPlate = m.Success && (side == "h" ? r <= rows && c < cols : r < rows && c <= cols);
                        string color = line.Value as string;
                        bool known = color != null && (LineColor.IsMatch(color)
                                     || (color.StartsWith("w:", StringComparison.Ordinal) && TryWell(color.Substring(2), out int wr, out int wc) && wr < rows && wc < cols));
                        if (!onPlate) refuse("frames." + side + ": not an edge of the plate: " + line.Key);
                        else if (!known) refuse("frames." + side + "." + line.Key + ": not a colour of a frame: " + line.Value);
                        else if (!found.Any(x => x.Row == r && x.Col == c)) found.Add((r, c, color[0] == '#' ? color.ToLowerInvariant() : color));
                    }
            }
            var result = new Dictionary<string, object>();
            foreach (var x in found.OrderBy(x => x.Row).ThenBy(x => x.Col))
                result[x.Row.ToString(CultureInfo.InvariantCulture) + ":" + x.Col.ToString(CultureInfo.InvariantCulture)] = x.Color;
            return result;
        }

        /// <summary>The well ids of the plate that are given, in plate order (A1, A2, ..., B1, ...).</summary>
        static IEnumerable<string> OrderedWells(IEnumerable<string> given, long rows, long cols, Action<string> refuse)
        {
            var found = new List<(int Row, int Col, string Id)>();
            foreach (string id in given)
            {
                if (TryWell(id, out int row, out int col) && row < rows && col < cols) found.Add((row, col, id));
                else refuse("not a well of the plate: " + id);
            }
            return found.OrderBy(f => f.Row).ThenBy(f => f.Col).Select(f => f.Id);
        }

        /// <summary>"A1" -> row 0, column 0; "P24" -> 15, 23.</summary>
        public static bool TryWell(string id, out int row, out int col)
        {
            row = col = -1;
            if (string.IsNullOrEmpty(id) || id.Length < 2 || id.Length > 3) return false;
            char letter = id[0];
            if (letter < 'A' || letter > 'P') return false;
            if (!int.TryParse(id.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1 || number > 24) return false;
            if (id.Substring(1) != number.ToString(CultureInfo.InvariantCulture)) return false;     // no "A01"
            row = letter - 'A';
            col = number - 1;
            return true;
        }

        // ---- a layout from an Excel sheet

        /// <summary>POST /api/layouts/import-xlsx: the first plate grid found in the workbook - a row with the column numbers
        /// 1-12 (or 1-24) and, in the column before them, the row letters A-H (or A-P) - as a layout (not saved). A second
        /// grid of the same size below it gives the groups. Throws ApiError(400) when there is none.</summary>
        public static Dictionary<string, object> ImportXlsx(byte[] bytes)
        {
            List<(string Name, Dictionary<(int, int), string> Cells)> sheets;
            try { sheets = XlsxReader.Sheets(bytes); }
            catch (Exception error) when (!(error is ApiError))
            {
                throw new ApiError(400, "not an Excel workbook (.xlsx): " + error.Message);
            }
            foreach (var sheet in sheets)
            {
                var grids = Grids(sheet.Cells);
                if (grids.Count == 0) continue;
                var first = grids[0];
                var groups = grids.Skip(1).FirstOrDefault(g => g.Rows == first.Rows && g.Cols == first.Cols && g.Top > first.Top);
                var wells = new Dictionary<string, object>();
                for (int r = 0; r < first.Rows; r++)
                    for (int c = 0; c < first.Cols; c++)
                    {
                        string sample = SettingsStore.Plain(Cell(sheet.Cells, first.Top + 1 + r, first.Left + 1 + c), 200);
                        string group = groups.Rows > 0 ? SettingsStore.Plain(Cell(sheet.Cells, groups.Top + 1 + r, groups.Left + 1 + c), 100) : "";
                        if (sample.Length == 0 && group.Length == 0) continue;
                        wells[((char)('A' + r)).ToString() + (c + 1).ToString(CultureInfo.InvariantCulture)] =
                            new Dictionary<string, object> { ["name"] = sample, ["group"] = group };
                    }
                return new Dictionary<string, object>
                {
                    ["id"] = "", ["name"] = SettingsStore.Plain(sheet.Name, 200).Length > 0 ? SettingsStore.Plain(sheet.Name, 200) : "Layout",
                    ["plate"] = new Dictionary<string, object> { ["rows"] = (long)first.Rows, ["cols"] = (long)first.Cols },
                    ["wells"] = wells, ["updatedAt"] = null,
                };
            }
            throw new ApiError(400, "No plate grid was found: a sheet needs the column numbers 1-12 (or 1-24) in one row and the row letters A-H (or A-P) in the column before them.");
        }

        static string Cell(Dictionary<(int, int), string> cells, int row, int col) => cells.TryGetValue((row, col), out string v) ? v : "";

        /// <summary>The plate grids of a sheet, top first: (top: the row of the numbers, left: the column of the letters).</summary>
        static List<(int Top, int Left, int Rows, int Cols)> Grids(Dictionary<(int, int), string> cells)
        {
            var found = new List<(int, int, int, int)>();
            foreach (var cell in cells.OrderBy(c => c.Key.Item1).ThenBy(c => c.Key.Item2))
            {
                if (Number(cell.Value) != 1) continue;
                int top = cell.Key.Item1, first = cell.Key.Item2;
                int count = 1;
                while (Number(Cell(cells, top, first + count)) == count + 1) count++;
                int cols = count >= 24 ? 24 : count >= 12 ? 12 : 0;
                if (cols == 0 || first == 0) continue;
                int rows = cols == 24 ? 16 : 8;
                bool letters = true;
                for (int r = 0; r < rows && letters; r++)
                    letters = string.Equals(Cell(cells, top + 1 + r, first - 1).Trim(), ((char)('A' + r)).ToString(), StringComparison.OrdinalIgnoreCase);
                if (!letters && cols == 24)
                {
                    // the numbers go on to 24 but the letters only to H: a 96-well grid with more columns beside it
                    cols = 12; rows = 8; letters = true;
                    for (int r = 0; r < rows && letters; r++)
                        letters = string.Equals(Cell(cells, top + 1 + r, first - 1).Trim(), ((char)('A' + r)).ToString(), StringComparison.OrdinalIgnoreCase);
                }
                if (letters) found.Add((top, first - 1, rows, cols));
            }
            return found;
        }

        static int Number(string text) =>
            int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n
            : double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d == Math.Floor(d) && d < 1000 ? (int)d : -1;
    }

    /// <summary>Reads the cells of an .xlsx workbook as text (shared strings, inline strings, numbers, booleans).</summary>
    public static class XlsxReader
    {
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const long MaxPart = 64L << 20;
        const int MaxDepth = 64;                 // elements in one another (a real sheet: about seven)

        /// <summary>The sheets in the workbook's order: (name, (row, column) -> text), rows and columns from 0.</summary>
        public static List<(string Name, Dictionary<(int, int), string> Cells)> Sheets(byte[] bytes)
        {
            var result = new List<(string, Dictionary<(int, int), string>)>();
            using (var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read))
            {
                var shared = SharedStrings(zip);
                var targets = new Dictionary<string, string>(StringComparer.Ordinal);
                var rels = Load(zip, "xl/_rels/workbook.xml.rels");
                if (rels != null)
                    foreach (XmlElement r in rels.GetElementsByTagName("Relationship"))
                    {
                        string target = r.GetAttribute("Target").Replace('\\', '/');
                        if (target.StartsWith("/")) target = target.Substring(1);
                        else if (!target.StartsWith("xl/")) target = "xl/" + target;
                        targets[r.GetAttribute("Id")] = target;
                    }
                var workbook = Load(zip, "xl/workbook.xml") ?? throw new InvalidDataException("no xl/workbook.xml");
                foreach (XmlElement sheet in workbook.GetElementsByTagName("sheet", Main))
                {
                    string id = sheet.GetAttribute("id", Rel);
                    if (!targets.TryGetValue(id, out string part)) continue;
                    var xml = Load(zip, part);
                    if (xml == null) continue;
                    result.Add((sheet.GetAttribute("name"), Cells(xml, shared)));
                }
            }
            return result;
        }

        static XmlDocument Load(ZipArchive zip, string part)
        {
            ZipArchiveEntry entry = zip.GetEntry(part);
            if (entry == null) return null;
            // (never more bytes than the entry says it holds: PkFile.EntryBytes)
            byte[] bytes = PkFile.EntryBytes(entry, MaxPart, part);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            // The text of an element is put together by a call for every level inside it (InnerText): elements nested
            // without end would use up the stack, and that ends the whole program. So the part is read through once first.
            using (XmlReader reader = XmlReader.Create(new MemoryStream(bytes, false), settings))
                while (reader.Read())
                    if (reader.Depth > MaxDepth) throw new InvalidDataException(part + " is nested too deeply");
            var doc = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(new MemoryStream(bytes, false), settings))
                doc.Load(reader);
            return doc;
        }

        static List<string> SharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            var doc = Load(zip, "xl/sharedStrings.xml");
            if (doc == null) return list;
            foreach (XmlElement si in doc.GetElementsByTagName("si", Main)) list.Add(Text(si));
            return list;
        }

        /// <summary>The text of a string item: its &lt;t&gt; elements, without the phonetic runs (&lt;rPh&gt;).</summary>
        static string Text(XmlElement item)
        {
            var sb = new StringBuilder();
            foreach (XmlElement t in item.GetElementsByTagName("t", Main))
            {
                if (t.ParentNode is XmlElement parent && parent.LocalName == "rPh") continue;
                sb.Append(t.InnerText);
            }
            return sb.ToString();
        }

        static Dictionary<(int, int), string> Cells(XmlDocument sheet, List<string> shared)
        {
            var cells = new Dictionary<(int, int), string>();
            int rowIndex = -1;
            foreach (XmlElement row in sheet.GetElementsByTagName("row", Main))
            {
                rowIndex = int.TryParse(row.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out int r) ? r - 1 : rowIndex + 1;
                int colIndex = -1;
                foreach (XmlNode node in row.ChildNodes)
                {
                    if (!(node is XmlElement c) || c.LocalName != "c") continue;
                    var reference = Reference(c.GetAttribute("r"));
                    int col = reference.HasValue ? reference.Value.Col : colIndex + 1;
                    int at = reference.HasValue ? reference.Value.Row : rowIndex;
                    colIndex = col;
                    string type = c.GetAttribute("t");
                    string value;
                    if (type == "inlineStr") value = c.GetElementsByTagName("is", Main).Count > 0 ? Text((XmlElement)c.GetElementsByTagName("is", Main)[0]) : "";
                    else
                    {
                        var v = c.GetElementsByTagName("v", Main);
                        string raw = v.Count > 0 ? v[0].InnerText : "";
                        if (type == "s") value = int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < shared.Count ? shared[index] : "";
                        else if (type == "b") value = raw == "1" ? "TRUE" : "FALSE";
                        else if (type == "e") value = "";
                        else if (type == "str") value = raw;
                        else if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                            value = d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : Json.FormatDouble(d);
                        else value = raw;
                    }
                    if (value.Length > 0) cells[(at, col)] = value;
                }
            }
            return cells;
        }

        /// <summary>"B3" -> row 2, column 1.</summary>
        static (int Row, int Col)? Reference(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            int i = 0, col = 0;
            while (i < reference.Length && char.IsLetter(reference[i])) { col = col * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1); i++; }
            if (i == 0 || i == reference.Length) return null;
            if (!int.TryParse(reference.Substring(i), NumberStyles.None, CultureInfo.InvariantCulture, out int row) || row < 1) return null;
            return (row - 1, col - 1);
        }
    }
}
