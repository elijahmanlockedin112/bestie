// PhoneFiles.cs - their computer's files from the phone: browse folders (starting at Desktop, Downloads, Documents...),
// open or save any file (served in byte ranges, so videos play and big downloads finish), and upload into the folder
// they're looking at. Only inside their user folder; never AppData, dot-folders (.ssh, .claude, Bestie's own .state) or
// hidden/system things. C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Bestie
{
    internal sealed partial class PhoneServer
    {
        static string Profile { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'); } }

        /// <summary>The places the browser starts from (the ones that exist).</summary>
        static List<KeyValuePair<string, string>> Places()
        {
            var list = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (label, path) => { if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) list.Add(new KeyValuePair<string, string>(label, path)); };
            add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            add("Downloads", Path.Combine(Profile, "Downloads"));
            add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
            add("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            add("Coding Projects", Path.Combine(Profile, "Downloads", "Coding Projects"));
            return list.Where(p => Browsable(p.Value)).ToList();
        }

        /// <summary>True if the phone may see this path: inside the user folder, no AppData, no dot-folder, nothing hidden.</summary>
        static bool Browsable(string full)
        {
            try
            {
                full = Path.GetFullPath(full).TrimEnd('\\');
                string root = Profile;
                if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                string cur = root;
                foreach (string part in full.Substring(root.Length + 1).Split('\\'))
                {
                    if (part.Length == 0 || part.StartsWith(".") || part.Equals("AppData", StringComparison.OrdinalIgnoreCase)) return false;
                    cur = Path.Combine(cur, part);
                    FileAttributes a = File.GetAttributes(cur);
                    if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) return false;
                    if ((a & FileAttributes.ReparsePoint) != 0 && (a & FileAttributes.Directory) != 0) return false;   // junctions can point anywhere
                }
                return true;
            }
            catch { return false; }
        }

        void Files(Request r, Stream s)
        {
            string want = r.Q("path") ?? "";
            if (want.Length == 0)
            {
                SendJson(s, new Dictionary<string, object>
                {
                    { "path", "" }, { "name", "This PC" }, { "parent", null },
                    { "entries", Places().Select(p => (object)new Dictionary<string, object> { { "name", p.Key }, { "path", p.Value }, { "dir", true } }).ToArray() },
                });
                return;
            }
            if (!Browsable(want) || !Directory.Exists(want)) { SendError(s, 404, "that folder isn't one i can show"); return; }
            string full = Path.GetFullPath(want).TrimEnd('\\');
            var dirs = new List<object>();
            var files = new List<KeyValuePair<DateTime, object>>();
            try
            {
                foreach (var d in new DirectoryInfo(full).EnumerateDirectories())
                {
                    if (dirs.Count >= 300) break;
                    if (d.Name.StartsWith(".") || (d.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0 || d.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    dirs.Add(new Dictionary<string, object> { { "name", d.Name }, { "path", d.FullName }, { "dir", true } });
                }
                foreach (var f in new DirectoryInfo(full).EnumerateFiles())
                {
                    if (files.Count >= 500) break;
                    if (f.Name.StartsWith(".") || (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    files.Add(new KeyValuePair<DateTime, object>(f.LastWriteTime, new Dictionary<string, object>
                    {
                        { "name", f.Name }, { "path", f.FullName }, { "dir", false }, { "size", f.Length },
                        { "modified", f.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) },
                    }));
                }
            }
            catch (Exception ex) { SendError(s, 500, "couldn't read that folder: " + ex.Message); return; }
            // folders a-z, then files newest first (what you just made or downloaded is at the top)
            var entries = dirs.OrderBy(d => Json.Str((Dictionary<string, object>)d, "name"), StringComparer.OrdinalIgnoreCase)
                .Concat(files.OrderByDescending(f => f.Key).Select(f => f.Value)).ToArray();
            string parent = Path.GetDirectoryName(full);
            bool isPlace = Places().Any(p => string.Equals(p.Value.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase));
            SendJson(s, new Dictionary<string, object>
            {
                { "path", full }, { "name", Path.GetFileName(full) },
                { "parent", isPlace || parent == null || !Browsable(parent) ? "" : parent },
                { "entries", entries },
            });
        }

        void FileGet(Request r, Stream s)
        {
            string want = r.Q("path") ?? "";
            if (!Browsable(want) || !File.Exists(want)) { SendText(s, 404, "that file isn't one i can send."); return; }
            if (r.Method == "GET" && (r.H("Range") ?? "").StartsWith("bytes=0-") || r.H("Range") == null)
                DebugLog.Write("phone: opening " + Path.GetFileName(want) + " on the phone");
            SendFileRanged(r, s, Path.GetFullPath(want), true);
        }

        /// <summary>Where an upload goes: the folder they're browsing (if allowed), else Downloads\From phone.</summary>
        static string UploadFolder(Request r, out string problem)
        {
            problem = null;
            string want = r.Q("dir");
            if (string.IsNullOrEmpty(want)) return FromPhoneDir;
            if (!Browsable(want) || !Directory.Exists(want)) { problem = "that folder isn't one i can put things in"; return null; }
            return Path.GetFullPath(want);
        }
    }
}
