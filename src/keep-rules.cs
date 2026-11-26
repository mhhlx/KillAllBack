using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml;

namespace KillAllBack
{
    internal static class KeepRule
    {
        private const string FilePrefix = "exe=";
        private const string DirectoryPrefix = "dir=";

        internal static string File(string path)
        {
            string fullPath = FullPath(path);
            if (string.IsNullOrEmpty(Path.GetFileName(fullPath)) ||
                fullPath.TrimEnd('\\').Length == Path.GetPathRoot(fullPath).TrimEnd('\\').Length)
                throw new ArgumentException("请选择可执行文件的完整路径。", "path");
            return FilePrefix + fullPath;
        }

        internal static string Directory(string path)
        {
            string fullPath = FullPath(path);
            string root = Path.GetPathRoot(fullPath);
            return DirectoryPrefix + (fullPath.Length == root.Length ? root : fullPath.TrimEnd('\\'));
        }

        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("需要完整的可执行文件或目录路径。", "path");

            string value = CleanPath(path);
            if (!IsAbsolutePath(value))
                throw new ArgumentException("需要完整的可执行文件或目录路径。", "path");

            return Path.GetFullPath(value);
        }

        private static string CleanPath(string path)
        {
            string value = path.Trim();
            if (value.Length > 1 && value[0] == '"' && value[value.Length - 1] == '"')
                value = value.Substring(1, value.Length - 2);
            return value.Replace('/', '\\');
        }

        private static bool IsAbsolutePath(string path)
        {
            return path.StartsWith(@"\\", StringComparison.Ordinal) ||
                (path.Length >= 3 && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z'))
                    && path[1] == ':' && path[2] == '\\');
        }

        internal static bool HasPathPrefix(string rule)
        {
            return rule.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase)
                || rule.StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase);
        }

        internal static void ValidatePathRule(string rule)
        {
            bool file = rule.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase);
            string path = rule.Substring(file ? FilePrefix.Length : DirectoryPrefix.Length);
            if (file) File(path);
            else Directory(path);
        }

        private static bool TryGetPath(string rule, out string target, out bool file)
        {
            file = rule.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase);
            target = null;
            if (!file && !rule.StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase)) return false;

            string candidate = CleanPath(rule.Substring(file ? FilePrefix.Length : DirectoryPrefix.Length));
            if (!IsAbsolutePath(candidate)) return false;
            target = Path.GetFullPath(candidate);
            return true;
        }

        internal static bool TryMatch(string rule, string path, out bool matches)
        {
            string target;
            bool file;
            matches = false;
            if (!TryGetPath(rule, out target, out file)) return false;
            if (string.IsNullOrEmpty(path)) return true;

            string processPath = path.Replace('/', '\\');
            matches = file
                ? string.Equals(processPath, target, StringComparison.OrdinalIgnoreCase)
                : IsInDirectory(processPath, target);
            return true;
        }

        internal static bool IsInDirectory(string path, string directory)
        {
            return !string.IsNullOrEmpty(path)
                && path.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        internal static string Display(string rule)
        {
            string target;
            bool file;
            return TryGetPath(rule, out target, out file) ? target : rule;
        }

        internal static string ScopeLabel(string rule)
        {
            string target;
            bool file;
            if (TryGetPath(rule, out target, out file)) return file ? "仅此 exe" : "整个目录";
            return IsName(rule) ? "同名进程" : "路径包含";
        }

        private static readonly char[] PathCharacters = { '\\', '/', ':' };

        internal static bool IsName(string rule)
        {
            return rule.IndexOfAny(PathCharacters) < 0;
        }

        internal static bool Equivalent(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
            string leftPath, rightPath;
            bool leftFile, rightFile;
            if (TryGetPath(left, out leftPath, out leftFile) &&
                TryGetPath(right, out rightPath, out rightFile))
            {
                if (leftFile != rightFile) return false;
                if (!leftFile)
                {
                    leftPath = leftPath.TrimEnd('\\');
                    rightPath = rightPath.TrimEnd('\\');
                }
                return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
            }

            return IsName(left) && IsName(right) &&
                string.Equals(NameWithoutExtension(left), NameWithoutExtension(right), StringComparison.OrdinalIgnoreCase);
        }

        internal static string ForProcessName(string name)
        {
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        }

        internal static string NameWithoutExtension(string name)
        {
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4)
                : name;
        }
    }

    internal static class KeepRuleStore
    {
        internal static KeepConfig Read(string path)
        {
            var serializer = new JavaScriptSerializer();
            return serializer.ConvertToType<KeepConfig>(ParseDocument(File.ReadAllText(path, Encoding.UTF8), serializer));
        }

        private static Dictionary<string, object> ParseDocument(string original, JavaScriptSerializer serializer)
        {
            var data = serializer.DeserializeObject(original) as Dictionary<string, object>;
            if (data == null)
                throw new InvalidDataException("keep.json 中需要一个有效的配置对象。");

            // 字典会覆盖重名键，用保留各字段的 JSON 读取器检查原文。
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(original),
                XmlDictionaryReaderQuotas.Max))
            {
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    if (reader.Depth != 1 || reader.NodeType != XmlNodeType.Element) continue;
                    string name = (reader.GetAttribute("item") ?? reader.LocalName).ToLowerInvariant();
                    if (name != "system" && name != "user" && name != "services" &&
                        name != "graceful" && name != "networkrepair") continue;
                    if (!fields.Add(name))
                        throw new InvalidDataException("keep.json 中有重复的 " + name + " 字段，请核对配置。");
                }
            }

            foreach (var field in data)
            {
                string name = field.Key.ToLowerInvariant();
                if (name != "system" && name != "user" && name != "services" &&
                    name != "graceful" && name != "networkrepair") continue;
                if (field.Value == null) continue;

                if (name == "networkrepair")
                {
                    if (!(field.Value is bool))
                        throw new InvalidDataException("keep.json 中的 networkRepair 需要是布尔值。");
                }
                else
                {
                    var items = field.Value as IList;
                    if (items == null || items.Cast<object>().Any(item => item != null && !(item is string)))
                        throw new InvalidDataException("keep.json 中的 " + name + " 需要是字符串数组。");
                    if (name == "system" && items.Count > 0 && items.Cast<string>().All(string.IsNullOrWhiteSpace))
                        throw new InvalidDataException("keep.json 中的 system 只有空白条目。请修正规则或使用空数组恢复默认保护。");

                    if (name == "system" || name == "user")
                    {
                        for (int index = 0; index < items.Count; index++)
                            ValidateKeepRule(name + " 第 " + (index + 1) + " 项", items[index] as string);
                    }
                }
            }
            return data;
        }

        private static void ValidateKeepRule(string location, string value)
        {
            if (value == null || !KeepRule.HasPathPrefix(value.Trim())) return;
            try { KeepRule.ValidatePathRule(value.Trim()); }
            catch (ArgumentException ex) { throw InvalidPath(location, ex); }
            catch (NotSupportedException ex) { throw InvalidPath(location, ex); }
            catch (PathTooLongException ex) { throw InvalidPath(location, ex); }
        }

        private static InvalidDataException InvalidPath(string location, Exception inner)
        {
            return new InvalidDataException("keep.json 中的 " + location + " 需要有效的完整路径。", inner);
        }

        internal static bool Change(string path, string group, string rule, bool add)
        {
            return ChangeMany(path, group, new[] { rule }, add) != 0;
        }

        internal static int AddUser(string path, IEnumerable<string> rules)
        {
            return ChangeMany(path, "user", rules, true);
        }

        private static int ChangeMany(string path, string group, IEnumerable<string> changes, bool add)
        {
            if (group != "user" && group != "services" && group != "graceful")
                throw new ArgumentException("此规则组不可编辑。", "group");

            path = Path.GetFullPath(path);
            var serializer = new JavaScriptSerializer();
            using (new NamedGate("config:" + path, 2000))
            {
                string original = File.ReadAllText(path, Encoding.UTF8);
                var data = ParseDocument(original, serializer);
                string groupKey = data.Keys.FirstOrDefault(key => string.Equals(key, group, StringComparison.OrdinalIgnoreCase))
                    ?? group;

                object value;
                var rules = new List<string>();
                if (data.TryGetValue(groupKey, out value) && value != null)
                    rules.AddRange(((IList)value).Cast<string>());

                int changed = 0;
                foreach (string requested in changes)
                {
                    if (string.IsNullOrWhiteSpace(requested))
                        throw new ArgumentException("请输入规则。", "changes");

                    string rule = add ? requested.Trim() : requested;
                    if (add)
                    {
                        if (group == "user") ValidateKeepRule(group + " 新规则", rule);
                        bool exists = rules.Any(item => item != null && (group == "services"
                            ? string.Equals(item.Trim(), rule, StringComparison.OrdinalIgnoreCase)
                            : KeepRule.Equivalent(item.Trim(), rule)));
                        if (exists) continue;
                        rules.Add(rule);
                    }
                    else
                    {
                        int index = rules.FindIndex(item => string.Equals(item, rule, StringComparison.Ordinal));
                        if (index < 0) continue;
                        rules.RemoveAt(index);
                    }
                    changed++;
                }
                if (changed == 0) return 0;

                data[groupKey] = rules;
                string[] lines = data.Select(entry => "  " + serializer.Serialize(entry.Key) + ": " + serializer.Serialize(entry.Value)).ToArray();
                string next = "{" + Environment.NewLine + string.Join("," + Environment.NewLine, lines) + Environment.NewLine + "}" + Environment.NewLine;
                string backupDirectory = Path.Combine(Path.GetDirectoryName(path), "backups");
                Directory.CreateDirectory(backupDirectory);
                string backupName = Path.GetFileName(path) + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
                string backup = Path.Combine(backupDirectory, backupName);
                AtomicConfig.Replace(path, original, next, backup);
                return changed;
            }
        }
    }
}
