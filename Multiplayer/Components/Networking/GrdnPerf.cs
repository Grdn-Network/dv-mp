using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using LiteNetLib;

namespace Multiplayer.Components.Networking;

/// <summary>
/// Lightweight, settings-gated performance instrumentation.
/// Hot-path cost when disabled is a single static bool branch; when enabled,
/// counters are plain Interlocked increments and timers are Stopwatch timestamps.
/// All string formatting and file I/O happens on a dedicated background thread.
/// Workers never touch Unity state: main-thread hooks record numbers, the writer
/// thread only reads those numbers and cached plain-object references.
/// </summary>
public static class GrdnPerf
{
    public enum Counter
    {
        SetsWalked,
        CarsChecked,
        PhysicsPacketsSent,
        FullSyncSends,
        DupTickAligned,     // tick == lastTickProcessed via the aligned apply path
        DupTickFallback,    // tick == lastTickProcessed via the mismatch fallback path
        TickRegression,     // tick < lastTickProcessed (true out-of-order)
        TrainsetMismatch,
        UnknownTrainset,
        UnableToApply,
        KinematicResets,    // rb.isKinematic true->false transitions from received packets
        COUNT
    }

    public enum Section
    {
        TickTotal,
        OnTick,
        PollClient,
        PollServer,
        ServerTickSet,
        COUNT
    }

    public static bool Enabled;

    private static readonly long[] counters = new long[(int)Counter.COUNT];
    // per section: sum (us), max (us), count
    private static readonly long[] sectionSumUs = new long[(int)Section.COUNT];
    private static readonly long[] sectionMaxUs = new long[(int)Section.COUNT];
    private static readonly long[] sectionCount = new long[(int)Section.COUNT];

    private static readonly double ticksToUs = 1_000_000.0 / Stopwatch.Frequency;

    private static Thread writerThread;
    private static volatile bool writerRunning;
    private static int flushIntervalSec = 10;
    private static readonly object lifecycleLock = new();

    // cached by the main thread so the writer never touches Unity singletons
    private static NetStatistics clientStats;
    private static NetStatistics serverStats;
    private static long lastBytesSent, lastBytesReceived, lastPacketsSent, lastPacketsReceived;

    public static void Count(Counter c)
    {
        if (!Enabled)
            return;
        Interlocked.Increment(ref counters[(int)c]);
    }

    public static void Add(Counter c, long n)
    {
        if (!Enabled)
            return;
        Interlocked.Add(ref counters[(int)c], n);
    }

    /// <summary>Returns 0 when disabled; pass the result to End().</summary>
    public static long Begin()
    {
        return Enabled ? Stopwatch.GetTimestamp() : 0L;
    }

    public static void End(Section s, long beginTimestamp)
    {
        if (beginTimestamp == 0 || !Enabled)
            return;
        long us = (long)((Stopwatch.GetTimestamp() - beginTimestamp) * ticksToUs);
        int i = (int)s;
        Interlocked.Add(ref sectionSumUs[i], us);
        Interlocked.Increment(ref sectionCount[i]);
        long max;
        while (us > (max = Interlocked.Read(ref sectionMaxUs[i])))
            if (Interlocked.CompareExchange(ref sectionMaxUs[i], us, max) == max)
                break;
    }

    /// <summary>Main thread only. Caches stats refs so the writer thread never resolves Unity singletons.</summary>
    public static void CacheTransportStats(NetStatistics client, NetStatistics server)
    {
        clientStats = client;
        serverStats = server;
    }

    public static void ApplySettings(Settings settings)
    {
        lock (lifecycleLock)
        {
            flushIntervalSec = Math.Max(1, settings.PerfFlushIntervalSec);
            if (settings.EnablePerfInstrumentation && !writerRunning)
                StartWriter();
            else if (!settings.EnablePerfInstrumentation && writerRunning)
                StopWriter();
            Enabled = settings.EnablePerfInstrumentation;
        }
    }

    private static void StartWriter()
    {
        writerRunning = true;
        writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "GrdnPerfWriter" };
        writerThread.Start();
        Multiplayer.Log("[GrdnPerf] Instrumentation enabled");
    }

    private static void StopWriter()
    {
        writerRunning = false;
        writerThread?.Interrupt();
        writerThread = null;
        Multiplayer.Log("[GrdnPerf] Instrumentation disabled");
    }

    private static void WriterLoop()
    {
        StreamWriter writer = null;
        try
        {
            string dir = Path.Combine(Multiplayer.ModEntry.Path, "perf");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"perf-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            writer = new StreamWriter(file, false, Encoding.UTF8);

            var header = new StringBuilder("time");
            for (int i = 0; i < (int)Counter.COUNT; i++)
                header.Append(',').Append((Counter)i);
            for (int i = 0; i < (int)Section.COUNT; i++)
                header.Append(',').Append((Section)i).Append("AvgUs,").Append((Section)i).Append("MaxUs,").Append((Section)i).Append("Count");
            header.Append(",BytesSent,BytesReceived,PacketsSent,PacketsReceived");
            writer.WriteLine(header.ToString());
            writer.Flush();

            var sb = new StringBuilder(512);
            while (writerRunning)
            {
                Thread.Sleep(flushIntervalSec * 1000);
                if (!writerRunning)
                    break;

                sb.Length = 0;
                sb.Append(DateTime.Now.ToString("HH:mm:ss"));
                for (int i = 0; i < (int)Counter.COUNT; i++)
                    sb.Append(',').Append(Interlocked.Exchange(ref counters[i], 0));
                for (int i = 0; i < (int)Section.COUNT; i++)
                {
                    long sum = Interlocked.Exchange(ref sectionSumUs[i], 0);
                    long max = Interlocked.Exchange(ref sectionMaxUs[i], 0);
                    long cnt = Interlocked.Exchange(ref sectionCount[i], 0);
                    sb.Append(',').Append(cnt > 0 ? sum / cnt : 0).Append(',').Append(max).Append(',').Append(cnt);
                }
                AppendTransportDeltas(sb);
                writer.WriteLine(sb.ToString());
                writer.Flush();
            }
        }
        catch (ThreadInterruptedException)
        {
            // normal shutdown
        }
        catch (Exception e)
        {
            Multiplayer.LogError($"[GrdnPerf] Writer thread died: {e}");
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private static void AppendTransportDeltas(StringBuilder sb)
    {
        long bs = 0, br = 0, ps = 0, pr = 0;
        NetStatistics c = clientStats;
        NetStatistics s = serverStats;
        if (c != null) { bs += c.BytesSent; br += c.BytesReceived; ps += c.PacketsSent; pr += c.PacketsReceived; }
        if (s != null) { bs += s.BytesSent; br += s.BytesReceived; ps += s.PacketsSent; pr += s.PacketsReceived; }
        sb.Append(',').Append(bs - lastBytesSent).Append(',').Append(br - lastBytesReceived)
          .Append(',').Append(ps - lastPacketsSent).Append(',').Append(pr - lastPacketsReceived);
        lastBytesSent = bs; lastBytesReceived = br; lastPacketsSent = ps; lastPacketsReceived = pr;
    }
}
