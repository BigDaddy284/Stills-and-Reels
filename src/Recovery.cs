using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace StillsAndReels
{
    /// <summary>
    /// Keeps a copy of the video project being edited so it can be offered back after a crash or a power cut.
    ///
    /// Each running copy of the app has its own folder, %LOCALAPPDATA%\StillsAndReels\Recovery\&lt;process id&gt;,
    /// holding the project file, a note of where the project was last saved, and the media that only exists in
    /// the app's cache (recordings, freeze frames, reversed clips, pasted pictures): the cache is emptied when
    /// the app starts, so without them a recovered project would have holes. The folder is deleted when the
    /// project is saved or closed normally; one left behind by a process that no longer runs is a project to offer back.
    /// </summary>
    internal static class ProjectRecovery
    {
        const string ProjectFile = "project.srproj", OriginFile = "origin.txt", MediaFolder = "media";

        public static string Root
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StillsAndReels", "Recovery"); }
        }

        static string Own { get { return Path.Combine(Root, Process.GetCurrentProcess().Id.ToString()); } }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);

        /// <summary>A second name for the same file where the disk allows it (instant, no extra space), otherwise a copy.</summary>
        static void LinkOrCopy(string source, string target)
        {
            if (FFmpeg.IsWindows)
            {
                try { if (CreateHardLink(target, source, IntPtr.Zero)) return; } catch { }
            }
            File.Copy(source, target, true);
        }

        /// <summary>
        /// Writes the snapshot. temporary lists the media (by id) that only lives in the cache; those files are kept
        /// in the recovery folder and the snapshot points at them there. Blocks; call off the UI thread.
        /// </summary>
        public static void Write(Project snapshot, HashSet<int> temporary, string savedAs)
        {
            string dir = Own, mediaDir = Path.Combine(dir, MediaFolder);
            Directory.CreateDirectory(mediaDir);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in snapshot.Media)
            {
                if (!temporary.Contains(r.Id) || string.IsNullOrEmpty(r.Path) || !File.Exists(r.Path)) continue;
                // the id keeps two files with the same name apart
                string target = Path.Combine(mediaDir, r.Id + "-" + Path.GetFileName(r.Path));
                if (!File.Exists(target)) LinkOrCopy(r.Path, target);
                keep.Add(target);
                r.Path = target;
            }
            snapshot.Save(Path.Combine(dir, ProjectFile));
            File.WriteAllText(Path.Combine(dir, OriginFile), savedAs ?? "", new UTF8Encoding(false));
            foreach (var f in Directory.GetFiles(mediaDir))
                if (!keep.Contains(f)) { try { File.Delete(f); } catch { } }
        }

        /// <summary>Removes this process's recovery copy (the project was saved, closed or replaced).</summary>
        public static void Clear()
        {
            try { if (Directory.Exists(Own)) Directory.Delete(Own, true); } catch { }
        }

        public static void Discard(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
        }

        /// <summary>Recovery folders left by copies of the app that are no longer running, newest first.</summary>
        public static List<string> FindLeftBehind()
        {
            var found = new List<string>();
            try
            {
                if (!Directory.Exists(Root)) return found;
                int me = Process.GetCurrentProcess().Id;
                foreach (var sub in Directory.GetDirectories(Root))
                {
                    int pid;
                    if (!int.TryParse(Path.GetFileName(sub), out pid) || pid == me) continue;
                    bool alive = false;
                    try { using (var p = Process.GetProcessById(pid)) alive = !p.HasExited && (p.ProcessName.IndexOf("StillsAndReels", StringComparison.OrdinalIgnoreCase) >= 0 || p.ProcessName.IndexOf("mono", StringComparison.OrdinalIgnoreCase) >= 0); }
                    catch { alive = false; }
                    if (alive) continue;
                    if (File.Exists(Path.Combine(sub, ProjectFile))) found.Add(sub);
                    else Discard(sub);
                }
                found.Sort((a, b) => File.GetLastWriteTimeUtc(Path.Combine(b, ProjectFile)).CompareTo(File.GetLastWriteTimeUtc(Path.Combine(a, ProjectFile))));
            }
            catch { }
            return found;
        }

        public static DateTime SavedAt(string folder)
        {
            try { return File.GetLastWriteTime(Path.Combine(folder, ProjectFile)); } catch { return DateTime.Now; }
        }

        /// <summary>
        /// Reads a recovery copy. Its cache-only media is moved into this session's cache and listed in temporary
        /// (by path), so it is treated as it was before the crash. savedAs is where the project had been saved, or null.
        /// </summary>
        public static Project Read(string folder, out string savedAs, out HashSet<string> temporary)
        {
            var p = Project.Load(Path.Combine(folder, ProjectFile));
            savedAs = null;
            try
            {
                string origin = File.ReadAllText(Path.Combine(folder, OriginFile)).Trim();
                if (origin.Length > 0 && File.Exists(origin)) savedAs = origin;
            }
            catch { }
            temporary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string mediaDir = Path.Combine(folder, MediaFolder);
            foreach (var r in p.Media)
            {
                if (string.IsNullOrEmpty(r.Path) || !r.Path.StartsWith(mediaDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(r.Path)) continue;
                string name = Path.GetFileName(r.Path);
                int dash = name.IndexOf('-');
                if (dash > 0 && dash < 8) name = name.Substring(dash + 1);   // the id put in front by Write
                string target = Path.Combine(MediaLibrary.Folder, name);
                for (int i = 2; File.Exists(target); i++) target = Path.Combine(MediaLibrary.Folder, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
                try { File.Move(r.Path, target); }
                catch { File.Copy(r.Path, target, true); }
                r.Path = target;
                temporary.Add(target);
            }
            return p;
        }
    }

    /// <summary>The video projects opened or saved most recently, newest first, kept in %APPDATA%\StillsAndReels\recent-projects.txt.</summary>
    internal static class RecentProjects
    {
        const int Max = 10;

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StillsAndReels", "recent-projects.txt"); }
        }

        /// <summary>The list, without projects whose file has gone.</summary>
        public static List<string> List()
        {
            var list = new List<string>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string p = line.Trim();
                    if (p.Length == 0 || list.Exists(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) continue;
                    try { if (File.Exists(p)) list.Add(p); } catch { }
                    if (list.Count >= Max) break;
                }
            }
            catch { }
            return list;
        }

        public static void Add(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var list = List();
                list.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, path);
                if (list.Count > Max) list.RemoveRange(Max, list.Count - Max);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, list.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }
}
