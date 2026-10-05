using Multiplayer.Components.Networking.Train;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.Networking;

public abstract class TickedQueue<T> : MonoBehaviour
{
    private const float WARNING_THRESHOLD_SECONDS = 3.0f;
    private const uint QUEUE_LENGTH_WARNING = (uint)(NetworkLifecycle.TICK_RATE * WARNING_THRESHOLD_SECONDS);
    private const uint SNAPSHOT_GAP_WARNING = (uint)(NetworkLifecycle.TICK_RATE * WARNING_THRESHOLD_SECONDS);
    // Both warnings below fire per received snapshot, and there is one queue per car per data type
    // (speed, rigidbody, each bogie). In a measured client session the gap warning alone accounted
    // for 59,418 of 100,632 log lines, 59 percent of all output. Both are symptoms of a starved or
    // desynced tick, but Unity logs synchronously on the main thread, so logging them without bound
    // becomes a cause of the stalls they report. Each now logs one in N with a running count; the
    // warning thresholds themselves are unchanged. The counters are static so the sample is one in
    // N across all queues rather than per queue: with a large fleet there are thousands of queue
    // instances, and per instance counters would still emit a first occurrence line for each one.
    private const int SUPPRESSED_LOG_INTERVAL = 1000;
    private static long queueLengthLogCount;
    private static long snapshotGapLogCount;

    private uint lastTick;
    private uint lastReceivedTick;
    private readonly Queue<(uint, T)> snapshots = new();
    protected string identifier;

    protected virtual void OnEnable()
    {
        NetworkLifecycle.Instance.OnTick += OnTick;
    }

    protected virtual void OnDisable()
    {
        if (UnloadWatcher.isQuitting)
            return;
        NetworkLifecycle.Instance.OnTick -= OnTick;
        lastTick = 0;
        snapshots.Clear();
        identifier = string.Empty;
    }

    public void ReceiveSnapshot(T snapshot, uint tick)
    {
        if (tick <= lastTick)
            return;

        if (snapshots.Count >= QUEUE_LENGTH_WARNING && queueLengthLogCount++ % SUPPRESSED_LOG_INTERVAL == 0)
            Multiplayer.LogWarning($"[{GetID()}] Snapshot queue exceeds {QUEUE_LENGTH_WARNING} items. Current size: {snapshots.Count} (occurrence #{queueLengthLogCount})");

        if (lastReceivedTick > 0 && tick - lastReceivedTick > SNAPSHOT_GAP_WARNING && snapshotGapLogCount++ % SUPPRESSED_LOG_INTERVAL == 0)
            Multiplayer.LogWarning($"[{GetID()}] Large gap between snapshots: {tick - lastReceivedTick} ticks. (occurrence #{snapshotGapLogCount})");

        lastReceivedTick = tick;
        lastTick = tick;
        snapshots.Enqueue((tick, snapshot));
    }

    private void OnTick(uint tick)
    {
        if (snapshots.Count == 0 || UnloadWatcher.isUnloading)
            return;
        while (snapshots.Count > 0)
        {
            (uint snapshotTick, T snapshot) = snapshots.Dequeue();
            Process(snapshot, snapshotTick);
        }
    }

    public void Clear()
    {
        snapshots.Clear();
    }

    protected abstract void Process(T snapshot, uint snapshotTick);

    private string GetID()
    {
        if (!string .IsNullOrEmpty(identifier))
            return identifier;

        if (this.gameObject == null)
            return "Bad GO";

        TrainCar car = TrainCar.Resolve(this.gameObject);
        int bogie = 0;

        if (car != null)
            if (this is NetworkedBogie netBogie)
                bogie = (car.Bogies[0] == netBogie.Bogie) ? 1 : 2;

        if (car?.logicCar != null)
            identifier = $"{car?.ID ?? gameObject.GetPath()}{(bogie > 0 ? $" Bogie {bogie}" : "")}";

        return identifier ?? "Unknown";
    }
}
