using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FbtCal
{
    // Follows VRChat's output log and reports when FBT calibration starts and ends (port of app/vrclog.py).
    // Needs VRChat's --enable-ik-debug-logging launch option; the lines look like:
    //   2026.10.25 20:45:00 Debug      -  [VRCTrackingManager] [IK Debug Log] Preparing for calibration...
    //   2026.10.25 20:45:07 Debug      -  [VRCTrackingManager] [IK Debug Log] Calibrating trackers...
    public sealed class VrcLogWatcher
    {
        public const string Start = "start", End = "end", NewLog = "newlog";
        const string IkTag = "[IK Debug Log]";
        const string StartLine = "[IK Debug Log] Preparing for calibration...";
        static readonly string[] EndLines = { "[IK Debug Log] Calibrating trackers...", "[IK Debug Log] Applying calibration to avatar..." };

        public static string DefaultDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "VRChat", "VRChat");

        readonly string _folder;
        long _offset;
        byte[] _partial = Array.Empty<byte>();

        public string CurrentPath { get; private set; }
        public bool IkDebugSeen { get; private set; }

        public VrcLogWatcher(string folder)
        {
            _folder = folder;
            Attach(NewestLog(), fromEnd: true);
        }

        string NewestLog()
        {
            if (!Directory.Exists(_folder)) return null;
            return Directory.GetFiles(_folder, "output_log_*.txt").OrderBy(p => p, StringComparer.Ordinal).LastOrDefault();  // names sort by date
        }

        static FileStream OpenShared(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        void Attach(string path, bool fromEnd)
        {
            CurrentPath = path;
            _partial = Array.Empty<byte>();
            _offset = 0;
            IkDebugSeen = false;
            if (path == null) return;
            try
            {
                using var fs = OpenShared(path);
                if (fromEnd) _offset = fs.Length;
                IkDebugSeen = Contains(fs, Encoding.ASCII.GetBytes(IkTag), fromEnd ? _offset : fs.Length);
            }
            catch (IOException) { }
        }

        // Only to tell whether the launch option is on: chunked search, the logs can be tens of MB.
        static bool Contains(Stream s, byte[] needle, long limit)
        {
            var buf = new byte[1 << 20];
            long done = 0;
            int keep = 0;
            while (done < limit)
            {
                int n = s.Read(buf, keep, (int)Math.Min(buf.Length - keep, limit - done));
                if (n <= 0) break;
                done += n;
                int len = keep + n;
                if (buf.AsSpan(0, len).IndexOf(needle) >= 0) return true;
                keep = Math.Min(needle.Length - 1, len);
                Array.Copy(buf, len - keep, buf, 0, keep);
            }
            return false;
        }

        // New events since the last call.
        public List<string> Poll()
        {
            var events = new List<string>();
            string latest = NewestLog();
            if (latest != null && !string.Equals(latest, CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                Attach(latest, fromEnd: false);  // VRChat restarted: read the new log from its start
                events.Add(NewLog);
            }
            if (CurrentPath == null) return events;

            byte[] data;
            try
            {
                using var fs = OpenShared(CurrentPath);
                long size = fs.Length;
                if (size < _offset)  // truncated
                {
                    _offset = 0;
                    _partial = Array.Empty<byte>();
                }
                if (size == _offset) return events;
                fs.Seek(_offset, SeekOrigin.Begin);
                data = new byte[size - _offset];
                int read = 0;
                while (read < data.Length)
                {
                    int n = fs.Read(data, read, data.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < data.Length) Array.Resize(ref data, read);
                _offset += read;
            }
            catch (IOException) { return events; }

            var all = new byte[_partial.Length + data.Length];
            _partial.CopyTo(all, 0);
            data.CopyTo(all, _partial.Length);
            int lineStart = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != (byte)'\n') continue;
                string line = Encoding.UTF8.GetString(all, lineStart, i - lineStart);
                lineStart = i + 1;
                if (line.Contains(IkTag)) IkDebugSeen = true;
                if (line.Contains(StartLine)) events.Add(Start);
                else if (EndLines.Any(line.Contains)) events.Add(End);
            }
            _partial = all.AsSpan(lineStart).ToArray();
            return events;
        }
    }
}
