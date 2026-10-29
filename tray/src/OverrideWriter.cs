using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace FbtCal
{
    // Writer side of the shared memory the fbtcal driver reads. Mirrors driver/src/shared_pose.h
    // (and app/shm.py) - change all of them together. The driver only uses the poses while the
    // heartbeat is younger than 250 ms, so Write() has to be called continuously while an override
    // should stay on; if this process stops, the real poses come back by themselves.
    public sealed class OverrideWriter : IDisposable
    {
        public const string Name = @"Local\fbtcal_pose_override_v1";
        public const string TestName = @"Local\fbtcal_pose_override_test";  // tests: never the live mapping
        const uint Magic = 0x4C414346;  // "FCAL"
        const uint Version = 1;
        public const int MaxEntries = 16;
        const int HeaderSize = 24, EntrySize = 64;
        const int Size = HeaderSize + MaxEntries * EntrySize;
        const int SeqOffset = 8;

        readonly MemoryMappedFile _mmf;
        readonly MemoryMappedViewAccessor _view;

        // True if the mapping already exists, i.e. SteamVR has loaded the fbtcal driver.
        // Ask before the first OverrideWriter is created (that one creates it otherwise).
        public static bool MappingExists()
        {
            try
            {
                using var m = MemoryMappedFile.OpenExisting(Name, MemoryMappedFileRights.Read);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (Exception) { return true; }  // exists, but no access: still the driver's
        }

        public OverrideWriter(string name = Name)
        {
            _mmf = MemoryMappedFile.CreateOrOpen(name, Size);
            _view = _mmf.CreateViewAccessor(0, Size);
        }

        public static ulong NowMs() => (ulong)Environment.TickCount64;  // = GetTickCount64, the driver's clock

        // Empty list = no override.
        public void Write(IReadOnlyList<(uint device, Vec3 pos, Quat rot)> entries, ulong? heartbeatMs = null)
        {
            int count = Math.Min(entries.Count, MaxEntries);
            uint seq = _view.ReadUInt32(SeqOffset);
            if ((seq & 1) != 0) seq++;
            _view.Write(SeqOffset, seq + 1);  // odd: mid-update
            Thread.MemoryBarrier();
            for (int i = 0; i < count; i++)
            {
                long o = HeaderSize + i * EntrySize;
                var (device, pos, rot) = entries[i];
                _view.Write(o, device);
                _view.Write(o + 4, 1u);
                _view.Write(o + 8, pos.X);
                _view.Write(o + 16, pos.Y);
                _view.Write(o + 24, pos.Z);
                _view.Write(o + 32, rot.W);
                _view.Write(o + 40, rot.X);
                _view.Write(o + 48, rot.Y);
                _view.Write(o + 56, rot.Z);
            }
            _view.Write(0, Magic);
            _view.Write(4, Version);
            _view.Write(12, (uint)count);
            _view.Write(16, heartbeatMs ?? NowMs());
            Thread.MemoryBarrier();
            _view.Write(SeqOffset, seq + 2);  // even: consistent
        }

        public void Clear() => Write(Array.Empty<(uint, Vec3, Quat)>(), 0);

        public void Dispose()
        {
            try { Clear(); } catch { }
            _view.Dispose();
            _mmf.Dispose();
        }
    }
}
